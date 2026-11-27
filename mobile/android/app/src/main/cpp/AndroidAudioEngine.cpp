#include "AndroidAudioEngine.h"

#include <android/log.h>
#include <sys/resource.h>
#include <algorithm>
#include <cstring>
#include <dlfcn.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "OsmiumAudio", __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, "OsmiumAudio", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "OsmiumAudio", __VA_ARGS__)

namespace osmium::mobile
{
    namespace
    {
        constexpr size_t kMaxPendingOpusFrames = 8;
        constexpr size_t kMaxPooledOpusBufs = 16;
        constexpr int kMicSampleRate = 48000;
        constexpr int kMicChannels = 1;
        constexpr int kMicFrameSamples = 960; // 20ms @ 48kHz
    }

    AndroidAudioEngine::~AndroidAudioEngine()
    {
        StopAll();
    }

    void AndroidAudioEngine::SetMuted(bool muted)
    {
        const bool prev = m_Muted.exchange(muted, std::memory_order_acq_rel);
        if (muted && !prev)
        {
            {
                std::lock_guard<std::mutex> ql(m_QueueMutex);
                while (!m_PendingQueue.empty())
                {
                    auto dropped = std::move(m_PendingQueue.front());
                    m_PendingQueue.pop_front();
                    if (m_FreeBuffers.size() < kMaxPooledOpusBufs && dropped.data.capacity() <= 4096)
                    {
                        dropped.data.clear();
                        m_FreeBuffers.push_back(std::move(dropped.data));
                    }
                }
            }
            // Wake worker immediately so it closes the AAudio stream while muted.
            m_QueueCv.notify_one();
        }
    }

    void AndroidAudioEngine::StopAll()
    {
        StopMicCapture();

        std::thread workerToJoin;
        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);
            m_DecodeStop = true;
            m_PendingQueue.clear();
            m_FreeBuffers.clear();
            if (m_DecodeThread.joinable())
            {
                workerToJoin = std::move(m_DecodeThread);
            }
        }
        m_QueueCv.notify_all();
        if (workerToJoin.joinable())
        {
            workerToJoin.join();
        }
        StopPlayback();
    }

    void AndroidAudioEngine::StopPlayback()
    {
        if (m_PlayStream)
        {
            AAudioStream_requestStop(m_PlayStream);
            AAudioStream_close(m_PlayStream);
            m_PlayStream = nullptr;
        }
        if (m_Decoder)
        {
            opus_decoder_destroy(m_Decoder);
            m_Decoder = nullptr;
        }
        m_PlaySampleRate = 0;
        m_PlayChannels.store(2, std::memory_order_relaxed);
        m_HasSequence = false;
        m_LastSequence = 0;
        m_RingRead.store(0, std::memory_order_relaxed);
        m_RingWrite.store(0, std::memory_order_relaxed);
    }

    bool AndroidAudioEngine::EnsurePlayback(uint16_t sampleRate, uint8_t channels)
    {
        if (sampleRate == 0) sampleRate = 48000;
        if (channels == 0 || channels > 2) channels = 2;

        const bool needRebuild = m_StreamNeedsRebuild.exchange(false, std::memory_order_acq_rel);
        if (!needRebuild && m_PlayStream && m_Decoder &&
            m_PlaySampleRate == sampleRate &&
            m_PlayChannels.load(std::memory_order_relaxed) == channels)
        {
            return true;
        }

        StopPlayback();

        int err = 0;
        m_Decoder = opus_decoder_create(sampleRate, channels, &err);
        if (!m_Decoder || err != OPUS_OK)
        {
            LOGE("opus_decoder_create failed: %d", err);
            return false;
        }

        m_PcmRing.assign(kRingCapacity, 0);
        m_RingRead.store(0, std::memory_order_relaxed);
        m_RingWrite.store(0, std::memory_order_relaxed);
        m_PlayChannels.store(channels, std::memory_order_release);
        m_DecodeScratch.resize(5760 * channels);

        AAudioStreamBuilder* builder = nullptr;
        if (AAudio_createStreamBuilder(&builder) != AAUDIO_OK || !builder)
        {
            LOGE("AAudio_createStreamBuilder failed");
            StopPlayback();
            return false;
        }

        // Use standard shared MEDIA playback (AAUDIO_PERFORMANCE_MODE_NONE + AAUDIO_USAGE_MEDIA)
        // instead of LOW_LATENCY + USAGE_GAME: our network Opus packets arrive every 20ms, so
        // 2-4ms FastMixer/MMAP SCHED_FIFO interrupts give zero latency benefit while triggering
        // aggressive OEM Game-Audio DSP/Wi-Fi coexistence modes and 2.4GHz Wi-Fi IRQ starvation.
        AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
        AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
        AAudioStreamBuilder_setSampleRate(builder, sampleRate);
        AAudioStreamBuilder_setChannelCount(builder, channels);
        AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
        AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_NONE);
        using SetInt32Fn = void (*)(AAudioStreamBuilder*, int32_t);
        if (auto fn = reinterpret_cast<SetInt32Fn>(dlsym(RTLD_DEFAULT, "AAudioStreamBuilder_setUsage")))
        {
            fn(builder, AAUDIO_USAGE_MEDIA);
        }
        if (auto fn = reinterpret_cast<SetInt32Fn>(dlsym(RTLD_DEFAULT, "AAudioStreamBuilder_setContentType")))
        {
            fn(builder, AAUDIO_CONTENT_TYPE_MUSIC);
        }
        AAudioStreamBuilder_setDataCallback(builder, &AndroidAudioEngine::PlaybackCallback, this);
        AAudioStreamBuilder_setErrorCallback(builder, &AndroidAudioEngine::PlaybackErrorCallback, this);

        aaudio_result_t res = AAudioStreamBuilder_openStream(builder, &m_PlayStream);
        AAudioStreamBuilder_delete(builder);
        if (res != AAUDIO_OK || !m_PlayStream)
        {
            LOGE("AAudio open output stream failed: %d", res);
            StopPlayback();
            return false;
        }

        // Keep AAudio's internal buffer around 2 bursts (~40-60ms) to absorb Wi-Fi jitter cleanly.
        const int32_t burst = std::max<int32_t>(1, AAudioStream_getFramesPerBurst(m_PlayStream));
        const int32_t capacity = AAudioStream_getBufferCapacityInFrames(m_PlayStream);
        const int32_t targetFrames = std::clamp<int32_t>(
            std::max<int32_t>(burst * 2, static_cast<int32_t>(sampleRate) / 25),
            burst,
            std::max<int32_t>(burst, capacity));
        AAudioStream_setBufferSizeInFrames(m_PlayStream, targetFrames);

        res = AAudioStream_requestStart(m_PlayStream);
        if (res != AAUDIO_OK)
        {
            LOGE("AAudio start output stream failed: %d", res);
            StopPlayback();
            return false;
        }

        m_PlaySampleRate = sampleRate;
        m_HasSequence = false;
        LOGI("AAudio output started (%u Hz, %u ch, media mode, burst=%d, buf=%d)",
             sampleRate, channels, burst, AAudioStream_getBufferSizeInFrames(m_PlayStream));
        return true;
    }

    void AndroidAudioEngine::PlaybackErrorCallback(
        AAudioStream* /*stream*/,
        void* userData,
        aaudio_result_t error)
    {
        auto* self = static_cast<AndroidAudioEngine*>(userData);
        LOGW("AAudio output stream error/disconnected (%d); scheduling rebuild", static_cast<int>(error));
        self->m_StreamNeedsRebuild.store(true, std::memory_order_release);
        self->m_QueueCv.notify_one();
    }

    aaudio_data_callback_result_t AndroidAudioEngine::PlaybackCallback(
        AAudioStream* /*stream*/,
        void* userData,
        void* audioData,
        int32_t numFrames)
    {
        auto* self = static_cast<AndroidAudioEngine*>(userData);
        auto* out = static_cast<int16_t*>(audioData);
        const uint8_t ch = std::max<uint8_t>(1, self->m_PlayChannels.load(std::memory_order_acquire));
        const uint32_t totalSamples = static_cast<uint32_t>(numFrames) * ch;

        const uint32_t r = self->m_RingRead.load(std::memory_order_relaxed);
        const uint32_t w = self->m_RingWrite.load(std::memory_order_acquire);
        const uint32_t avail = w - r;

        if (self->m_Muted.load(std::memory_order_relaxed))
        {
            std::memset(out, 0, static_cast<size_t>(totalSamples) * sizeof(int16_t));
            const uint32_t discard = std::min(totalSamples, avail);
            self->m_RingRead.store(r + discard, std::memory_order_release);
            return AAUDIO_CALLBACK_RESULT_CONTINUE;
        }

        if (avail == 0 || self->m_PcmRing.size() < kRingCapacity)
        {
            std::memset(out, 0, static_cast<size_t>(totalSamples) * sizeof(int16_t));
            return AAUDIO_CALLBACK_RESULT_CONTINUE;
        }

        const uint32_t toRead = std::min(totalSamples, avail);
        const uint32_t readIdx = r & kRingMask;
        const uint32_t firstPart = std::min(toRead, kRingCapacity - readIdx);
        std::memcpy(out, self->m_PcmRing.data() + readIdx, static_cast<size_t>(firstPart) * sizeof(int16_t));
        if (toRead > firstPart)
        {
            std::memcpy(out + firstPart, self->m_PcmRing.data(),
                        static_cast<size_t>(toRead - firstPart) * sizeof(int16_t));
        }
        self->m_RingRead.store(r + toRead, std::memory_order_release);

        if (toRead < totalSamples)
        {
            std::memset(out + toRead, 0, static_cast<size_t>(totalSamples - toRead) * sizeof(int16_t));
        }
        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }

    void AndroidAudioEngine::EnsureDecodeThreadLocked()
    {
        if (!m_DecodeThread.joinable())
        {
            m_DecodeStop = false;
            m_DecodeThread = std::thread(&AndroidAudioEngine::DecodeWorkerLoop, this);
        }
    }

    void AndroidAudioEngine::PushOpusFrame(
        const uint8_t* data,
        uint16_t bytes,
        uint16_t sampleRate,
        uint8_t channels,
        uint16_t sampleCount,
        uint32_t sequence)
    {
        if (!data || bytes == 0 || bytes > 4096) return;
        if (m_Muted.load(std::memory_order_relaxed)) return;

        {
            std::lock_guard<std::mutex> ql(m_QueueMutex);
            EnsureDecodeThreadLocked();
            while (m_PendingQueue.size() >= kMaxPendingOpusFrames)
            {
                auto dropped = std::move(m_PendingQueue.front());
                m_PendingQueue.pop_front();
                if (m_FreeBuffers.size() < kMaxPooledOpusBufs && dropped.data.capacity() <= 4096)
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
            frame.data.assign(data, data + bytes);
            m_PendingQueue.push_back(std::move(frame));
        }
        m_QueueCv.notify_one();
    }

    void AndroidAudioEngine::DecodeWorkerLoop()
    {
        // Keep audio decode strictly below RecvLoop (-10) and DecodeLoop (-6)
        // so audio work never preempts network socket draining or video frame rendering.
        setpriority(PRIO_PROCESS, 0, -2);

        for (;;)
        {
            PendingOpusFrame frame;
            bool hasFrame = false;
            {
                std::unique_lock<std::mutex> ql(m_QueueMutex);
                if (m_PlayStream)
                {
                    m_QueueCv.wait_for(ql, std::chrono::milliseconds(1500), [this]
                    {
                        return m_DecodeStop || !m_PendingQueue.empty() ||
                            m_Muted.load(std::memory_order_acquire) ||
                            m_StreamNeedsRebuild.load(std::memory_order_acquire);
                    });
                }
                else
                {
                    m_QueueCv.wait(ql, [this] { return m_DecodeStop || !m_PendingQueue.empty(); });
                }
                if (m_DecodeStop) break;
                if (!m_PendingQueue.empty())
                {
                    frame = std::move(m_PendingQueue.front());
                    m_PendingQueue.pop_front();
                    hasFrame = true;
                }
            }

            if (!hasFrame)
            {
                // Close AAudio when muted, disconnected, or silent for >= 1.5s so Android
                // releases the audio HAL/mixer completely while no audio is playing.
                if (m_PlayStream)
                {
                    m_StreamNeedsRebuild.store(false, std::memory_order_release);
                    StopPlayback();
                }
                continue;
            }

            if (m_Muted.load(std::memory_order_acquire))
            {
                if (m_PlayStream) StopPlayback();
            }
            else
            {
                ProcessOpusFrame(frame);
            }

            if (frame.data.capacity() <= 4096)
            {
                std::lock_guard<std::mutex> ql(m_QueueMutex);
                if (m_FreeBuffers.size() < kMaxPooledOpusBufs)
                {
                    frame.data.clear();
                    m_FreeBuffers.push_back(std::move(frame.data));
                }
            }
        }
    }

    void AndroidAudioEngine::WritePcmRing(const int16_t* samples, size_t count)
    {
        if (!samples || count == 0 || m_PcmRing.size() < kRingCapacity) return;

        const uint32_t r = m_RingRead.load(std::memory_order_acquire);
        const uint32_t w = m_RingWrite.load(std::memory_order_relaxed);
        const uint32_t used = w - r;

        // Keep buffered playback audio below ~105ms by smoothly skipping excess producer
        // frames instead of mutating the consumer's read pointer.
        const uint32_t ch = std::max<uint32_t>(1, m_PlayChannels.load(std::memory_order_relaxed));
        const uint32_t rate = m_PlaySampleRate > 0 ? m_PlaySampleRate : 48000U;
        const uint32_t maxBufferedSamples = (rate * ch * 105U) / 1000U;
        if (used + count > maxBufferedSamples || used + count > kRingCapacity)
        {
            return;
        }

        const uint32_t toWrite = static_cast<uint32_t>(count);
        const uint32_t writeIdx = w & kRingMask;
        const uint32_t firstPart = std::min(toWrite, kRingCapacity - writeIdx);
        std::memcpy(m_PcmRing.data() + writeIdx, samples, static_cast<size_t>(firstPart) * sizeof(int16_t));
        if (toWrite > firstPart)
        {
            std::memcpy(m_PcmRing.data(), samples + firstPart,
                        static_cast<size_t>(toWrite - firstPart) * sizeof(int16_t));
        }
        m_RingWrite.store(w + toWrite, std::memory_order_release);
    }

    void AndroidAudioEngine::ProcessOpusFrame(const PendingOpusFrame& frame)
    {
        if (frame.data.empty() || m_Muted.load(std::memory_order_relaxed)) return;
        if (!EnsurePlayback(frame.sampleRate, frame.channels)) return;

        const uint8_t ch = m_PlayChannels.load(std::memory_order_relaxed);
        const int maxPerCh = static_cast<int>(m_DecodeScratch.size() / std::max<uint8_t>(1, ch));

        // Single-packet loss concealment (in-band FEC) if sequence jumped by 2 and buffer isn't backed up.
        if (frame.sequence != 0 && m_HasSequence)
        {
            const uint32_t diff = frame.sequence - m_LastSequence;
            const uint32_t used = m_RingWrite.load(std::memory_order_relaxed) -
                m_RingRead.load(std::memory_order_acquire);
            const uint32_t rate = m_PlaySampleRate > 0 ? m_PlaySampleRate : 48000U;
            if (diff == 2 && used < (rate * ch * 70U) / 1000U)
            {
                int fecFrames = opus_decode(
                    m_Decoder,
                    frame.data.data(),
                    static_cast<int>(frame.data.size()),
                    m_DecodeScratch.data(),
                    std::min<int>(frame.sampleCount, maxPerCh),
                    1);
                if (fecFrames > 0)
                {
                    WritePcmRing(m_DecodeScratch.data(), static_cast<size_t>(fecFrames) * ch);
                }
            }
        }
        if (frame.sequence != 0)
        {
            m_LastSequence = frame.sequence;
            m_HasSequence = true;
        }

        int decodedFrames = opus_decode(
            m_Decoder,
            frame.data.data(),
            static_cast<int>(frame.data.size()),
            m_DecodeScratch.data(),
            maxPerCh,
            0);
        if (decodedFrames <= 0) return;

        WritePcmRing(m_DecodeScratch.data(), static_cast<size_t>(decodedFrames) * ch);
    }

    bool AndroidAudioEngine::StartMicCapture(MicFrameCallback callback)
    {
        std::lock_guard<std::mutex> lock(m_MicMutex);
        if (m_MicStream) return true;

        int err = 0;
        m_Encoder = opus_encoder_create(kMicSampleRate, kMicChannels, OPUS_APPLICATION_VOIP, &err);
        if (!m_Encoder || err != OPUS_OK)
        {
            LOGE("opus_encoder_create failed: %d", err);
            return false;
        }
        opus_encoder_ctl(m_Encoder, OPUS_SET_BITRATE(32000));
        opus_encoder_ctl(m_Encoder, OPUS_SET_COMPLEXITY(5));
        opus_encoder_ctl(m_Encoder, OPUS_SET_INBAND_FEC(1));
        opus_encoder_ctl(m_Encoder, OPUS_SET_PACKET_LOSS_PERC(5));
        opus_encoder_ctl(m_Encoder, OPUS_SET_DTX(1));

        m_MicCb = std::move(callback);
        m_MicAccum.clear();
        m_MicAccum.reserve(kMicFrameSamples * 4);
        m_MicReadPos = 0;

        AAudioStreamBuilder* builder = nullptr;
        if (AAudio_createStreamBuilder(&builder) != AAUDIO_OK || !builder)
        {
            opus_encoder_destroy(m_Encoder);
            m_Encoder = nullptr;
            return false;
        }

        AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_INPUT);
        AAudioStreamBuilder_setSampleRate(builder, kMicSampleRate);
        AAudioStreamBuilder_setChannelCount(builder, kMicChannels);
        AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
        using SetInputPresetFn = void (*)(AAudioStreamBuilder*, int32_t);
        if (auto fn = reinterpret_cast<SetInputPresetFn>(dlsym(RTLD_DEFAULT, "AAudioStreamBuilder_setInputPreset")))
        {
            fn(builder, AAUDIO_INPUT_PRESET_VOICE_COMMUNICATION);
        }
        AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
        AAudioStreamBuilder_setDataCallback(builder, &AndroidAudioEngine::MicCallback, this);

        aaudio_result_t res = AAudioStreamBuilder_openStream(builder, &m_MicStream);
        AAudioStreamBuilder_delete(builder);
        if (res != AAUDIO_OK || !m_MicStream)
        {
            LOGW("AAudio mic stream open failed: %d", res);
            opus_encoder_destroy(m_Encoder);
            m_Encoder = nullptr;
            return false;
        }

        if (AAudioStream_requestStart(m_MicStream) != AAUDIO_OK)
        {
            AAudioStream_close(m_MicStream);
            m_MicStream = nullptr;
            opus_encoder_destroy(m_Encoder);
            m_Encoder = nullptr;
            return false;
        }

        LOGI("AAudio mic capture started");
        return true;
    }

    void AndroidAudioEngine::StopMicCapture()
    {
        std::lock_guard<std::mutex> lock(m_MicMutex);
        if (m_MicStream)
        {
            AAudioStream_requestStop(m_MicStream);
            AAudioStream_close(m_MicStream);
            m_MicStream = nullptr;
        }
        if (m_Encoder)
        {
            opus_encoder_destroy(m_Encoder);
            m_Encoder = nullptr;
        }
        m_MicCb = nullptr;
        m_MicAccum.clear();
        m_MicReadPos = 0;
    }

    aaudio_data_callback_result_t AndroidAudioEngine::MicCallback(
        AAudioStream* /*stream*/,
        void* userData,
        void* audioData,
        int32_t numFrames)
    {
        auto* self = static_cast<AndroidAudioEngine*>(userData);
        if (!self->m_Encoder || !self->m_MicCb || numFrames <= 0)
            return AAUDIO_CALLBACK_RESULT_CONTINUE;

        const auto* in = static_cast<const int16_t*>(audioData);
        self->m_MicAccum.insert(self->m_MicAccum.end(), in, in + numFrames);

        while (self->m_MicAccum.size() - self->m_MicReadPos >= kMicFrameSamples)
        {
            int encoded = opus_encode(
                self->m_Encoder,
                self->m_MicAccum.data() + self->m_MicReadPos,
                kMicFrameSamples,
                self->m_MicOutBuf,
                sizeof(self->m_MicOutBuf));
            self->m_MicReadPos += kMicFrameSamples;
            if (encoded > 0)
            {
                self->m_MicCb(
                    self->m_MicOutBuf,
                    static_cast<uint16_t>(encoded),
                    kMicSampleRate,
                    kMicChannels,
                    kMicFrameSamples);
            }
        }
        if (self->m_MicReadPos == self->m_MicAccum.size())
        {
            self->m_MicAccum.clear();
            self->m_MicReadPos = 0;
        }
        else if (self->m_MicReadPos >= static_cast<size_t>(kMicFrameSamples) * 4)
        {
            self->m_MicAccum.erase(self->m_MicAccum.begin(),
                                   self->m_MicAccum.begin() + static_cast<ptrdiff_t>(self->m_MicReadPos));
            self->m_MicReadPos = 0;
        }
        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }
}
