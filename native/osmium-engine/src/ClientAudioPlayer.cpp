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
            m_NeedFadeIn = true;
            m_HasLastArrival = false;
            m_JitterUs = 6000.0;
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

            // RFC3550 inter-arrival jitter estimate
            if (m_HasLastArrival && captureTimeUs > m_LastCaptureUs && (flags & proto::kAudioFlagStreamStart) == 0)
            {
                const int64_t dArrival = static_cast<int64_t>(nowUs - m_LastArrivalUs);
                const int64_t dCapture = static_cast<int64_t>(captureTimeUs - m_LastCaptureUs);
                if (dCapture > 0 && dCapture < 200000LL)
                {
                    const double err = std::fabs(static_cast<double>(dArrival - dCapture));
                    m_JitterUs += (std::min(err, 120000.0) - m_JitterUs) / 16.0;
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

    void ClientAudioPlayer::UpdateAvSyncRatio(uint64_t audioCaptureTimeUs, int queuedBytes)
    {
        if (!m_AudioStream || audioCaptureTimeUs == 0) return;

        const uint64_t nowUs = NowMicros();
        const int64_t queuedAudioUs =
            (static_cast<int64_t>(queuedBytes) * 1000000LL) / (kDefaultSampleRate * kDefaultChannels * static_cast<int64_t>(sizeof(float)));

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

        if (!haveSyncRef)
        {
            // No sync reference yet: keep gentle buffer-depth stabilization around 40ms
            if (std::fabs(m_CurrentFreqRatio - 1.0f) > 0.0005f)
            {
                m_CurrentFreqRatio = 1.0f;
                SDL_SetAudioStreamFrequencyRatio(m_AudioStream, 1.0f);
            }
            return;
        }

        const int64_t actualPlayLocalUs = static_cast<int64_t>(nowUs) + queuedAudioUs + kWasapiDacLatencyUs;
        const int64_t targetPlayLocalUs = static_cast<int64_t>(audioCaptureTimeUs) + targetOffsetUs;
        const int64_t avErrorUs = actualPlayLocalUs - targetPlayLocalUs;

        float desiredRatio = 1.0f;
        if (std::llabs(avErrorUs) <= 12000LL)
        {
            m_HighErrorStreak = 0;
            desiredRatio = 1.0f;
        }
        else if (std::llabs(avErrorUs) <= 180000LL)
        {
            m_HighErrorStreak = 0;
            // Smooth proportional rate control (+/- 1.5% max, pitch-imperceptible)
            desiredRatio = std::clamp(1.0f + static_cast<float>(avErrorUs) / 3500000.0f, 0.985f, 1.015f);
        }
        else
        {
            // Very large drift (>180ms): push faster up to +/-3%, and if persistent & audio is very late, trim SDL buffer
            desiredRatio = (avErrorUs > 0) ? 1.03f : 0.97f;
            if (avErrorUs > 220000LL && queuedAudioUs > 90000LL)
            {
                if (++m_HighErrorStreak >= 8)
                {
                    m_HighErrorStreak = 0;
                    SDL_ClearAudioStream(m_AudioStream);
                    m_NeedFadeIn = true;
                    desiredRatio = 1.0f;
                }
            }
        }

        const float smoothed = m_CurrentFreqRatio * 0.80f + desiredRatio * 0.20f;
        if (std::fabs(smoothed - m_CurrentFreqRatio) >= 0.0004f)
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
        if (queued > (bytesPerSec * 220) / 1000)
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
                if (m_JitterMap.empty() && !m_HasExpectedSeq)
                {
                    m_QueueCv.wait_for(ql, std::chrono::milliseconds(15), [this]
                    {
                        return m_StopFlag.load(std::memory_order_acquire) ||
                               m_PendingClear.load(std::memory_order_acquire) ||
                               !m_JitterMap.empty();
                    });
                }
                else
                {
                    m_QueueCv.wait_for(ql, std::chrono::milliseconds(6), [this]
                    {
                        return m_StopFlag.load(std::memory_order_acquire) ||
                               m_PendingClear.load(std::memory_order_acquire) ||
                               (m_HasExpectedSeq && m_JitterMap.find(m_ExpectedSeq) != m_JitterMap.end());
                    });
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

                // Adaptive jitter priming target (30..70ms)
                const int targetPrimeMs = std::clamp(static_cast<int>((m_JitterUs * 2.5) / 1000.0) + 18, 28, 70);
                const int neededFrames = std::clamp((targetPrimeMs + 19) / 20, 2, 4);

                if (m_Priming)
                {
                    if (static_cast<int>(m_JitterMap.size()) >= neededFrames ||
                        (!m_JitterMap.empty() && (nowUs - m_FirstArrivalUs) >= static_cast<uint64_t>(targetPrimeMs) * 1000ULL))
                    {
                        m_Priming = false;
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

                // Pace feeding SDL stream so we hold ~40-60ms in SDL + jitter buffer
                auto exactIt = m_JitterMap.find(m_ExpectedSeq);
                if (exactIt != m_JitterMap.end())
                {
                    // Avoid dumping excessive jitter frames at once into SDL if SDL already holds > 55ms
                    if (sdlQueuedMs <= 60 || m_JitterMap.size() > 4)
                    {
                        toDecode = std::move(exactIt->second);
                        m_JitterMap.erase(exactIt);
                        m_ExpectedSeq++;
                        m_LastFramePopUs = nowUs;
                        m_StatsExpected++;
                        doDecode = true;
                        if ((toDecode.flags & proto::kAudioFlagSilenceEnd) != 0)
                        {
                            // End of talkspurt: stop expecting further packets until next stream start
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
                        // Stale slot behind expected
                        m_JitterMap.erase(m_JitterMap.begin());
                    }
                    else if (gap > 6)
                    {
                        // Big jump (e.g., network hiccup): fast-forward cleanly
                        m_StatsExpected += static_cast<uint32_t>(std::min<int32_t>(gap, 25));
                        m_StatsLost += static_cast<uint32_t>(std::min<int32_t>(gap, 25));
                        m_ExpectedSeq = oldestSeq;
                        m_NeedFadeIn = true;
                    }
                    else
                    {
                        // Missing packet: wait while SDL buffer still has cushion (> 20ms) and wait < 32ms
                        const uint64_t waitUs = nowUs - m_LastFramePopUs;
                        if (sdlQueuedMs < 22 || waitUs >= 32000ULL)
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
                    // Jitter map empty: if stream went silent (> 220ms with no packets), reset to primed idle
                    if (m_LastFramePopUs > 0 && (nowUs - m_LastFramePopUs) > 220000ULL)
                    {
                        m_HasExpectedSeq = false;
                        m_Priming = true;
                        m_ConsecutivePlc = 0;
                        m_NeedFadeIn = true;
                    }
                    else if (sdlQueuedMs < 14 && m_LastFramePopUs > 0 && (nowUs - m_LastFramePopUs) >= 24000ULL)
                    {
                        m_ExpectedSeq++;
                        m_LastFramePopUs = nowUs;
                        m_StatsExpected++;
                        m_StatsLost++;
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
