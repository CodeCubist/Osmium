#include "ClientAudioPlayer.h"

namespace osmium
{
    ClientAudioPlayer::~ClientAudioPlayer()
    {
        Shutdown();
    }

    bool ClientAudioPlayer::Initialize()
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        if (!SDL_WasInit(SDL_INIT_AUDIO))
        {
            if (!SDL_InitSubSystem(SDL_INIT_AUDIO))
            {
                OSM_LOG_WARN("SDL_InitSubSystem(AUDIO) failed: %s", SDL_GetError());
                return false;
            }
        }

        int err = OPUS_OK;
        m_Opus = opus_decoder_create(48000, 2, &err);
        if (!m_Opus || err != OPUS_OK)
        {
            OSM_LOG_ERROR("opus_decoder_create failed: %d", err);
            return false;
        }

        SDL_AudioSpec spec{};
        spec.format = SDL_AUDIO_F32;
        spec.channels = 2;
        spec.freq = 48000;
        m_AudioStream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, nullptr, nullptr);
        if (!m_AudioStream)
        {
            OSM_LOG_WARN("SDL_OpenAudioDeviceStream failed: %s", SDL_GetError());
            return false;
        }

        SDL_ResumeAudioStreamDevice(m_AudioStream);
        m_PcmBuf.resize(5760 * 2);
        return true;
    }

    void ClientAudioPlayer::Shutdown()
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
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
        std::lock_guard<std::mutex> lock(m_Mutex);
        m_Muted = muted;
        if (m_AudioStream && muted)
        {
            SDL_ClearAudioStream(m_AudioStream);
        }
    }

    void ClientAudioPlayer::PushOpusFrame(const uint8_t* data, int len, int sampleRate, int channels, int sampleCount)
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        if (!m_AudioStream || !m_Opus || !data || len <= 0) return;

        if (sampleRate != m_SampleRate || channels != m_Channels)
        {
            opus_decoder_destroy(m_Opus);
            int err = OPUS_OK;
            m_Opus = opus_decoder_create(sampleRate, channels, &err);
            if (!m_Opus || err != OPUS_OK) return;
            m_SampleRate = sampleRate;
            m_Channels = channels;
            SDL_AudioSpec srcSpec{SDL_AUDIO_F32, channels, sampleRate};
            SDL_AudioSpec dstSpec{SDL_AUDIO_F32, 2, 48000};
            SDL_SetAudioStreamFormat(m_AudioStream, &srcSpec, &dstSpec);
        }

        int maxSamples = std::max(sampleCount, 1920);
        if (static_cast<int>(m_PcmBuf.size()) < maxSamples * channels)
        {
            m_PcmBuf.resize(static_cast<size_t>(maxSamples) * channels);
        }

        int decoded = opus_decode_float(m_Opus, data, len, m_PcmBuf.data(), maxSamples, 0);
        if (decoded <= 0 || m_Muted) return;

        int queued = SDL_GetAudioStreamQueued(m_AudioStream);
        int bytesPerSec = 48000 * 2 * static_cast<int>(sizeof(float));
        if (queued > (bytesPerSec * 60) / 1000)
        {
            SDL_ClearAudioStream(m_AudioStream);
        }

        SDL_PutAudioStreamData(m_AudioStream, m_PcmBuf.data(), decoded * channels * static_cast<int>(sizeof(float)));
    }
}
