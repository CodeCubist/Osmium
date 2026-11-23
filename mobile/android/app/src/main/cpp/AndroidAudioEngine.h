#pragma once

#include <aaudio/AAudio.h>
#include <opus.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
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

        void SetMuted(bool muted) { m_Muted.store(muted, std::memory_order_relaxed); }
        bool IsMuted() const { return m_Muted.load(std::memory_order_relaxed); }

        void PushOpusFrame(
            const uint8_t* data,
            uint16_t bytes,
            uint16_t sampleRate,
            uint8_t channels,
            uint16_t sampleCount);

        bool StartMicCapture(MicFrameCallback callback);
        void StopMicCapture();

        void StopAll();

    private:
        bool EnsurePlaybackLocked(uint16_t sampleRate, uint8_t channels);
        void StopPlaybackLocked();

        static aaudio_data_callback_result_t PlaybackCallback(
            AAudioStream* stream,
            void* userData,
            void* audioData,
            int32_t numFrames);

        static aaudio_data_callback_result_t MicCallback(
            AAudioStream* stream,
            void* userData,
            void* audioData,
            int32_t numFrames);

        std::mutex m_PlayMutex;
        OpusDecoder* m_Decoder = nullptr;
        AAudioStream* m_PlayStream = nullptr;
        uint16_t m_PlaySampleRate = 0;
        uint8_t m_PlayChannels = 0;
        std::atomic<bool> m_Muted{false};

        // Simple lock-protected PCM ring buffer for low-latency playback
        std::mutex m_RingMutex;
        std::vector<int16_t> m_PcmRing;
        size_t m_RingRead = 0;
        size_t m_RingWrite = 0;
        size_t m_RingCount = 0;
        std::vector<int16_t> m_DecodeScratch;

        std::mutex m_MicMutex;
        OpusEncoder* m_Encoder = nullptr;
        AAudioStream* m_MicStream = nullptr;
        MicFrameCallback m_MicCb;
        std::vector<int16_t> m_MicAccum;
        uint8_t m_MicOutBuf[1024]{};
    };
}
