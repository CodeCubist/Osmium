#pragma once

#include "OsmiumCommon.h"
#include "OsmiumProtocol.h"
#include <SDL3/SDL.h>
#include <opus.h>
#include <atomic>
#include <condition_variable>
#include <map>
#include <mutex>
#include <thread>
#include <vector>

namespace osmium
{
    class ClientAudioPlayer
    {
    public:
        ClientAudioPlayer() = default;
        ~ClientAudioPlayer();

        bool Initialize();
        void Shutdown();

        void SetMuted(bool muted);

        void PushOpusFrame(
            const uint8_t* data,
            int len,
            int sampleRate,
            int channels,
            int sampleCount,
            uint32_t sequence,
            uint64_t captureTimeUs = 0,
            uint8_t flags = 0,
            const uint8_t* redundantData = nullptr,
            int redundantLen = 0);

        // Video-Master A/V sync hooks
        void NotifyVideoFrameRendered(uint64_t hostCaptureTimeUs, uint64_t clientRenderTimeUs);
        void RecordClockOffset(int64_t clockOffsetUs, uint32_t rttUs);

        // Telemetry for AudioPing
        void GetAudioTelemetry(uint16_t& outLossPermille, uint16_t& outJitterMs, uint16_t& outBufferMs) const;

    private:
        struct JitterSlot
        {
            uint32_t sequence = 0;
            uint64_t captureTimeUs = 0;
            uint64_t arrivalUs = 0;
            int sampleRate = 48000;
            int channels = 2;
            int sampleCount = 960;
            uint8_t flags = 0;
            std::vector<uint8_t> data;
        };

        int ComputeMinSafeQueuedMsLocked() const;
        int ComputeTargetPrimeMsLocked() const;
        int ComputeMaxFeedQueuedMsLocked() const;
        void WorkerLoop();
        void DecodeAndQueueSlot(const JitterSlot& slot);
        void ConcealMissingFrame();
        void UpdateAvSyncRatio(uint64_t audioCaptureTimeUs, int queuedBytes);
        void ApplyFadeIn(float* pcm, int sampleCount, int channels);
        void ApplySoftLimiter(float* pcm, int totalFloats);

        SDL_AudioStream* m_AudioStream = nullptr;
        OpusDecoder* m_Opus = nullptr;
        int m_SampleRate = 48000;
        int m_Channels = 2;

        std::atomic<bool> m_Initialized{false};
        std::atomic<bool> m_Muted{false};
        std::atomic<bool> m_PendingClear{false};
        std::atomic<bool> m_StopFlag{false};

        std::vector<float> m_PcmBuf;
        std::vector<float> m_FadeBuf;

        // Jitter buffer state (guarded by m_QueueMutex)
        std::mutex m_QueueMutex;
        std::condition_variable m_QueueCv;
        std::map<uint32_t, JitterSlot> m_JitterMap;
        bool m_HasExpectedSeq = false;
        uint32_t m_ExpectedSeq = 0;
        bool m_Priming = true;
        uint64_t m_FirstArrivalUs = 0;
        uint64_t m_LastFramePopUs = 0;
        int m_ConsecutivePlc = 0;
        int m_EmptyBridgePlc = 0;
        bool m_NeedFadeIn = true;

        // Arrival jitter, peak excursion & loss stats (guarded by m_QueueMutex)
        bool m_HasLastArrival = false;
        uint64_t m_LastArrivalUs = 0;
        uint64_t m_LastCaptureUs = 0;
        double m_JitterUs = 6000.0;
        double m_PeakExcursionUs = 10000.0;
        std::atomic<int64_t> m_AdaptiveMinQueuedUs{36000};
        uint32_t m_StatsExpected = 0;
        uint32_t m_StatsLost = 0;
        uint64_t m_StatsWindowStartMs = 0;
        std::atomic<uint16_t> m_TelemetryLossPermille{0};
        std::atomic<uint16_t> m_TelemetryJitterMs{6};
        std::atomic<uint16_t> m_TelemetryBufferMs{0};

        // Video-Master A/V sync state (guarded by m_SyncMutex)
        mutable std::mutex m_SyncMutex;
        bool m_HasClockOffset = false;
        int64_t m_ClockOffsetUs = 0;
        uint32_t m_BestClockRttUs = UINT32_MAX;
        uint64_t m_LastBestRttResetUs = 0;
        bool m_HasVideoOffset = false;
        int64_t m_VideoPresentOffsetUs = 0;
        int64_t m_VideoPipelineUs = 25000;
        uint64_t m_LastVideoRenderUs = 0;
        float m_CurrentFreqRatio = 1.0f;
        int m_HighErrorStreak = 0;

        std::thread m_WorkerThread;
        std::mutex m_LifecycleMutex;
    };
}
