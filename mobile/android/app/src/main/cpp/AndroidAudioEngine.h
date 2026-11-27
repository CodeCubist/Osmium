#pragma once

#include "OsmiumProtocol.h"
#include <aaudio/AAudio.h>
#include <opus.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <map>
#include <mutex>
#include <thread>
#include <vector>

namespace osmium::mobile
{
    class AndroidAudioEngine
    {
    public:
        using MicFrameCallback = std::function<void(const uint8_t* opusData, uint16_t payloadBytes,
                                                    uint16_t sampleRate, uint8_t channels, uint16_t sampleCount)>;

        AndroidAudioEngine() = default;
        ~AndroidAudioEngine();

        AndroidAudioEngine(const AndroidAudioEngine&) = delete;
        AndroidAudioEngine& operator=(const AndroidAudioEngine&) = delete;

        void SetMuted(bool muted);
        bool IsMuted() const { return m_Muted.load(std::memory_order_relaxed); }

        void PushOpusFrame(
            const uint8_t* data,
            uint16_t bytes,
            uint16_t sampleRate,
            uint8_t channels,
            uint16_t sampleCount,
            uint32_t sequence,
            uint64_t captureTimeUs = 0,
            uint8_t flags = 0,
            const uint8_t* redundantData = nullptr,
            uint16_t redundantBytes = 0);

        // Video-Master A/V synchronization hooks
        void NotifyVideoFrameRendered(uint64_t hostCaptureTimeUs, uint64_t clientRenderTimeUs);
        void RecordClockOffset(int64_t clockOffsetUs, uint32_t rttUs);

        // Live audio telemetry for AudioPing feedback to Host
        void GetAudioTelemetry(uint16_t& outLossPermille, uint16_t& outJitterMs, uint16_t& outBufferMs) const;

        bool StartMicCapture(MicFrameCallback callback);
        void StopMicCapture();

        void StopAll();

    private:
        struct JitterSlot
        {
            uint32_t sequence = 0;
            uint64_t captureTimeUs = 0;
            uint64_t arrivalUs = 0;
            uint16_t sampleRate = 48000;
            uint8_t channels = 2;
            uint16_t sampleCount = 960;
            uint8_t flags = 0;
            std::vector<uint8_t> data;
        };

        void EnsureDecodeThreadLocked();
        int ComputeMinSafeRingMsLocked() const;
        int ComputeTargetPrimeMsLocked() const;
        int ComputeMaxFeedRingMsLocked() const;
        void DecodeWorkerLoop();
        void DecodeSlot(const JitterSlot& slot);
        void ConcealMissingFrame();
        bool EnsurePlayback(uint16_t sampleRate, uint8_t channels);
        void StopPlayback();
        void WriteFloatFrames(const float* samples, uint32_t frameCount, uint8_t ch);
        void UpdateHardwareLatencyEstimate();
        void UpdateAvSyncRate(uint64_t audioCaptureTimeUs, uint32_t ringFramesAvail);
        void ApplyFadeIn(float* pcm, int sampleCount, uint8_t ch);
        void ApplySoftLimiter(float* pcm, int totalFloats);

        static aaudio_data_callback_result_t PlaybackCallback(
            AAudioStream* stream,
            void* userData,
            void* audioData,
            int32_t numFrames);

        static void PlaybackErrorCallback(
            AAudioStream* stream,
            void* userData,
            aaudio_result_t error);

        static aaudio_data_callback_result_t MicCallback(
            AAudioStream* stream,
            void* userData,
            void* audioData,
            int32_t numFrames);

        // Lock-free SPSC float frame ring between DecodeWorkerLoop (producer) and
        // real-time AAudio PlaybackCallback (consumer).
        // 32768 frames ~= 682ms @ 48kHz stereo.
        static constexpr uint32_t kRingFrames = 32768;
        static constexpr uint32_t kRingFrameMask = kRingFrames - 1;

        std::mutex m_QueueMutex;
        std::condition_variable m_QueueCv;
        std::map<uint32_t, JitterSlot> m_JitterMap;
        bool m_HasExpectedSeq = false;
        uint32_t m_ExpectedSeq = 0;
        bool m_Priming = true;
        uint64_t m_FirstArrivalUs = 0;
        uint64_t m_LastFramePopUs = 0;
        uint64_t m_LastPacketReceivedUs = 0;
        int m_ConsecutivePlc = 0;
        int m_EmptyBridgePlc = 0;
        bool m_NeedFadeIn = true;
        bool m_DecodeStop = false;
        std::thread m_DecodeThread;

        // Arrival jitter, peak excursion & loss stats (guarded by m_QueueMutex)
        bool m_HasLastArrival = false;
        uint64_t m_LastArrivalUs = 0;
        uint64_t m_LastCaptureUs = 0;
        double m_JitterUs = 8000.0;
        double m_PeakExcursionUs = 12000.0;
        std::atomic<int64_t> m_AdaptiveMinRingUs{42000};
        uint32_t m_StatsExpected = 0;
        uint32_t m_StatsLost = 0;
        uint64_t m_StatsWindowStartMs = 0;
        std::atomic<uint16_t> m_TelemetryLossPermille{0};
        std::atomic<uint16_t> m_TelemetryJitterMs{8};
        std::atomic<uint16_t> m_TelemetryBufferMs{0};

        // Video-Master A/V sync state (guarded by m_SyncMutex)
        mutable std::mutex m_SyncMutex;
        bool m_HasClockOffset = false;
        int64_t m_ClockOffsetUs = 0;
        uint32_t m_BestClockRttUs = UINT32_MAX;
        uint64_t m_LastBestRttResetUs = 0;
        bool m_HasVideoOffset = false;
        int64_t m_VideoPresentOffsetUs = 0;
        int64_t m_VideoPipelineUs = 32000;
        uint64_t m_LastVideoRenderUs = 0;
        double m_HwDacLatencyUs = 45000.0;
        uint64_t m_LastHwTimestampQueryUs = 0;
        double m_SmoothedRate = 1.0;
        int m_HighErrorStreak = 0;

        OpusDecoder* m_Decoder = nullptr;
        AAudioStream* m_PlayStream = nullptr;
        uint16_t m_PlaySampleRate = 0;
        std::atomic<uint8_t> m_PlayChannels{2};
        std::atomic<bool> m_UseFloatOutput{true};
        std::atomic<bool> m_Muted{false};
        std::atomic<bool> m_StreamNeedsRebuild{false};
        std::atomic<double> m_PlaybackRate{1.0};
        std::atomic<bool> m_PendingRingReset{false};
        std::atomic<uint32_t> m_PendingTrimKeepFrames{0};
        std::atomic<bool> m_StreamActiveForUnderrun{false};
        std::atomic<bool> m_HadUnderrun{false};

        // Interleaved float ring buffer [kRingFrames * 2]
        std::vector<float> m_PcmRing;
        alignas(64) std::atomic<uint32_t> m_RingReadFrame{0};
        alignas(64) std::atomic<uint32_t> m_RingWriteFrame{0};
        // Accessed exclusively on AAudio PlaybackCallback thread (reset when stream stopped)
        double m_FracPos = 0.0;
        float m_PrevOutSample[2]{0.0f, 0.0f};
        float m_FadeAnchorSample[2]{0.0f, 0.0f};
        int32_t m_CallbackFadeInLeft = 0;
        bool m_HasRingPrev = false;
        bool m_InUnderrun = false;

        std::vector<float> m_DecodeScratch;

        std::mutex m_MicMutex;
        OpusEncoder* m_Encoder = nullptr;
        AAudioStream* m_MicStream = nullptr;
        MicFrameCallback m_MicCb;
        std::vector<int16_t> m_MicAccum;
        size_t m_MicReadPos = 0;
        uint8_t m_MicOutBuf[1024]{};
    };
}
