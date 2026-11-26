#include "ClientAudioPlayer.h"
#include <algorithm>
#include <cstring>

namespace osmium
{
    namespace
    {
        constexpr size_t kMaxPendingAudioFrames = 8;
        constexpr size_t kMaxPooledAudioBufs = 16;
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
            opus_decoder_destroy(m_Opus);
            m_Opus = nullptr;
            return false;
        }

        SDL_ResumeAudioStreamDevice(m_AudioStream);
        m_SampleRate = 48000;
        m_Channels = 2;
        m_LastSequence = 0;
        m_HasSequence = false;
        m_PendingClear.store(false, std::memory_order_relaxed);
        m_PcmBuf.resize(5760 * 2);

        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);
            m_PendingQueue.clear();
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
            m_PendingQueue.clear();
            m_FreeBuffers.clear();
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
                while (!m_PendingQueue.empty())
                {
                    auto frame = std::move(m_PendingQueue.front());
                    m_PendingQueue.pop_front();
                    if (m_FreeBuffers.size() < kMaxPooledAudioBufs && frame.data.capacity() <= 4096)
                    {
                        frame.data.clear();
                        m_FreeBuffers.push_back(std::move(frame.data));
                    }
                }
            }
            m_QueueCv.notify_one();
        }
    }

    void ClientAudioPlayer::PushOpusFrame(const uint8_t* data, int len, int sampleRate, int channels, int sampleCount,
                                          uint32_t sequence)
    {
        if (!m_Initialized.load(std::memory_order_acquire) || !data || len <= 0 || len > 4096) return;
        if (m_Muted.load(std::memory_order_acquire)) return;

        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);
            while (m_PendingQueue.size() >= kMaxPendingAudioFrames)
            {
                auto dropped = std::move(m_PendingQueue.front());
                m_PendingQueue.pop_front();
                if (m_FreeBuffers.size() < kMaxPooledAudioBufs && dropped.data.capacity() <= 4096)
                {
                    dropped.data.clear();
                    m_FreeBuffers.push_back(std::move(dropped.data));
                }
            }

            PendingOpusFrame frame;
            frame.sequence = sequence;
            frame.sampleRate = sampleRate > 0 ? sampleRate : 48000;
            frame.channels = (channels == 1 || channels == 2) ? channels : 2;
            frame.sampleCount = sampleCount > 0 ? sampleCount : 960;
            if (!m_FreeBuffers.empty())
            {
                frame.data = std::move(m_FreeBuffers.back());
                m_FreeBuffers.pop_back();
            }
            frame.data.assign(data, data + len);
            m_PendingQueue.push_back(std::move(frame));
        }
        m_QueueCv.notify_one();
    }

    void ClientAudioPlayer::WorkerLoop()
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);

        while (!m_StopFlag.load(std::memory_order_acquire))
        {
            PendingOpusFrame frame;
            bool hasFrame = false;
            {
                std::unique_lock<std::mutex> ql(m_QueueMutex);
                m_QueueCv.wait(ql, [this]
                {
                    return m_StopFlag.load(std::memory_order_acquire) ||
                        m_PendingClear.load(std::memory_order_acquire) ||
                        !m_PendingQueue.empty();
                });

                if (m_StopFlag.load(std::memory_order_acquire)) break;

                if (m_PendingClear.exchange(false, std::memory_order_acq_rel))
                {
                    ql.unlock();
                    if (m_AudioStream)
                    {
                        SDL_ClearAudioStream(m_AudioStream);
                    }
                    m_HasSequence = false;
                    continue;
                }

                if (!m_PendingQueue.empty())
                {
                    frame = std::move(m_PendingQueue.front());
                    m_PendingQueue.pop_front();
                    hasFrame = true;
                }
            }

            if (hasFrame)
            {
                ProcessFrame(frame);
                if (frame.data.capacity() <= 4096)
                {
                    std::lock_guard<std::mutex> ql(m_QueueMutex);
                    if (m_FreeBuffers.size() < kMaxPooledAudioBufs)
                    {
                        frame.data.clear();
                        m_FreeBuffers.push_back(std::move(frame.data));
                    }
                }
            }
        }
    }

    void ClientAudioPlayer::ProcessFrame(const PendingOpusFrame& frame)
    {
        if (!m_AudioStream || !m_Opus || frame.data.empty() || m_Muted.load(std::memory_order_acquire)) return;

        if (frame.sampleRate != m_SampleRate || frame.channels != m_Channels)
        {
            opus_decoder_destroy(m_Opus);
            int err = OPUS_OK;
            m_Opus = opus_decoder_create(frame.sampleRate, frame.channels, &err);
            if (!m_Opus || err != OPUS_OK)
            {
                m_Opus = nullptr;
                return;
            }
            m_SampleRate = frame.sampleRate;
            m_Channels = frame.channels;
            m_HasSequence = false;
            SDL_AudioSpec srcSpec{SDL_AUDIO_F32, frame.channels, frame.sampleRate};
            SDL_AudioSpec dstSpec{SDL_AUDIO_F32, 2, 48000};
            SDL_SetAudioStreamFormat(m_AudioStream, &srcSpec, &dstSpec);
        }

        const int maxSamples = std::max(frame.sampleCount, 1920);
        if (static_cast<int>(m_PcmBuf.size()) < maxSamples * m_Channels)
        {
            m_PcmBuf.resize(static_cast<size_t>(maxSamples) * m_Channels);
        }

        const int bytesPerSec = 48000 * 2 * static_cast<int>(sizeof(float));
        int queued = SDL_GetAudioStreamQueued(m_AudioStream);

        // Only hard-clear on severe runaway backlog (>140ms) to avoid constant audible
        // drop-to-zero stalls that occurred with the old 60ms hard-clear threshold.
        if (queued > (bytesPerSec * 140) / 1000)
        {
            SDL_ClearAudioStream(m_AudioStream);
            queued = 0;
        }

        // Single-packet loss concealment (in-band FEC) when sequence has a 1-packet gap
        // and playback buffer is not already backed up.
        if (frame.sequence != 0 && m_HasSequence)
        {
            const uint32_t diff = frame.sequence - m_LastSequence;
            if (diff == 2 && queued < (bytesPerSec * 70) / 1000)
            {
                int plcDecoded = opus_decode_float(
                    m_Opus,
                    frame.data.data(),
                    static_cast<int>(frame.data.size()),
                    m_PcmBuf.data(),
                    frame.sampleCount,
                    1);
                if (plcDecoded > 0)
                {
                    SDL_PutAudioStreamData(
                        m_AudioStream,
                        m_PcmBuf.data(),
                        plcDecoded * m_Channels * static_cast<int>(sizeof(float)));
                    queued += plcDecoded * 2 * static_cast<int>(sizeof(float));
                }
            }
        }
        if (frame.sequence != 0)
        {
            m_LastSequence = frame.sequence;
            m_HasSequence = true;
        }

        int decoded = opus_decode_float(
            m_Opus,
            frame.data.data(),
            static_cast<int>(frame.data.size()),
            m_PcmBuf.data(),
            maxSamples,
            0);
        if (decoded <= 0 || m_Muted.load(std::memory_order_acquire)) return;

        // Soft drift trim: if queued audio is already > 95ms, drop this frame
        // after updating decoder state so latency smoothly converges back to ~40-60ms.
        if (queued > (bytesPerSec * 95) / 1000)
        {
            return;
        }

        SDL_PutAudioStreamData(
            m_AudioStream,
            m_PcmBuf.data(),
            decoded * m_Channels * static_cast<int>(sizeof(float)));
    }
}
