#include "AndroidAudioEngine.h"
#include "MobileTransport.h"

#include <android/log.h>
#include <sys/resource.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <ctime>
#include <dlfcn.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "OsmiumAudio", __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, "OsmiumAudio", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "OsmiumAudio", __VA_ARGS__)

namespace osmium::mobile
{
    namespace
    {
        constexpr size_t kMaxJitterSlots = 24;
        constexpr int kDefaultSampleRate = 48000;
        constexpr int kDefaultFrameSamples = 960; // 20ms @ 48kHz
        constexpr int kFadeInSamples = 240;       // 5ms @ 48kHz
        constexpr uint64_t kIdleKeepAliveUs = 15000000ULL; // keep AAudio warm for 15s of silence
        constexpr int kMicSampleRate = 48000;
        constexpr int kMicChannels = 1;
        constexpr int kMicFrameSamples = 960;

        inline int32_t SeqDiff(uint32_t a, uint32_t b)
        {
            return static_cast<int32_t>(a - b);
        }

        inline int64_t NowMonotonicNanos()
        {
            struct timespec ts{};
            clock_gettime(CLOCK_MONOTONIC, &ts);
            return static_cast<int64_t>(ts.tv_sec) * 1000000000LL + static_cast<int64_t>(ts.tv_nsec);
        }
    }

    AndroidAudioEngine::~AndroidAudioEngine()
    {
        StopAll();
    }

    void AndroidAudioEngine::SetMuted(bool muted)
    {
        const bool prev = m_Muted.exchange(muted, std::memory_order_acq_rel);
        if (muted && !prev)
        {
            {
                std::lock_guard<std::mutex> ql(m_QueueMutex);
                m_JitterMap.clear();
                m_HasExpectedSeq = false;
                m_Priming = true;
                m_NeedFadeIn = true;
            }
            m_PendingRingReset.store(true, std::memory_order_release);
            m_QueueCv.notify_one();
        }
        else if (!muted && prev)
        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);
            m_HasExpectedSeq = false;
            m_Priming = true;
            m_NeedFadeIn = true;
        }
    }

    void AndroidAudioEngine::NotifyVideoFrameRendered(uint64_t hostCaptureTimeUs, uint64_t clientRenderTimeUs)
    {
        if (hostCaptureTimeUs == 0 || clientRenderTimeUs == 0) return;
        const int64_t sampleOffset = static_cast<int64_t>(clientRenderTimeUs) - static_cast<int64_t>(hostCaptureTimeUs);

        std::lock_guard<std::mutex> sl(m_SyncMutex);
        if (!m_HasVideoOffset || std::llabs(sampleOffset - m_VideoPresentOffsetUs) > 400000LL)
        {
            m_VideoPresentOffsetUs = sampleOffset;
            m_HasVideoOffset = true;
        }
        else
        {
            m_VideoPresentOffsetUs = static_cast<int64_t>(
                static_cast<double>(m_VideoPresentOffsetUs) * 0.92 + static_cast<double>(sampleOffset) * 0.08);
        }
        m_LastVideoRenderUs = clientRenderTimeUs;
        if (m_HasClockOffset)
        {
            const int64_t pipe = m_VideoPresentOffsetUs - m_ClockOffsetUs;
            if (pipe >= 4000LL && pipe <= 350000LL)
            {
                m_VideoPipelineUs = static_cast<int64_t>(
                    static_cast<double>(m_VideoPipelineUs) * 0.90 + static_cast<double>(pipe) * 0.10);
            }
        }
    }

    void AndroidAudioEngine::RecordClockOffset(int64_t clockOffsetUs, uint32_t rttUs)
    {
        const uint64_t nowUs = MobileTransport::NowMicros();
        std::lock_guard<std::mutex> sl(m_SyncMutex);
        if (!m_HasClockOffset || nowUs - m_LastBestRttResetUs > 15000000ULL)
        {
            m_BestClockRttUs = rttUs;
            m_LastBestRttResetUs = nowUs;
            if (!m_HasClockOffset || std::llabs(clockOffsetUs - m_ClockOffsetUs) > 500000LL)
            {
                m_ClockOffsetUs = clockOffsetUs;
                m_HasClockOffset = true;
            }
            else
            {
                m_ClockOffsetUs = static_cast<int64_t>(
                    static_cast<double>(m_ClockOffsetUs) * 0.75 + static_cast<double>(clockOffsetUs) * 0.25);
            }
            return;
        }

        if (rttUs <= m_BestClockRttUs + 3500U)
        {
            if (rttUs < m_BestClockRttUs) m_BestClockRttUs = rttUs;
            m_ClockOffsetUs = static_cast<int64_t>(
                static_cast<double>(m_ClockOffsetUs) * 0.85 + static_cast<double>(clockOffsetUs) * 0.15);
        }
    }

    void AndroidAudioEngine::GetAudioTelemetry(
        uint16_t& outLossPermille,
        uint16_t& outJitterMs,
        uint16_t& outBufferMs) const
    {
        outLossPermille = m_TelemetryLossPermille.load(std::memory_order_relaxed);
        outJitterMs = m_TelemetryJitterMs.load(std::memory_order_relaxed);
        outBufferMs = m_TelemetryBufferMs.load(std::memory_order_relaxed);
    }

    void AndroidAudioEngine::StopAll()
    {
        StopMicCapture();

        std::thread workerToJoin;
        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);
            m_DecodeStop = true;
            m_JitterMap.clear();
            if (m_DecodeThread.joinable())
            {
                workerToJoin = std::move(m_DecodeThread);
            }
        }
        m_QueueCv.notify_all();
        if (workerToJoin.joinable())
        {
            workerToJoin.join();
        }
        StopPlayback();
    }

    void AndroidAudioEngine::StopPlayback()
    {
        if (m_PlayStream)
        {
            AAudioStream_requestStop(m_PlayStream);
            AAudioStream_close(m_PlayStream);
            m_PlayStream = nullptr;
        }
        if (m_Decoder)
        {
            opus_decoder_destroy(m_Decoder);
            m_Decoder = nullptr;
        }
        m_PlaySampleRate = 0;
        m_PlayChannels.store(2, std::memory_order_relaxed);
        m_RingReadFrame.store(0, std::memory_order_relaxed);
        m_RingWriteFrame.store(0, std::memory_order_relaxed);
        m_PendingTrimKeepFrames.store(0, std::memory_order_relaxed);
        m_StreamActiveForUnderrun.store(false, std::memory_order_relaxed);
        m_HadUnderrun.store(false, std::memory_order_relaxed);
        m_FracPos = 0.0;
        m_PrevOutSample[0] = 0.0f;
        m_PrevOutSample[1] = 0.0f;
        m_FadeAnchorSample[0] = 0.0f;
        m_FadeAnchorSample[1] = 0.0f;
        m_CallbackFadeInLeft = 0;
        m_HasRingPrev = false;
        m_InUnderrun = false;
        m_PlaybackRate.store(1.0, std::memory_order_relaxed);
        m_SmoothedRate = 1.0;
        m_NeedFadeIn = true;
    }

    bool AndroidAudioEngine::EnsurePlayback(uint16_t sampleRate, uint8_t channels)
    {
        if (sampleRate == 0) sampleRate = kDefaultSampleRate;
        if (channels == 0 || channels > 2) channels = 2;

        const bool needRebuild = m_StreamNeedsRebuild.exchange(false, std::memory_order_acq_rel);
        if (!needRebuild && m_PlayStream && m_Decoder &&
            m_PlaySampleRate == sampleRate &&
            m_PlayChannels.load(std::memory_order_relaxed) == channels)
        {
            return true;
        }

        StopPlayback();

        int err = 0;
        m_Decoder = opus_decoder_create(sampleRate, channels, &err);
        if (!m_Decoder || err != OPUS_OK)
        {
            LOGE("opus_decoder_create failed: %d", err);
            return false;
        }

        m_PcmRing.assign(static_cast<size_t>(kRingFrames) * 2, 0.0f);
        m_RingReadFrame.store(0, std::memory_order_relaxed);
        m_RingWriteFrame.store(0, std::memory_order_relaxed);
        m_FracPos = 0.0;
        m_PrevOutSample[0] = 0.0f;
        m_PrevOutSample[1] = 0.0f;
        m_PlayChannels.store(channels, std::memory_order_release);
        m_DecodeScratch.resize(5760 * channels);

        auto tryOpenStream = [&](aaudio_format_t fmt) -> bool
        {
            AAudioStreamBuilder* builder = nullptr;
            if (AAudio_createStreamBuilder(&builder) != AAUDIO_OK || !builder)
            {
                return false;
            }

            // CRITICAL: Keep AAUDIO_PERFORMANCE_MODE_NONE + AAUDIO_USAGE_MEDIA + AAUDIO_CONTENT_TYPE_MUSIC
            // to prevent OEM Game-Audio DSP/Wi-Fi coexistence from starving 2.4GHz Wi-Fi IRQs.
            AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
            AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
            AAudioStreamBuilder_setSampleRate(builder, sampleRate);
            AAudioStreamBuilder_setChannelCount(builder, channels);
            AAudioStreamBuilder_setFormat(builder, fmt);
            AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_NONE);
            using SetInt32Fn = void (*)(AAudioStreamBuilder*, int32_t);
            if (auto fn = reinterpret_cast<SetInt32Fn>(dlsym(RTLD_DEFAULT, "AAudioStreamBuilder_setUsage")))
            {
                fn(builder, AAUDIO_USAGE_MEDIA);
            }
            if (auto fn = reinterpret_cast<SetInt32Fn>(dlsym(RTLD_DEFAULT, "AAudioStreamBuilder_setContentType")))
            {
                fn(builder, AAUDIO_CONTENT_TYPE_MUSIC);
            }
            AAudioStreamBuilder_setDataCallback(builder, &AndroidAudioEngine::PlaybackCallback, this);
            AAudioStreamBuilder_setErrorCallback(builder, &AndroidAudioEngine::PlaybackErrorCallback, this);

            aaudio_result_t res = AAudioStreamBuilder_openStream(builder, &m_PlayStream);
            AAudioStreamBuilder_delete(builder);
            if (res != AAUDIO_OK || !m_PlayStream)
            {
                m_PlayStream = nullptr;
                return false;
            }
            return true;
        };

        bool useFloat = true;
        if (!tryOpenStream(AAUDIO_FORMAT_PCM_FLOAT))
        {
            useFloat = false;
            if (!tryOpenStream(AAUDIO_FORMAT_PCM_I16))
            {
                LOGE("AAudio open output stream failed for both FLOAT and I16");
                StopPlayback();
                return false;
            }
        }
        m_UseFloatOutput.store(useFloat, std::memory_order_release);

        // Set AAudio internal buffer to >=3 bursts (~50-60ms) to avoid AudioFlinger HAL
        // underruns under concurrent HW video decoding while keeping latency measurable.
        const int32_t burst = std::max<int32_t>(1, AAudioStream_getFramesPerBurst(m_PlayStream));
        const int32_t capacity = AAudioStream_getBufferCapacityInFrames(m_PlayStream);
        const int32_t targetFrames = std::clamp<int32_t>(
            std::max<int32_t>(burst * 3, static_cast<int32_t>(sampleRate) / 20),
            burst,
            std::max<int32_t>(burst, capacity));
        AAudioStream_setBufferSizeInFrames(m_PlayStream, targetFrames);

        aaudio_result_t startRes = AAudioStream_requestStart(m_PlayStream);
        if (startRes != AAUDIO_OK)
        {
            LOGE("AAudio start output stream failed: %d", startRes);
            StopPlayback();
            return false;
        }

        m_PlaySampleRate = sampleRate;
        m_NeedFadeIn = true;
        m_HwDacLatencyUs = 45000.0;
        m_LastHwTimestampQueryUs = 0;
        LOGI("AAudio output started (%u Hz, %u ch, fmt=%s, media mode, burst=%d, buf=%d)",
             sampleRate, channels, useFloat ? "F32" : "I16", burst, AAudioStream_getBufferSizeInFrames(m_PlayStream));
        return true;
    }

    void AndroidAudioEngine::PlaybackErrorCallback(
        AAudioStream* /*stream*/,
        void* userData,
        aaudio_result_t error)
    {
        auto* self = static_cast<AndroidAudioEngine*>(userData);
        LOGW("AAudio output stream error/disconnected (%d); scheduling rebuild", static_cast<int>(error));
        self->m_StreamNeedsRebuild.store(true, std::memory_order_release);
        self->m_QueueCv.notify_one();
    }

    aaudio_data_callback_result_t AndroidAudioEngine::PlaybackCallback(
        AAudioStream* /*stream*/,
        void* userData,
        void* audioData,
        int32_t numFrames)
    {
        auto* self = static_cast<AndroidAudioEngine*>(userData);
        if (numFrames <= 0) return AAUDIO_CALLBACK_RESULT_CONTINUE;

        const uint8_t ch = std::clamp<uint8_t>(self->m_PlayChannels.load(std::memory_order_acquire), 1, 2);
        const bool isFloat = self->m_UseFloatOutput.load(std::memory_order_acquire);
        auto* outF = static_cast<float*>(audioData);
        auto* outI16 = static_cast<int16_t*>(audioData);

        uint32_t r = self->m_RingReadFrame.load(std::memory_order_relaxed);
        const uint32_t w = self->m_RingWriteFrame.load(std::memory_order_acquire);

        if (self->m_PendingRingReset.exchange(false, std::memory_order_acq_rel))
        {
            r = w;
            self->m_RingReadFrame.store(r, std::memory_order_release);
            self->m_FracPos = 0.0;
            self->m_HasRingPrev = false;
            self->m_PendingTrimKeepFrames.store(0, std::memory_order_relaxed);
        }
        else
        {
            const uint32_t trimKeep = self->m_PendingTrimKeepFrames.exchange(0, std::memory_order_acq_rel);
            if (trimKeep > 0)
            {
                const uint32_t availNow = w - r;
                if (availNow > trimKeep)
                {
                    r = w - trimKeep;
                    self->m_RingReadFrame.store(r, std::memory_order_release);
                    self->m_FracPos = 0.0;
                    self->m_HasRingPrev = false;
                    self->m_FadeAnchorSample[0] = self->m_PrevOutSample[0];
                    self->m_FadeAnchorSample[1] = self->m_PrevOutSample[1];
                    self->m_CallbackFadeInLeft = 192;
                }
            }
        }

        const bool muted = self->m_Muted.load(std::memory_order_relaxed);
        const double rate = std::clamp(self->m_PlaybackRate.load(std::memory_order_relaxed), 0.96, 1.04);
        const bool ringReady = self->m_PcmRing.size() >= static_cast<size_t>(kRingFrames) * 2;

        if (muted || !ringReady)
        {
            if (muted)
            {
                self->m_RingReadFrame.store(w, std::memory_order_release);
                self->m_FracPos = 0.0;
                self->m_HasRingPrev = false;
            }
            // Smooth exponential fade-out to silence (~2ms) instead of hard-zero click!
            for (int32_t i = 0; i < numFrames; ++i)
            {
                for (uint8_t c = 0; c < ch; ++c)
                {
                    float s = self->m_PrevOutSample[c] * 0.965f;
                    if (std::fabs(s) < 1e-6f) s = 0.0f;
                    self->m_PrevOutSample[c] = s;
                    if (isFloat)
                    {
                        outF[i * ch + c] = s;
                    }
                    else
                    {
                        float cl = std::clamp(s, -1.0f, 1.0f);
                        outI16[i * ch + c] = static_cast<int16_t>(std::lrintf(cl * 32767.0f));
                    }
                }
            }
            return AAUDIO_CALLBACK_RESULT_CONTINUE;
        }

        const float* ring = self->m_PcmRing.data();
        double frac = self->m_FracPos;
        const bool rateIsUnity = (std::fabs(rate - 1.0) < 1e-6);

        for (int32_t i = 0; i < numFrames; ++i)
        {
            const uint32_t avail = w - r;
            if (avail >= 1)
            {
                if (self->m_InUnderrun)
                {
                    self->m_InUnderrun = false;
                    self->m_FadeAnchorSample[0] = self->m_PrevOutSample[0];
                    self->m_FadeAnchorSample[1] = self->m_PrevOutSample[1];
                    self->m_CallbackFadeInLeft = 192;
                }

                float sampleOut[2]{0.0f, 0.0f};
                if ((rateIsUnity && frac < 1e-6) || avail == 1)
                {
                    const uint32_t idx0 = (r & kRingFrameMask) * 2U;
                    for (uint8_t c = 0; c < ch; ++c)
                    {
                        sampleOut[c] = ring[idx0 + c];
                    }
                    r += 1U;
                    frac = 0.0;
                    self->m_HasRingPrev = true;
                }
                else if (self->m_HasRingPrev && avail >= 3)
                {
                    // 4-point Catmull-Rom cubic Hermite interpolation eliminates high-frequency shimmer
                    const uint32_t idxM1 = ((r - 1U) & kRingFrameMask) * 2U;
                    const uint32_t idx0 = (r & kRingFrameMask) * 2U;
                    const uint32_t idx1 = ((r + 1U) & kRingFrameMask) * 2U;
                    const uint32_t idx2 = ((r + 2U) & kRingFrameMask) * 2U;
                    const float t = static_cast<float>(frac);
                    const float t2 = t * t;
                    const float t3 = t2 * t;
                    for (uint8_t c = 0; c < ch; ++c)
                    {
                        const float y0 = ring[idxM1 + c];
                        const float y1 = ring[idx0 + c];
                        const float y2 = ring[idx1 + c];
                        const float y3 = ring[idx2 + c];
                        const float s = 0.5f * (
                            (2.0f * y1) +
                            (-y0 + y2) * t +
                            (2.0f * y0 - 5.0f * y1 + 4.0f * y2 - y3) * t2 +
                            (-y0 + 3.0f * y1 - 3.0f * y2 + y3) * t3);
                        sampleOut[c] = std::clamp(s, -1.0f, 1.0f);
                    }
                    frac += rate;
                    const uint32_t step = static_cast<uint32_t>(frac);
                    if (step > 0)
                    {
                        frac -= static_cast<double>(step);
                        const uint32_t maxStep = avail - 1U;
                        r += (step <= maxStep) ? step : maxStep;
                    }
                }
                else
                {
                    const uint32_t idx0 = (r & kRingFrameMask) * 2U;
                    const uint32_t idx1 = ((r + 1U) & kRingFrameMask) * 2U;
                    const float f = static_cast<float>(frac);
                    for (uint8_t c = 0; c < ch; ++c)
                    {
                        const float s0 = ring[idx0 + c];
                        const float s1 = ring[idx1 + c];
                        sampleOut[c] = s0 + (s1 - s0) * f;
                    }
                    frac += rate;
                    const uint32_t step = static_cast<uint32_t>(frac);
                    if (step > 0)
                    {
                        frac -= static_cast<double>(step);
                        const uint32_t maxStep = avail - 1U;
                        r += (step <= maxStep) ? step : maxStep;
                        self->m_HasRingPrev = true;
                    }
                }

                const bool applyFade = (self->m_CallbackFadeInLeft > 0);
                const float alpha = applyFade
                    ? (1.0f - static_cast<float>(self->m_CallbackFadeInLeft) / 192.0f)
                    : 1.0f;
                if (applyFade)
                {
                    self->m_CallbackFadeInLeft--;
                }

                for (uint8_t c = 0; c < ch; ++c)
                {
                    const float s = applyFade
                        ? (self->m_FadeAnchorSample[c] * (1.0f - alpha) + sampleOut[c] * alpha)
                        : sampleOut[c];
                    self->m_PrevOutSample[c] = s;
                    if (isFloat)
                    {
                        outF[i * ch + c] = s;
                    }
                    else
                    {
                        float cl = std::clamp(s, -1.0f, 1.0f);
                        outI16[i * ch + c] = static_cast<int16_t>(std::lrintf(cl * 32767.0f));
                    }
                }
            }
            else
            {
                // Ring underrun: smooth exponential decay from last sample -> ZERO electronic POP!
                if (!self->m_InUnderrun)
                {
                    self->m_InUnderrun = true;
                    self->m_HasRingPrev = false;
                    if (self->m_StreamActiveForUnderrun.load(std::memory_order_relaxed))
                    {
                        self->m_HadUnderrun.store(true, std::memory_order_release);
                    }
                }
                for (uint8_t c = 0; c < ch; ++c)
                {
                    float s = self->m_PrevOutSample[c] * 0.965f;
                    if (std::fabs(s) < 1e-6f) s = 0.0f;
                    self->m_PrevOutSample[c] = s;
                    if (isFloat)
                    {
                        outF[i * ch + c] = s;
                    }
                    else
                    {
                        float cl = std::clamp(s, -1.0f, 1.0f);
                        outI16[i * ch + c] = static_cast<int16_t>(std::lrintf(cl * 32767.0f));
                    }
                }
                frac = 0.0;
            }
        }

        self->m_FracPos = frac;
        self->m_RingReadFrame.store(r, std::memory_order_release);
        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }

    int AndroidAudioEngine::ComputeMinSafeRingMsLocked() const
    {
        const double jitterMs = m_JitterUs / 1000.0;
        const double peakMs = m_PeakExcursionUs / 1000.0;
        return std::clamp(
            static_cast<int>(std::ceil(std::max(jitterMs * 3.0 + 24.0, peakMs * 0.90 + 26.0))),
            42,
            115);
    }

    int AndroidAudioEngine::ComputeTargetPrimeMsLocked() const
    {
        return std::clamp(ComputeMinSafeRingMsLocked() + 10, 48, 120);
    }

    int AndroidAudioEngine::ComputeMaxFeedRingMsLocked() const
    {
        return std::max(95, ComputeMinSafeRingMsLocked() + 50);
    }

    void AndroidAudioEngine::EnsureDecodeThreadLocked()
    {
        if (!m_DecodeThread.joinable())
        {
            m_DecodeStop = false;
            m_StatsWindowStartMs = MobileTransport::NowMillis();
            m_DecodeThread = std::thread(&AndroidAudioEngine::DecodeWorkerLoop, this);
        }
    }

    void AndroidAudioEngine::PushOpusFrame(
        const uint8_t* data,
        uint16_t bytes,
        uint16_t sampleRate,
        uint8_t channels,
        uint16_t sampleCount,
        uint32_t sequence,
        uint64_t captureTimeUs,
        uint8_t flags,
        const uint8_t* redundantData,
        uint16_t redundantBytes)
    {
        if (!data || bytes == 0 || bytes > 4096) return;
        if (m_Muted.load(std::memory_order_relaxed)) return;

        const uint64_t nowUs = MobileTransport::NowMicros();
        const uint16_t sr = sampleRate > 0 ? sampleRate : kDefaultSampleRate;
        const uint8_t ch = (channels == 1 || channels == 2) ? channels : 2;
        const uint16_t sc = sampleCount > 0 ? sampleCount : kDefaultFrameSamples;

        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);
            EnsureDecodeThreadLocked();
            m_LastPacketReceivedUs = nowUs;

            // RFC3550 inter-arrival jitter + peak late-arrival burst excursion estimator
            if (m_HasLastArrival && captureTimeUs > m_LastCaptureUs &&
                (flags & osmium::proto::kAudioFlagStreamStart) == 0)
            {
                const int64_t dArrival = static_cast<int64_t>(nowUs - m_LastArrivalUs);
                const int64_t dCapture = static_cast<int64_t>(captureTimeUs - m_LastCaptureUs);
                if (dCapture > 0 && dCapture < 250000LL)
                {
                    const int64_t lateUs = dArrival - dCapture;
                    const double err = std::fabs(static_cast<double>(lateUs));
                    m_JitterUs += (std::min(err, 150000.0) - m_JitterUs) / 16.0;
                    if (lateUs > 0)
                    {
                        const double clampedLate = std::min(static_cast<double>(lateUs), 160000.0);
                        if (clampedLate > m_PeakExcursionUs)
                        {
                            m_PeakExcursionUs = m_PeakExcursionUs * 0.35 + clampedLate * 0.65;
                        }
                        else
                        {
                            m_PeakExcursionUs *= 0.9985;
                        }
                    }
                    else
                    {
                        m_PeakExcursionUs *= 0.9985;
                    }
                    m_AdaptiveMinRingUs.store(
                        static_cast<int64_t>(ComputeMinSafeRingMsLocked()) * 1000LL,
                        std::memory_order_relaxed);
                    m_TelemetryJitterMs.store(
                        static_cast<uint16_t>(std::clamp(m_JitterUs / 1000.0, 0.0, 500.0)),
                        std::memory_order_relaxed);
                }
            }
            m_HasLastArrival = true;
            m_LastArrivalUs = nowUs;
            m_LastCaptureUs = captureTimeUs;

            const bool isStreamStart = (flags & osmium::proto::kAudioFlagStreamStart) != 0;
            if (isStreamStart)
            {
                if (!m_HasExpectedSeq || SeqDiff(sequence, m_ExpectedSeq) >= 0)
                {
                    m_JitterMap.clear();
                    m_ExpectedSeq = sequence;
                    m_HasExpectedSeq = true;
                    m_Priming = true;
                    m_FirstArrivalUs = nowUs;
                    m_ConsecutivePlc = 0;
                    m_NeedFadeIn = true;
                }
            }
            else if (!m_HasExpectedSeq)
            {
                m_ExpectedSeq = sequence;
                m_HasExpectedSeq = true;
                m_Priming = true;
                m_FirstArrivalUs = nowUs;
            }
            else if (SeqDiff(sequence, m_ExpectedSeq) < -4)
            {
                if (SeqDiff(sequence, m_ExpectedSeq) < -200)
                {
                    m_JitterMap.clear();
                    m_ExpectedSeq = sequence;
                    m_Priming = true;
                    m_FirstArrivalUs = nowUs;
                    m_NeedFadeIn = true;
                }
                else
                {
                    return;
                }
            }

            // Bit-exact redundant previous-frame recovery if (sequence - 1) was lost!
            if (redundantData && redundantBytes > 0 && redundantBytes <= 4096 && sequence > 1 && !isStreamStart)
            {
                const uint32_t prevSeq = sequence - 1;
                if (SeqDiff(prevSeq, m_ExpectedSeq) >= 0 && m_JitterMap.find(prevSeq) == m_JitterMap.end())
                {
                    JitterSlot prevSlot;
                    prevSlot.sequence = prevSeq;
                    prevSlot.captureTimeUs = (captureTimeUs > 20000ULL) ? (captureTimeUs - 20000ULL) : captureTimeUs;
                    prevSlot.arrivalUs = nowUs;
                    prevSlot.sampleRate = sr;
                    prevSlot.channels = ch;
                    prevSlot.sampleCount = sc;
                    prevSlot.flags = 0;
                    prevSlot.data.assign(redundantData, redundantData + redundantBytes);
                    m_JitterMap.emplace(prevSeq, std::move(prevSlot));
                }
            }

            if (SeqDiff(sequence, m_ExpectedSeq) >= 0 && m_JitterMap.find(sequence) == m_JitterMap.end())
            {
                JitterSlot slot;
                slot.sequence = sequence;
                slot.captureTimeUs = captureTimeUs;
                slot.arrivalUs = nowUs;
                slot.sampleRate = sr;
                slot.channels = ch;
                slot.sampleCount = sc;
                slot.flags = flags;
                slot.data.assign(data, data + bytes);
                m_JitterMap.emplace(sequence, std::move(slot));
            }

            while (m_JitterMap.size() > kMaxJitterSlots)
            {
                auto it = m_JitterMap.begin();
                m_ExpectedSeq = it->first + 1;
                m_JitterMap.erase(it);
            }
        }
        m_QueueCv.notify_one();
    }

    void AndroidAudioEngine::ApplyFadeIn(float* pcm, int sampleCount, uint8_t ch)
    {
        const int ramp = std::min(sampleCount, kFadeInSamples);
        for (int i = 0; i < ramp; ++i)
        {
            const float g = static_cast<float>(i) / static_cast<float>(ramp);
            for (uint8_t c = 0; c < ch; ++c)
            {
                pcm[i * ch + c] *= g;
            }
        }
    }

    void AndroidAudioEngine::ApplySoftLimiter(float* pcm, int totalFloats)
    {
        for (int i = 0; i < totalFloats; ++i)
        {
            const float x = pcm[i];
            const float ax = std::fabs(x);
            if (ax > 0.94f)
            {
                const float ex = ax - 0.94f;
                const float y = 0.94f + 0.055f * std::tanh(ex / 0.055f);
                pcm[i] = (x < 0.0f) ? -y : y;
            }
        }
    }

    void AndroidAudioEngine::WriteFloatFrames(const float* samples, uint32_t frameCount, uint8_t ch)
    {
        if (!samples || frameCount == 0 || m_PcmRing.size() < static_cast<size_t>(kRingFrames) * 2) return;

        const uint32_t r = m_RingReadFrame.load(std::memory_order_acquire);
        const uint32_t w = m_RingWriteFrame.load(std::memory_order_relaxed);
        const uint32_t used = w - r;
        const uint32_t rate = m_PlaySampleRate > 0 ? m_PlaySampleRate : kDefaultSampleRate;

        // Hard safety cap (~450ms) only for extreme runaway; normal drift is handled smoothly by m_PlaybackRate!
        const uint32_t hardMaxFrames = std::min<uint32_t>(kRingFrames - 4, (rate * 450U) / 1000U);
        if (used + frameCount > hardMaxFrames)
        {
            return;
        }

        float* ring = m_PcmRing.data();
        if (ch == 2)
        {
            const uint32_t writeIdx = w & kRingFrameMask;
            const uint32_t firstFrames = std::min(frameCount, kRingFrames - writeIdx);
            std::memcpy(ring + static_cast<size_t>(writeIdx) * 2, samples,
                        static_cast<size_t>(firstFrames) * 2 * sizeof(float));
            if (frameCount > firstFrames)
            {
                std::memcpy(ring, samples + static_cast<size_t>(firstFrames) * 2,
                            static_cast<size_t>(frameCount - firstFrames) * 2 * sizeof(float));
            }
        }
        else
        {
            for (uint32_t i = 0; i < frameCount; ++i)
            {
                const uint32_t dst = ((w + i) & kRingFrameMask) * 2U;
                ring[dst] = samples[i];
                ring[dst + 1U] = samples[i];
            }
        }
        m_RingWriteFrame.store(w + frameCount, std::memory_order_release);
    }

    void AndroidAudioEngine::UpdateHardwareLatencyEstimate()
    {
        if (!m_PlayStream || m_PlaySampleRate == 0) return;
        const uint64_t nowUs = MobileTransport::NowMicros();
        if (nowUs - m_LastHwTimestampQueryUs < 250000ULL) return;
        m_LastHwTimestampQueryUs = nowUs;

        int64_t framePosition = 0;
        int64_t timeNanoseconds = 0;
        if (AAudioStream_getTimestamp(m_PlayStream, CLOCK_MONOTONIC, &framePosition, &timeNanoseconds) == AAUDIO_OK)
        {
            const int64_t framesWritten = AAudioStream_getFramesWritten(m_PlayStream);
            const int64_t nowNs = NowMonotonicNanos();
            const int64_t pendingFrames = framesWritten - framePosition;
            if (pendingFrames >= 0 && pendingFrames < static_cast<int64_t>(m_PlaySampleRate) * 2)
            {
                const int64_t deltaTimeUs = (nowNs - timeNanoseconds) / 1000LL;
                const double estUs =
                    (static_cast<double>(pendingFrames) * 1000000.0) / static_cast<double>(m_PlaySampleRate) -
                    static_cast<double>(deltaTimeUs);
                if (estUs >= 10000.0 && estUs <= 350000.0)
                {
                    m_HwDacLatencyUs = m_HwDacLatencyUs * 0.80 + estUs * 0.20;
                }
            }
        }
    }

    void AndroidAudioEngine::UpdateAvSyncRate(uint64_t audioCaptureTimeUs, uint32_t ringFramesAvail)
    {
        if (audioCaptureTimeUs == 0 || m_PlaySampleRate == 0) return;
        UpdateHardwareLatencyEstimate();

        const uint64_t nowUs = MobileTransport::NowMicros();
        const int64_t ringBufferedUs =
            (static_cast<int64_t>(ringFramesAvail) * 1000000LL) / static_cast<int64_t>(m_PlaySampleRate);
        const int64_t minSafeRingUs = std::clamp<int64_t>(
            m_AdaptiveMinRingUs.load(std::memory_order_relaxed), 42000LL, 115000LL);
        const int64_t targetRingUs = minSafeRingUs + 15000LL;

        int64_t targetOffsetUs = 0;
        bool haveSyncRef = false;
        {
            std::lock_guard<std::mutex> sl(m_SyncMutex);
            const bool videoRecent = m_HasVideoOffset && (nowUs - m_LastVideoRenderUs) < 2500000ULL;
            if (videoRecent)
            {
                targetOffsetUs = m_VideoPresentOffsetUs;
                haveSyncRef = true;
            }
            else if (m_HasClockOffset)
            {
                targetOffsetUs = m_ClockOffsetUs + m_VideoPipelineUs;
                haveSyncRef = true;
            }
        }

        double desiredRate = 1.0;
        if (ringBufferedUs < minSafeRingUs)
        {
            // CRITICAL: Never speed up below the adaptive safety floor! On Android Media-mode
            // AAudio/Bluetooth, m_HwDacLatencyUs (60-250ms) exceeds LAN video latency (~15ms),
            // so speeding up when ring is low CANNOT make audio earlier — it only starves the
            // ring into endless underruns. Gently slow down to silently rebuild cushion.
            m_HighErrorStreak = 0;
            const int64_t deficitUs = minSafeRingUs - ringBufferedUs;
            desiredRate = std::clamp(1.0 - static_cast<double>(deficitUs) / 3000000.0, 0.988, 0.997);
        }
        else if (haveSyncRef)
        {
            const int64_t actualPlayLocalUs =
                static_cast<int64_t>(nowUs) + ringBufferedUs + static_cast<int64_t>(m_HwDacLatencyUs);
            const int64_t targetPlayLocalUs = static_cast<int64_t>(audioCaptureTimeUs) + targetOffsetUs;
            const int64_t avErrorUs = actualPlayLocalUs - targetPlayLocalUs;

            if (avErrorUs > 0)
            {
                // Only drain excess ring buffer above minSafeRingUs; never eat into the safety floor!
                const int64_t excessRingUs = ringBufferedUs - minSafeRingUs;
                const int64_t effectiveCatchupUs = std::min(avErrorUs, excessRingUs);
                if (effectiveCatchupUs <= 15000LL)
                {
                    m_HighErrorStreak = 0;
                    desiredRate = 1.0;
                }
                else if (ringBufferedUs <= targetRingUs + 120000LL)
                {
                    m_HighErrorStreak = 0;
                    // Pitch-imperceptible continuous resampling (+1.2% max)
                    desiredRate = std::clamp(
                        1.0 + static_cast<double>(effectiveCatchupUs - 15000LL) / 4000000.0,
                        1.0,
                        1.012);
                }
                else
                {
                    desiredRate = std::clamp(
                        1.0 + static_cast<double>(effectiveCatchupUs) / 3000000.0,
                        1.005,
                        1.022);
                    // Only trim on genuine excessive ring backlog (>280ms), NEVER due to large HW DAC latency!
                    if (ringBufferedUs > 280000LL)
                    {
                        if (++m_HighErrorStreak >= 8)
                        {
                            m_HighErrorStreak = 0;
                            const uint32_t keepFrames = static_cast<uint32_t>(
                                (targetRingUs * static_cast<int64_t>(m_PlaySampleRate)) / 1000000LL);
                            m_PendingTrimKeepFrames.store(std::max<uint32_t>(keepFrames, 960U), std::memory_order_release);
                            desiredRate = 1.0;
                        }
                    }
                    else
                    {
                        m_HighErrorStreak = 0;
                    }
                }
            }
            else
            {
                // Audio is earlier than video (e.g., high video pipeline latency on WAN/4K):
                // gently slow down to lock lip-sync with video while keeping ring bounded (<240ms).
                m_HighErrorStreak = 0;
                const int64_t earlyUs = -avErrorUs;
                if (earlyUs <= 15000LL || ringBufferedUs >= 240000LL)
                {
                    desiredRate = 1.0;
                }
                else
                {
                    desiredRate = std::clamp(
                        1.0 - static_cast<double>(earlyUs - 15000LL) / 4000000.0,
                        0.988,
                        1.0);
                }
            }
        }
        else
        {
            // Fallback gentle buffer-depth stabilization around adaptive target before clock lock
            m_HighErrorStreak = 0;
            const int64_t bufErrUs = ringBufferedUs - targetRingUs;
            if (std::llabs(bufErrUs) > 15000LL)
            {
                desiredRate = std::clamp(1.0 + static_cast<double>(bufErrUs) / 4000000.0, 0.990, 1.010);
            }
        }

        m_SmoothedRate = m_SmoothedRate * 0.85 + desiredRate * 0.15;
        if (std::fabs(m_SmoothedRate - 1.0) < 0.0006 && std::fabs(desiredRate - 1.0) < 1e-6)
        {
            m_SmoothedRate = 1.0;
        }
        m_PlaybackRate.store(m_SmoothedRate, std::memory_order_relaxed);
    }

    void AndroidAudioEngine::DecodeSlot(const JitterSlot& slot)
    {
        if (slot.data.empty() || m_Muted.load(std::memory_order_relaxed)) return;
        if (!EnsurePlayback(slot.sampleRate, slot.channels)) return;

        const uint8_t ch = m_PlayChannels.load(std::memory_order_relaxed);
        const int maxPerCh = static_cast<int>(m_DecodeScratch.size() / std::max<uint8_t>(1, ch));
        const uint32_t ringAvail =
            m_RingWriteFrame.load(std::memory_order_relaxed) - m_RingReadFrame.load(std::memory_order_acquire);

        UpdateAvSyncRate(slot.captureTimeUs, ringAvail);

        int decodedFrames = opus_decode_float(
            m_Decoder,
            slot.data.data(),
            static_cast<int>(slot.data.size()),
            m_DecodeScratch.data(),
            maxPerCh,
            0);
        if (decodedFrames <= 0) return;

        if (m_NeedFadeIn || (slot.flags & osmium::proto::kAudioFlagStreamStart) != 0)
        {
            ApplyFadeIn(m_DecodeScratch.data(), decodedFrames, ch);
            m_NeedFadeIn = false;
        }
        ApplySoftLimiter(m_DecodeScratch.data(), decodedFrames * ch);
        WriteFloatFrames(m_DecodeScratch.data(), static_cast<uint32_t>(decodedFrames), ch);
        m_ConsecutivePlc = 0;
    }

    void AndroidAudioEngine::ConcealMissingFrame()
    {
        if (!m_PlayStream || !m_Decoder || m_Muted.load(std::memory_order_relaxed)) return;
        if (m_ConsecutivePlc >= 3)
        {
            m_NeedFadeIn = true;
            return;
        }

        const uint8_t ch = m_PlayChannels.load(std::memory_order_relaxed);
        const int plcFrames = kDefaultFrameSamples;
        if (static_cast<int>(m_DecodeScratch.size()) < plcFrames * ch)
        {
            m_DecodeScratch.resize(static_cast<size_t>(plcFrames) * ch);
        }

        // True Opus PLC (nullptr, 0)
        int decoded = opus_decode_float(m_Decoder, nullptr, 0, m_DecodeScratch.data(), plcFrames, 0);
        if (decoded > 0)
        {
            if (m_ConsecutivePlc >= 1)
            {
                const float startGain = (m_ConsecutivePlc == 1) ? 0.85f : 0.45f;
                const float endGain = (m_ConsecutivePlc == 1) ? 0.45f : 0.0f;
                for (int i = 0; i < decoded; ++i)
                {
                    const float g = startGain + (endGain - startGain) * (static_cast<float>(i) / static_cast<float>(decoded));
                    for (uint8_t c = 0; c < ch; ++c)
                    {
                        m_DecodeScratch[i * ch + c] *= g;
                    }
                }
            }
            ApplySoftLimiter(m_DecodeScratch.data(), decoded * ch);
            WriteFloatFrames(m_DecodeScratch.data(), static_cast<uint32_t>(decoded), ch);
        }
        m_ConsecutivePlc++;
    }

    void AndroidAudioEngine::DecodeWorkerLoop()
    {
        // Keep audio decode strictly below video RecvLoop (-10) and DecodeLoop (-6).
        setpriority(PRIO_PROCESS, 0, -2);

        for (;;)
        {
            JitterSlot toDecode;
            bool doDecode = false;
            bool doPlc = false;
            bool shouldCloseIdle = false;

            {
                std::unique_lock<std::mutex> ql(m_QueueMutex);
                const uint32_t srWait = m_PlaySampleRate > 0 ? m_PlaySampleRate : kDefaultSampleRate;
                auto canProcessNowLocked = [this, srWait]() -> bool
                {
                    if (m_DecodeStop ||
                        m_Muted.load(std::memory_order_acquire) ||
                        m_StreamNeedsRebuild.load(std::memory_order_acquire))
                    {
                        return true;
                    }
                    if (!m_HasExpectedSeq)
                    {
                        return !m_JitterMap.empty();
                    }
                    if (m_Priming)
                    {
                        const int targetPrimeMs = ComputeTargetPrimeMsLocked();
                        const int neededFrames = std::clamp((targetPrimeMs + 19) / 20, 2, 6);
                        return static_cast<int>(m_JitterMap.size()) >= neededFrames;
                    }
                    if (m_JitterMap.find(m_ExpectedSeq) != m_JitterMap.end())
                    {
                        const uint32_t rf =
                            m_RingWriteFrame.load(std::memory_order_relaxed) -
                            m_RingReadFrame.load(std::memory_order_acquire);
                        const int rms = static_cast<int>((static_cast<uint64_t>(rf) * 1000ULL) / srWait);
                        return (rms <= ComputeMaxFeedRingMsLocked()) || (m_JitterMap.size() > 4);
                    }
                    if (!m_JitterMap.empty())
                    {
                        const int32_t gap = SeqDiff(m_JitterMap.begin()->first, m_ExpectedSeq);
                        if (gap <= 0 || gap > 6) return true;
                    }
                    return false;
                };

                if (!m_HasExpectedSeq && m_JitterMap.empty())
                {
                    m_QueueCv.wait_for(ql, std::chrono::milliseconds(200), canProcessNowLocked);
                }
                else
                {
                    m_QueueCv.wait_for(ql, std::chrono::milliseconds(5), canProcessNowLocked);
                }

                if (m_DecodeStop) break;

                if (m_HadUnderrun.exchange(false, std::memory_order_acq_rel))
                {
                    // Self-heal: bump peak-excursion cushion on any real mid-stream underrun
                    m_PeakExcursionUs = std::min(160000.0, m_PeakExcursionUs + 25000.0);
                    m_AdaptiveMinRingUs.store(
                        static_cast<int64_t>(ComputeMinSafeRingMsLocked()) * 1000LL,
                        std::memory_order_relaxed);
                    m_NeedFadeIn = true;
                }

                if (m_Muted.load(std::memory_order_acquire))
                {
                    m_StreamActiveForUnderrun.store(false, std::memory_order_relaxed);
                    shouldCloseIdle = (m_PlayStream != nullptr);
                }
                else if (m_StreamNeedsRebuild.load(std::memory_order_acquire) && m_JitterMap.empty())
                {
                    m_StreamActiveForUnderrun.store(false, std::memory_order_relaxed);
                    shouldCloseIdle = true;
                }
                else
                {
                    const uint64_t nowUs = MobileTransport::NowMicros();
                    const uint64_t nowMs = nowUs / 1000ULL;
                    const uint32_t sr = m_PlaySampleRate > 0 ? m_PlaySampleRate : kDefaultSampleRate;
                    const uint32_t ringFrames =
                        m_RingWriteFrame.load(std::memory_order_relaxed) -
                        m_RingReadFrame.load(std::memory_order_acquire);
                    const int ringMs = static_cast<int>((static_cast<uint64_t>(ringFrames) * 1000ULL) / sr);

                    m_TelemetryBufferMs.store(
                        static_cast<uint16_t>(std::clamp(ringMs + static_cast<int>(m_JitterMap.size()) * 20, 0, 500)),
                        std::memory_order_relaxed);

                    if (nowMs - m_StatsWindowStartMs >= 1000ULL)
                    {
                        if (m_StatsExpected > 0)
                        {
                            uint32_t permille = (m_StatsLost * 1000U) / m_StatsExpected;
                            m_TelemetryLossPermille.store(
                                static_cast<uint16_t>(std::min<uint32_t>(permille, 1000U)),
                                std::memory_order_relaxed);
                        }
                        else
                        {
                            m_TelemetryLossPermille.store(0, std::memory_order_relaxed);
                        }
                        m_StatsExpected = 0;
                        m_StatsLost = 0;
                        m_StatsWindowStartMs = nowMs;
                    }

                    if (!m_HasExpectedSeq)
                    {
                        m_StreamActiveForUnderrun.store(false, std::memory_order_relaxed);
                        if (m_PlayStream && m_LastPacketReceivedUs > 0 &&
                            (nowUs - m_LastPacketReceivedUs) >= kIdleKeepAliveUs)
                        {
                            shouldCloseIdle = true;
                        }
                    }
                    else
                    {
                        const int targetPrimeMs = ComputeTargetPrimeMsLocked();
                        const int neededFrames = std::clamp((targetPrimeMs + 19) / 20, 2, 6);
                        const int maxFeedRingMs = ComputeMaxFeedRingMsLocked();

                        if (m_Priming)
                        {
                            m_StreamActiveForUnderrun.store(false, std::memory_order_relaxed);
                            if (static_cast<int>(m_JitterMap.size()) >= neededFrames ||
                                (!m_JitterMap.empty() &&
                                 (nowUs - m_FirstArrivalUs) >= static_cast<uint64_t>(targetPrimeMs) * 1000ULL))
                            {
                                m_Priming = false;
                                m_EmptyBridgePlc = 0;
                                if (!m_JitterMap.empty())
                                {
                                    m_ExpectedSeq = m_JitterMap.begin()->first;
                                }
                            }
                        }

                        if (!m_Priming)
                        {
                            const bool recentlyReceiving =
                                m_LastPacketReceivedUs > 0 && (nowUs - m_LastPacketReceivedUs) < 180000ULL;
                            m_StreamActiveForUnderrun.store(recentlyReceiving, std::memory_order_relaxed);

                            auto exactIt = m_JitterMap.find(m_ExpectedSeq);
                            if (exactIt != m_JitterMap.end())
                            {
                                if (ringMs <= maxFeedRingMs || m_JitterMap.size() > 4)
                                {
                                    toDecode = std::move(exactIt->second);
                                    m_JitterMap.erase(exactIt);
                                    m_ExpectedSeq++;
                                    m_LastFramePopUs = nowUs;
                                    m_EmptyBridgePlc = 0;
                                    m_StatsExpected++;
                                    doDecode = true;
                                    if ((toDecode.flags & osmium::proto::kAudioFlagSilenceEnd) != 0)
                                    {
                                        m_HasExpectedSeq = false;
                                        m_Priming = true;
                                        m_StreamActiveForUnderrun.store(false, std::memory_order_relaxed);
                                    }
                                }
                            }
                            else if (!m_JitterMap.empty())
                            {
                                const uint32_t oldestSeq = m_JitterMap.begin()->first;
                                const int32_t gap = SeqDiff(oldestSeq, m_ExpectedSeq);
                                if (gap <= 0)
                                {
                                    m_JitterMap.erase(m_JitterMap.begin());
                                }
                                else if (gap > 6)
                                {
                                    m_StatsExpected += static_cast<uint32_t>(std::min<int32_t>(gap, 25));
                                    m_StatsLost += static_cast<uint32_t>(std::min<int32_t>(gap, 25));
                                    m_ExpectedSeq = oldestSeq;
                                    m_EmptyBridgePlc = 0;
                                    m_NeedFadeIn = true;
                                }
                                else
                                {
                                    const uint64_t waitUs = nowUs - m_LastFramePopUs;
                                    const uint64_t gapBudgetUs = static_cast<uint64_t>(
                                        std::clamp(m_JitterUs * 2.5 + 12000.0, 26000.0, 55000.0));
                                    if (ringMs < 20 || waitUs >= gapBudgetUs)
                                    {
                                        m_ExpectedSeq++;
                                        m_LastFramePopUs = nowUs;
                                        m_StatsExpected++;
                                        m_StatsLost++;
                                        doPlc = true;
                                    }
                                }
                            }
                            else
                            {
                                // Jitter map empty (Wi-Fi stall or talkspurt pause):
                                // NEVER advance m_ExpectedSeq or discard late packets before a newer seq arrives!
                                if (m_LastFramePopUs > 0 && (nowUs - m_LastFramePopUs) > 350000ULL)
                                {
                                    m_HasExpectedSeq = false;
                                    m_Priming = true;
                                    m_ConsecutivePlc = 0;
                                    m_EmptyBridgePlc = 0;
                                    m_NeedFadeIn = true;
                                    m_StreamActiveForUnderrun.store(false, std::memory_order_relaxed);
                                }
                                else if (ringMs < 12 &&
                                         m_EmptyBridgePlc < 2 &&
                                         m_LastFramePopUs > 0 &&
                                         (nowUs - m_LastFramePopUs) >= 34000ULL)
                                {
                                    m_EmptyBridgePlc++;
                                    m_LastFramePopUs = nowUs;
                                    doPlc = true;
                                }
                            }
                        }
                    }
                }
            }

            if (shouldCloseIdle)
            {
                m_StreamNeedsRebuild.store(false, std::memory_order_release);
                StopPlayback();
            }
            else if (doDecode)
            {
                DecodeSlot(toDecode);
            }
            else if (doPlc)
            {
                ConcealMissingFrame();
            }
        }
    }

    bool AndroidAudioEngine::StartMicCapture(MicFrameCallback callback)
    {
        std::lock_guard<std::mutex> lock(m_MicMutex);
        if (m_MicStream) return true;

        int err = 0;
        m_Encoder = opus_encoder_create(kMicSampleRate, kMicChannels, OPUS_APPLICATION_VOIP, &err);
        if (!m_Encoder || err != OPUS_OK)
        {
            LOGE("opus_encoder_create failed: %d", err);
            return false;
        }
        opus_encoder_ctl(m_Encoder, OPUS_SET_BITRATE(32000));
        opus_encoder_ctl(m_Encoder, OPUS_SET_COMPLEXITY(5));
        opus_encoder_ctl(m_Encoder, OPUS_SET_INBAND_FEC(1));
        opus_encoder_ctl(m_Encoder, OPUS_SET_PACKET_LOSS_PERC(5));
        opus_encoder_ctl(m_Encoder, OPUS_SET_DTX(1));

        m_MicCb = std::move(callback);
        m_MicAccum.clear();
        m_MicAccum.reserve(kMicFrameSamples * 4);
        m_MicReadPos = 0;

        AAudioStreamBuilder* builder = nullptr;
        if (AAudio_createStreamBuilder(&builder) != AAUDIO_OK || !builder)
        {
            opus_encoder_destroy(m_Encoder);
            m_Encoder = nullptr;
            return false;
        }

        AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_INPUT);
        AAudioStreamBuilder_setSampleRate(builder, kMicSampleRate);
        AAudioStreamBuilder_setChannelCount(builder, kMicChannels);
        AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
        using SetInputPresetFn = void (*)(AAudioStreamBuilder*, int32_t);
        if (auto fn = reinterpret_cast<SetInputPresetFn>(dlsym(RTLD_DEFAULT, "AAudioStreamBuilder_setInputPreset")))
        {
            fn(builder, AAUDIO_INPUT_PRESET_VOICE_COMMUNICATION);
        }
        AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
        AAudioStreamBuilder_setDataCallback(builder, &AndroidAudioEngine::MicCallback, this);

        aaudio_result_t res = AAudioStreamBuilder_openStream(builder, &m_MicStream);
        AAudioStreamBuilder_delete(builder);
        if (res != AAUDIO_OK || !m_MicStream)
        {
            LOGW("AAudio mic stream open failed: %d", res);
            opus_encoder_destroy(m_Encoder);
            m_Encoder = nullptr;
            return false;
        }

        if (AAudioStream_requestStart(m_MicStream) != AAUDIO_OK)
        {
            AAudioStream_close(m_MicStream);
            m_MicStream = nullptr;
            opus_encoder_destroy(m_Encoder);
            m_Encoder = nullptr;
            return false;
        }

        LOGI("AAudio mic capture started");
        return true;
    }

    void AndroidAudioEngine::StopMicCapture()
    {
        std::lock_guard<std::mutex> lock(m_MicMutex);
        if (m_MicStream)
        {
            AAudioStream_requestStop(m_MicStream);
            AAudioStream_close(m_MicStream);
            m_MicStream = nullptr;
        }
        if (m_Encoder)
        {
            opus_encoder_destroy(m_Encoder);
            m_Encoder = nullptr;
        }
        m_MicCb = nullptr;
        m_MicAccum.clear();
        m_MicReadPos = 0;
    }

    aaudio_data_callback_result_t AndroidAudioEngine::MicCallback(
        AAudioStream* /*stream*/,
        void* userData,
        void* audioData,
        int32_t numFrames)
    {
        auto* self = static_cast<AndroidAudioEngine*>(userData);
        if (!self->m_Encoder || !self->m_MicCb || numFrames <= 0)
            return AAUDIO_CALLBACK_RESULT_CONTINUE;

        const auto* in = static_cast<const int16_t*>(audioData);
        self->m_MicAccum.insert(self->m_MicAccum.end(), in, in + numFrames);

        while (self->m_MicAccum.size() - self->m_MicReadPos >= kMicFrameSamples)
        {
            int encoded = opus_encode(
                self->m_Encoder,
                self->m_MicAccum.data() + self->m_MicReadPos,
                kMicFrameSamples,
                self->m_MicOutBuf,
                sizeof(self->m_MicOutBuf));
            self->m_MicReadPos += kMicFrameSamples;
            if (encoded > 0)
            {
                self->m_MicCb(
                    self->m_MicOutBuf,
                    static_cast<uint16_t>(encoded),
                    kMicSampleRate,
                    kMicChannels,
                    kMicFrameSamples);
            }
        }
        if (self->m_MicReadPos == self->m_MicAccum.size())
        {
            self->m_MicAccum.clear();
            self->m_MicReadPos = 0;
        }
        else if (self->m_MicReadPos >= static_cast<size_t>(kMicFrameSamples) * 4)
        {
            self->m_MicAccum.erase(self->m_MicAccum.begin(),
                                   self->m_MicAccum.begin() + static_cast<ptrdiff_t>(self->m_MicReadPos));
            self->m_MicReadPos = 0;
        }
        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }
}
