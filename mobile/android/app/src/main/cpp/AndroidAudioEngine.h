#pragma once

#include <aaudio/AAudio.h>
#include <opus.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
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
            uint32_t sequence = 0);

        bool StartMicCapture(MicFrameCallback callback);
        void StopMicCapture();

        void StopAll();

    private:
        struct PendingOpusFrame
        {
            uint32_t sequence = 0;
            uint16_t sampleRate = 48000;
            uint8_t channels = 2;
            uint16_t sampleCount = 960;
            std::vector<uint8_t> data;
        };

        void EnsureDecodeThreadLocked();
        void DecodeWorkerLoop();
        void ProcessOpusFrame(const PendingOpusFrame& frame);
        bool EnsurePlayback(uint16_t sampleRate, uint8_t channels);
        void StopPlayback();
        void WritePcmRing(const int16_t* samples, size_t count);

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

        // Lock-free SPSC ring buffer between DecodeWorkerLoop (producer) and
        // real-time SCHED_FIFO AAudio PlaybackCallback (consumer).
        // Capacity is a power of two (32768 int16 samples ~= 341ms @ 48kHz stereo).
        static constexpr uint32_t kRingCapacity = 32768;
        static constexpr uint32_t kRingMask = kRingCapacity - 1;

        std::mutex m_QueueMutex;
        std::condition_variable m_QueueCv;
        std::deque<PendingOpusFrame> m_PendingQueue;
        std::vector<std::vector<uint8_t>> m_FreeBuffers;
        bool m_DecodeStop = false;
        std::thread m_DecodeThread;

        OpusDecoder* m_Decoder = nullptr;
        AAudioStream* m_PlayStream = nullptr;
        uint16_t m_PlaySampleRate = 0;
        std::atomic<uint8_t> m_PlayChannels{2};
        uint32_t m_LastSequence = 0;
        bool m_HasSequence = false;
        std::atomic<bool> m_Muted{false};
        std::atomic<bool> m_StreamNeedsRebuild{false};

        std::vector<int16_t> m_PcmRing;
        alignas(64) std::atomic<uint32_t> m_RingRead{0};
        alignas(64) std::atomic<uint32_t> m_RingWrite{0};
        std::vector<int16_t> m_DecodeScratch;

        std::mutex m_MicMutex;
        OpusEncoder* m_Encoder = nullptr;
        AAudioStream* m_MicStream = nullptr;
        MicFrameCallback m_MicCb;
        std::vector<int16_t> m_MicAccum;
        size_t m_MicReadPos = 0;
        uint8_t m_MicOutBuf[1024]{};
    };
}
