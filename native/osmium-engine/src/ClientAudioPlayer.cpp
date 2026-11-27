#include "ClientAudioPlayer.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>

namespace osmium
{
    namespace
    {
        constexpr size_t kMaxJitterSlots = 24;
        constexpr int kDefaultSampleRate = 48000;
        constexpr int kDefaultChannels = 2;
        constexpr int kDefaultFrameSamples = 960; // 20ms @ 48kHz
        constexpr int64_t kWasapiDacLatencyUs = 15000; // ~15ms shared-mode WASAPI render latency
        constexpr int kFadeInSamples = 240; // 5ms @ 48kHz

        inline int32_t SeqDiff(uint32_t a, uint32_t b)
        {
            return static_cast<int32_t>(a - b);
        }
    }

    ClientAudioPlayer::~ClientAudioPlayer()
    {
        Shutdown();
    }

    bool ClientAudioPlayer::Initialize()
    {
        std::lock_guard<std::mutex> lock(m_LifecycleMutex);
        if (m_Initialized.load(std::memory_order_acquire))
        {
            return true;
        }

        if (!SDL_WasInit(SDL_INIT_AUDIO))
        {
            if (!SDL_InitSubSystem(SDL_INIT_AUDIO))
            {
                OSM_LOG_WARN("SDL_InitSubSystem(AUDIO) failed: %s", SDL_GetError());
                return false;
            }
        }

        int err = OPUS_OK;
        m_Opus = opus_decoder_create(kDefaultSampleRate, kDefaultChannels, &err);
        if (!m_Opus || err != OPUS_OK)
        {
            OSM_LOG_ERROR("opus_decoder_create failed: %d", err);
            return false;
        }

        SDL_AudioSpec spec{};
        spec.format = SDL_AUDIO_F32;
        spec.channels = kDefaultChannels;
        spec.freq = kDefaultSampleRate;
        m_AudioStream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, nullptr, nullptr);
        if (!m_AudioStream)
        {
            OSM_LOG_WARN("SDL_OpenAudioDeviceStream failed: %s", SDL_GetError());
            opus_decoder_destroy(m_Opus);
            m_Opus = nullptr;
            return false;
        }

        SDL_ResumeAudioStreamDevice(m_AudioStream);
        SDL_SetAudioStreamFrequencyRatio(m_AudioStream, 1.0f);
        m_SampleRate = kDefaultSampleRate;
        m_Channels = kDefaultChannels;
        m_PendingClear.store(false, std::memory_order_relaxed);
        m_PcmBuf.resize(5760 * 2);

        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);
            m_JitterMap.clear();
            m_HasExpectedSeq = false;
            m_ExpectedSeq = 0;
            m_Priming = true;
            m_FirstArrivalUs = 0;
            m_LastFramePopUs = 0;
            m_ConsecutivePlc = 0;
            m_EmptyBridgePlc = 0;
            m_NeedFadeIn = true;
            m_HasLastArrival = false;
            m_JitterUs = 6000.0;
            m_PeakExcursionUs = 10000.0;
            m_AdaptiveMinQueuedUs.store(36000, std::memory_order_relaxed);
            m_StatsExpected = 0;
            m_StatsLost = 0;
            m_StatsWindowStartMs = NowMillis();
        }
        {
            std::lock_guard<std::mutex> sl(m_SyncMutex);
            m_HasClockOffset = false;
            m_ClockOffsetUs = 0;
            m_BestClockRttUs = UINT32_MAX;
            m_LastBestRttResetUs = 0;
            m_HasVideoOffset = false;
            m_VideoPresentOffsetUs = 0;
            m_VideoPipelineUs = 25000;
            m_LastVideoRenderUs = 0;
            m_CurrentFreqRatio = 1.0f;
            m_HighErrorStreak = 0;
        }

        m_StopFlag.store(false, std::memory_order_release);
        m_Initialized.store(true, std::memory_order_release);
        m_WorkerThread = std::thread(&ClientAudioPlayer::WorkerLoop, this);
        return true;
    }

    void ClientAudioPlayer::Shutdown()
    {
        std::lock_guard<std::mutex> lock(m_LifecycleMutex);
        m_Initialized.store(false, std::memory_order_release);
        m_StopFlag.store(true, std::memory_order_release);
        m_QueueCv.notify_all();

        if (m_WorkerThread.joinable())
        {
            m_WorkerThread.join();
        }

        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);
            m_JitterMap.clear();
        }

        if (m_AudioStream)
        {
            SDL_DestroyAudioStream(m_AudioStream);
            m_AudioStream = nullptr;
        }
        if (m_Opus)
        {
            opus_decoder_destroy(m_Opus);
            m_Opus = nullptr;
        }
    }

    void ClientAudioPlayer::SetMuted(bool muted)
    {
        m_Muted.store(muted, std::memory_order_release);
        if (muted)
        {
            m_PendingClear.store(true, std::memory_order_release);
            {
                std::lock_guard<std::mutex> ql(m_QueueMutex);
                m_JitterMap.clear();
                m_HasExpectedSeq = false;
                m_Priming = true;
                m_NeedFadeIn = true;
            }
            m_QueueCv.notify_one();
        }
    }

    void ClientAudioPlayer::NotifyVideoFrameRendered(uint64_t hostCaptureTimeUs, uint64_t clientRenderTimeUs)
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
            if (pipe >= 4000LL && pipe <= 300000LL)
            {
                m_VideoPipelineUs = static_cast<int64_t>(
                    static_cast<double>(m_VideoPipelineUs) * 0.90 + static_cast<double>(pipe) * 0.10);
            }
        }
    }

    void ClientAudioPlayer::RecordClockOffset(int64_t clockOffsetUs, uint32_t rttUs)
    {
        const uint64_t nowUs = NowMicros();
        std::lock_guard<std::mutex> sl(m_SyncMutex);
        // Reset min-RTT filter every 15s to track slow route changes without losing stability.
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

        if (rttUs <= m_BestClockRttUs + 3000U)
        {
            if (rttUs < m_BestClockRttUs) m_BestClockRttUs = rttUs;
            m_ClockOffsetUs = static_cast<int64_t>(
                static_cast<double>(m_ClockOffsetUs) * 0.85 + static_cast<double>(clockOffsetUs) * 0.15);
        }
    }

    void ClientAudioPlayer::GetAudioTelemetry(uint16_t& outLossPermille, uint16_t& outJitterMs, uint16_t& outBufferMs) const
    {
        outLossPermille = m_TelemetryLossPermille.load(std::memory_order_relaxed);
        outJitterMs = m_TelemetryJitterMs.load(std::memory_order_relaxed);
        outBufferMs = m_TelemetryBufferMs.load(std::memory_order_relaxed);
    }

    void ClientAudioPlayer::PushOpusFrame(
        const uint8_t* data,
        int len,
        int sampleRate,
        int channels,
        int sampleCount,
        uint32_t sequence,
        uint64_t captureTimeUs,
        uint8_t flags,
        const uint8_t* redundantData,
        int redundantLen)
    {
        if (!m_Initialized.load(std::memory_order_acquire) || !data || len <= 0 || len > 4096) return;
        if (m_Muted.load(std::memory_order_acquire)) return;

        const uint64_t nowUs = NowMicros();
        const int sr = sampleRate > 0 ? sampleRate : kDefaultSampleRate;
        const int ch = (channels == 1 || channels == 2) ? channels : kDefaultChannels;
        const int sc = sampleCount > 0 ? sampleCount : kDefaultFrameSamples;

        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);

            // RFC3550 inter-arrival jitter + peak late-arrival burst excursion estimator
            if (m_HasLastArrival && captureTimeUs > m_LastCaptureUs && (flags & proto::kAudioFlagStreamStart) == 0)
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
                    m_AdaptiveMinQueuedUs.store(
                        static_cast<int64_t>(ComputeMinSafeQueuedMsLocked()) * 1000LL,
                        std::memory_order_relaxed);
                    m_TelemetryJitterMs.store(
                        static_cast<uint16_t>(std::clamp(m_JitterUs / 1000.0, 0.0, 500.0)),
                        std::memory_order_relaxed);
                }
            }
            m_HasLastArrival = true;
            m_LastArrivalUs = nowUs;
            m_LastCaptureUs = captureTimeUs;

            const bool isStreamStart = (flags & proto::kAudioFlagStreamStart) != 0;

            if (isStreamStart)
            {
                // Clean start of a new talkspurt after silence
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
                // Very old duplicate/reordered packet or stream restart after sequence jump
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

            // Piggybacked redundant previous frame: bit-exact recovery if (sequence - 1) was lost!
            if (redundantData && redundantLen > 0 && redundantLen <= 4096 && sequence > 1 && !isStreamStart)
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
                    prevSlot.data.assign(redundantData, redundantData + redundantLen);
                    m_JitterMap.emplace(prevSeq, std::move(prevSlot));
                }
            }

            // Insert primary frame if still relevant
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
                slot.data.assign(data, data + len);
                m_JitterMap.emplace(sequence, std::move(slot));
            }

            // Bound jitter buffer size without jumping violently
            while (m_JitterMap.size() > kMaxJitterSlots)
            {
                auto it = m_JitterMap.begin();
                m_ExpectedSeq = it->first + 1;
                m_JitterMap.erase(it);
            }
        }
        m_QueueCv.notify_one();
    }

    void ClientAudioPlayer::ApplyFadeIn(float* pcm, int sampleCount, int channels)
    {
        const int ramp = std::min(sampleCount, kFadeInSamples);
        for (int i = 0; i < ramp; ++i)
        {
            const float g = static_cast<float>(i) / static_cast<float>(ramp);
            for (int c = 0; c < channels; ++c)
            {
                pcm[i * channels + c] *= g;
            }
        }
    }

    void ClientAudioPlayer::ApplySoftLimiter(float* pcm, int totalFloats)
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

    int ClientAudioPlayer::ComputeMinSafeQueuedMsLocked() const
    {
        const double jitterMs = m_JitterUs / 1000.0;
        const double peakMs = m_PeakExcursionUs / 1000.0;
        return std::clamp(
            static_cast<int>(std::ceil(std::max(jitterMs * 3.0 + 20.0, peakMs * 0.85 + 22.0))),
            36,
            110);
    }

    int ClientAudioPlayer::ComputeTargetPrimeMsLocked() const
    {
        return std::clamp(ComputeMinSafeQueuedMsLocked() + 8, 40, 115);
    }

    int ClientAudioPlayer::ComputeMaxFeedQueuedMsLocked() const
    {
        return std::max(90, ComputeMinSafeQueuedMsLocked() + 50);
    }

    void ClientAudioPlayer::UpdateAvSyncRatio(uint64_t audioCaptureTimeUs, int queuedBytes)
    {
        if (!m_AudioStream || audioCaptureTimeUs == 0) return;

        const uint64_t nowUs = NowMicros();
        const int64_t queuedAudioUs =
            (static_cast<int64_t>(queuedBytes) * 1000000LL) / (kDefaultSampleRate * kDefaultChannels * static_cast<int64_t>(sizeof(float)));
        const int64_t minSafeUs = std::clamp<int64_t>(
            m_AdaptiveMinQueuedUs.load(std::memory_order_relaxed), 36000LL, 110000LL);
        const int64_t targetQueuedUs = minSafeUs + 15000LL;

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

        float desiredRatio = 1.0f;
        if (queuedAudioUs < minSafeUs)
        {
            // Never speed up below safety floor — gently refill cushion instead of starving!
            m_HighErrorStreak = 0;
            const int64_t deficitUs = minSafeUs - queuedAudioUs;
            desiredRatio = std::clamp(1.0f - static_cast<float>(deficitUs) / 3000000.0f, 0.988f, 0.997f);
        }
        else if (haveSyncRef)
        {
            const int64_t actualPlayLocalUs = static_cast<int64_t>(nowUs) + queuedAudioUs + kWasapiDacLatencyUs;
            const int64_t targetPlayLocalUs = static_cast<int64_t>(audioCaptureTimeUs) + targetOffsetUs;
            const int64_t avErrorUs = actualPlayLocalUs - targetPlayLocalUs;

            if (avErrorUs > 0)
            {
                const int64_t excessQueuedUs = queuedAudioUs - minSafeUs;
                const int64_t effectiveCatchupUs = std::min(avErrorUs, excessQueuedUs);
                if (effectiveCatchupUs <= 15000LL)
                {
                    m_HighErrorStreak = 0;
                    desiredRatio = 1.0f;
                }
                else if (queuedAudioUs <= targetQueuedUs + 120000LL)
                {
                    m_HighErrorStreak = 0;
                    desiredRatio = std::clamp(
                        1.0f + static_cast<float>(effectiveCatchupUs - 15000LL) / 4000000.0f,
                        1.0f,
                        1.012f);
                }
                else
                {
                    desiredRatio = std::clamp(
                        1.0f + static_cast<float>(effectiveCatchupUs) / 3000000.0f,
                        1.005f,
                        1.022f);
                    if (queuedAudioUs > 300000LL)
                    {
                        if (++m_HighErrorStreak >= 8)
                        {
                            m_HighErrorStreak = 0;
                            SDL_ClearAudioStream(m_AudioStream);
                            m_NeedFadeIn = true;
                            desiredRatio = 1.0f;
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
                m_HighErrorStreak = 0;
                const int64_t earlyUs = -avErrorUs;
                if (earlyUs <= 15000LL || queuedAudioUs >= 240000LL)
                {
                    desiredRatio = 1.0f;
                }
                else
                {
                    desiredRatio = std::clamp(
                        1.0f - static_cast<float>(earlyUs - 15000LL) / 4000000.0f,
                        0.988f,
                        1.0f);
                }
            }
        }
        else
        {
            m_HighErrorStreak = 0;
            const int64_t bufErrUs = queuedAudioUs - targetQueuedUs;
            if (std::llabs(bufErrUs) > 15000LL)
            {
                desiredRatio = std::clamp(1.0f + static_cast<float>(bufErrUs) / 4000000.0f, 0.990f, 1.010f);
            }
        }

        float smoothed = m_CurrentFreqRatio * 0.84f + desiredRatio * 0.16f;
        if (std::fabs(smoothed - 1.0f) < 0.0006f && std::fabs(desiredRatio - 1.0f) < 1e-6f)
        {
            smoothed = 1.0f;
        }
        if (std::fabs(smoothed - m_CurrentFreqRatio) >= 0.0004f || (smoothed == 1.0f && m_CurrentFreqRatio != 1.0f))
        {
            m_CurrentFreqRatio = smoothed;
            SDL_SetAudioStreamFrequencyRatio(m_AudioStream, m_CurrentFreqRatio);
        }
    }

    void ClientAudioPlayer::DecodeAndQueueSlot(const JitterSlot& slot)
    {
        if (!m_AudioStream || !m_Opus || slot.data.empty() || m_Muted.load(std::memory_order_acquire)) return;

        if (slot.sampleRate != m_SampleRate || slot.channels != m_Channels)
        {
            opus_decoder_destroy(m_Opus);
            int err = OPUS_OK;
            m_Opus = opus_decoder_create(slot.sampleRate, slot.channels, &err);
            if (!m_Opus || err != OPUS_OK)
            {
                m_Opus = nullptr;
                return;
            }
            m_SampleRate = slot.sampleRate;
            m_Channels = slot.channels;
            SDL_AudioSpec srcSpec{SDL_AUDIO_F32, slot.channels, slot.sampleRate};
            SDL_AudioSpec dstSpec{SDL_AUDIO_F32, kDefaultChannels, kDefaultSampleRate};
            SDL_SetAudioStreamFormat(m_AudioStream, &srcSpec, &dstSpec);
        }

        const int maxSamples = std::max(slot.sampleCount, 1920);
        if (static_cast<int>(m_PcmBuf.size()) < maxSamples * m_Channels)
        {
            m_PcmBuf.resize(static_cast<size_t>(maxSamples) * m_Channels);
        }

        const int bytesPerSec = kDefaultSampleRate * kDefaultChannels * static_cast<int>(sizeof(float));
        int queued = SDL_GetAudioStreamQueued(m_AudioStream);
        if (queued > (bytesPerSec * 360) / 1000)
        {
            SDL_ClearAudioStream(m_AudioStream);
            m_NeedFadeIn = true;
            queued = 0;
        }

        UpdateAvSyncRatio(slot.captureTimeUs, queued);

        int decoded = opus_decode_float(
            m_Opus,
            slot.data.data(),
            static_cast<int>(slot.data.size()),
            m_PcmBuf.data(),
            maxSamples,
            0);
        if (decoded <= 0 || m_Muted.load(std::memory_order_acquire)) return;

        if (m_NeedFadeIn || (slot.flags & proto::kAudioFlagStreamStart) != 0)
        {
            ApplyFadeIn(m_PcmBuf.data(), decoded, m_Channels);
            m_NeedFadeIn = false;
        }
        ApplySoftLimiter(m_PcmBuf.data(), decoded * m_Channels);

        SDL_PutAudioStreamData(
            m_AudioStream,
            m_PcmBuf.data(),
            decoded * m_Channels * static_cast<int>(sizeof(float)));

        m_ConsecutivePlc = 0;
        const int newQueued = queued + decoded * kDefaultChannels * static_cast<int>(sizeof(float));
        m_TelemetryBufferMs.store(
            static_cast<uint16_t>(std::clamp((newQueued * 1000) / bytesPerSec, 0, 500)),
            std::memory_order_relaxed);
    }

    void ClientAudioPlayer::ConcealMissingFrame()
    {
        if (!m_AudioStream || !m_Opus || m_Muted.load(std::memory_order_acquire)) return;
        if (m_ConsecutivePlc >= 3)
        {
            // Stop PLC after 60ms of continuous loss; fade-in cleanly on next real packet.
            m_NeedFadeIn = true;
            return;
        }

        const int plcSamples = kDefaultFrameSamples;
        if (static_cast<int>(m_PcmBuf.size()) < plcSamples * m_Channels)
        {
            m_PcmBuf.resize(static_cast<size_t>(plcSamples) * m_Channels);
        }

        // True Opus Packet Loss Concealment (data = nullptr, len = 0)
        int decoded = opus_decode_float(m_Opus, nullptr, 0, m_PcmBuf.data(), plcSamples, 0);
        if (decoded > 0)
        {
            // Gentle attenuation on 2nd/3rd PLC frames to prevent ringing
            if (m_ConsecutivePlc >= 1)
            {
                const float startGain = (m_ConsecutivePlc == 1) ? 0.85f : 0.45f;
                const float endGain = (m_ConsecutivePlc == 1) ? 0.45f : 0.0f;
                for (int i = 0; i < decoded; ++i)
                {
                    const float g = startGain + (endGain - startGain) * (static_cast<float>(i) / static_cast<float>(decoded));
                    for (int c = 0; c < m_Channels; ++c)
                    {
                        m_PcmBuf[i * m_Channels + c] *= g;
                    }
                }
            }
            ApplySoftLimiter(m_PcmBuf.data(), decoded * m_Channels);
            SDL_PutAudioStreamData(
                m_AudioStream,
                m_PcmBuf.data(),
                decoded * m_Channels * static_cast<int>(sizeof(float)));
        }
        m_ConsecutivePlc++;
    }

    void ClientAudioPlayer::WorkerLoop()
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        constexpr int kBytesPerMs = (kDefaultSampleRate * kDefaultChannels * static_cast<int>(sizeof(float))) / 1000;

        while (!m_StopFlag.load(std::memory_order_acquire))
        {
            if (m_PendingClear.exchange(false, std::memory_order_acq_rel))
            {
                if (m_AudioStream)
                {
                    SDL_ClearAudioStream(m_AudioStream);
                    SDL_SetAudioStreamFrequencyRatio(m_AudioStream, 1.0f);
                }
                m_CurrentFreqRatio = 1.0f;
                m_NeedFadeIn = true;
            }

            JitterSlot toDecode;
            bool doDecode = false;
            bool doPlc = false;

            {
                std::unique_lock<std::mutex> ql(m_QueueMutex);
                auto canProcessNowLocked = [this, kBytesPerMs]() -> bool
                {
                    if (m_StopFlag.load(std::memory_order_acquire) ||
                        m_PendingClear.load(std::memory_order_acquire))
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
                        const int qBytes = m_AudioStream ? SDL_GetAudioStreamQueued(m_AudioStream) : 0;
                        const int qMs = qBytes / kBytesPerMs;
                        return (qMs <= ComputeMaxFeedQueuedMsLocked()) || (m_JitterMap.size() > 4);
                    }
                    if (!m_JitterMap.empty())
                    {
                        const int32_t gap = SeqDiff(m_JitterMap.begin()->first, m_ExpectedSeq);
                        if (gap <= 0 || gap > 6) return true;
                    }
                    return false;
                };

                if (m_JitterMap.empty() && !m_HasExpectedSeq)
                {
                    m_QueueCv.wait_for(ql, std::chrono::milliseconds(15), canProcessNowLocked);
                }
                else
                {
                    m_QueueCv.wait_for(ql, std::chrono::milliseconds(5), canProcessNowLocked);
                }

                if (m_StopFlag.load(std::memory_order_acquire)) break;
                if (m_PendingClear.load(std::memory_order_acquire) || m_Muted.load(std::memory_order_acquire))
                {
                    continue;
                }

                const uint64_t nowUs = NowMicros();
                const uint64_t nowMs = nowUs / 1000ULL;
                const int sdlQueuedBytes = m_AudioStream ? SDL_GetAudioStreamQueued(m_AudioStream) : 0;
                const int sdlQueuedMs = sdlQueuedBytes / kBytesPerMs;
                m_TelemetryBufferMs.store(
                    static_cast<uint16_t>(std::clamp(sdlQueuedMs + static_cast<int>(m_JitterMap.size()) * 20, 0, 500)),
                    std::memory_order_relaxed);

                // Update 1-second sliding audio loss window
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
                    continue;
                }

                const int targetPrimeMs = ComputeTargetPrimeMsLocked();
                const int neededFrames = std::clamp((targetPrimeMs + 19) / 20, 2, 6);
                const int maxFeedQueuedMs = ComputeMaxFeedQueuedMsLocked();

                if (m_Priming)
                {
                    if (static_cast<int>(m_JitterMap.size()) >= neededFrames ||
                        (!m_JitterMap.empty() && (nowUs - m_FirstArrivalUs) >= static_cast<uint64_t>(targetPrimeMs) * 1000ULL))
                    {
                        m_Priming = false;
                        m_EmptyBridgePlc = 0;
                        if (!m_JitterMap.empty())
                        {
                            m_ExpectedSeq = m_JitterMap.begin()->first;
                        }
                    }
                    else
                    {
                        continue;
                    }
                }

                auto exactIt = m_JitterMap.find(m_ExpectedSeq);
                if (exactIt != m_JitterMap.end())
                {
                    if (sdlQueuedMs <= maxFeedQueuedMs || m_JitterMap.size() > 4)
                    {
                        toDecode = std::move(exactIt->second);
                        m_JitterMap.erase(exactIt);
                        m_ExpectedSeq++;
                        m_LastFramePopUs = nowUs;
                        m_EmptyBridgePlc = 0;
                        m_StatsExpected++;
                        doDecode = true;
                        if ((toDecode.flags & proto::kAudioFlagSilenceEnd) != 0)
                        {
                            m_HasExpectedSeq = false;
                            m_Priming = true;
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
                        if (sdlQueuedMs < 20 || waitUs >= gapBudgetUs)
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
                    // Jitter map empty: NEVER increment m_ExpectedSeq or discard late packets!
                    if (m_LastFramePopUs > 0 && (nowUs - m_LastFramePopUs) > 350000ULL)
                    {
                        m_HasExpectedSeq = false;
                        m_Priming = true;
                        m_ConsecutivePlc = 0;
                        m_EmptyBridgePlc = 0;
                        m_NeedFadeIn = true;
                    }
                    else if (sdlQueuedMs < 12 &&
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

            if (doDecode)
            {
                DecodeAndQueueSlot(toDecode);
            }
            else if (doPlc)
            {
                ConcealMissingFrame();
            }
        }
    }
}
