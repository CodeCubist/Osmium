#pragma once

#include "OsmiumCommon.h"
#include <SDL3/SDL.h>
#include <opus.h>
#include <atomic>
#include <condition_variable>
#include <deque>
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
        void PushOpusFrame(const uint8_t* data, int len, int sampleRate, int channels, int sampleCount,
                           uint32_t sequence = 0);

    private:
        struct PendingOpusFrame
        {
            uint32_t sequence = 0;
            int sampleRate = 48000;
            int channels = 2;
            int sampleCount = 960;
            std::vector<uint8_t> data;
        };

        void WorkerLoop();
        void ProcessFrame(const PendingOpusFrame& frame);

        SDL_AudioStream* m_AudioStream = nullptr;
        OpusDecoder* m_Opus = nullptr;
        int m_SampleRate = 48000;
        int m_Channels = 2;
        uint32_t m_LastSequence = 0;
        bool m_HasSequence = false;

        std::atomic<bool> m_Initialized{false};
        std::atomic<bool> m_Muted{false};
        std::atomic<bool> m_PendingClear{false};
        std::atomic<bool> m_StopFlag{false};

        std::vector<float> m_PcmBuf;

        std::mutex m_QueueMutex;
        std::condition_variable m_QueueCv;
        std::deque<PendingOpusFrame> m_PendingQueue;
        std::vector<std::vector<uint8_t>> m_FreeBuffers;
        std::thread m_WorkerThread;
        std::mutex m_LifecycleMutex;
    };
}
