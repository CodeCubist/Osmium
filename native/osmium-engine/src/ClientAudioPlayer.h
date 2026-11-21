#pragma once

#include "OsmiumCommon.h"
#include <SDL3/SDL.h>
#include <opus.h>
#include <mutex>
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
        void PushOpusFrame(const uint8_t* data, int len, int sampleRate, int channels, int sampleCount);

    private:
        SDL_AudioStream* m_AudioStream = nullptr;
        OpusDecoder* m_Opus = nullptr;
        int m_SampleRate = 48000;
        int m_Channels = 2;
        bool m_Muted = false;
        std::vector<float> m_PcmBuf;
        std::mutex m_Mutex;
    };
}
