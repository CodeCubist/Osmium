#include "AndroidAudioEngine.h"

#include <android/log.h>
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
        constexpr size_t kRingCapacitySamples = 48000 * 2 / 4; // 250ms @ 48kHz stereo
        constexpr size_t kMaxBufferedSamples = 48000 * 2 / 10; // Cap latency ~100ms
        constexpr int kMicSampleRate = 48000;
        constexpr int kMicChannels = 1;
        constexpr int kMicFrameSamples = 960; // 20ms @ 48kHz
    }

    AndroidAudioEngine::~AndroidAudioEngine()
    {
        StopAll();
    }

    void AndroidAudioEngine::StopAll()
    {
        StopMicCapture();
        std::lock_guard<std::mutex> lock(m_PlayMutex);
        StopPlaybackLocked();
    }

    void AndroidAudioEngine::StopPlaybackLocked()
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
        m_PlayChannels = 0;
        std::lock_guard<std::mutex> rl(m_RingMutex);
        m_RingRead = m_RingWrite = m_RingCount = 0;
    }

    bool AndroidAudioEngine::EnsurePlaybackLocked(uint16_t sampleRate, uint8_t channels)
    {
        if (sampleRate == 0) sampleRate = 48000;
        if (channels == 0 || channels > 2) channels = 2;

        if (m_PlayStream && m_Decoder &&
            m_PlaySampleRate == sampleRate && m_PlayChannels == channels)
        {
            return true;
        }

        StopPlaybackLocked();

        int err = 0;
        m_Decoder = opus_decoder_create(sampleRate, channels, &err);
        if (!m_Decoder || err != OPUS_OK)
        {
            LOGE("opus_decoder_create failed: %d", err);
            return false;
        }

        {
            std::lock_guard<std::mutex> rl(m_RingMutex);
            m_PcmRing.assign(kRingCapacitySamples, 0);
            m_RingRead = m_RingWrite = m_RingCount = 0;
        }
        m_DecodeScratch.resize(5760 * channels);

        AAudioStreamBuilder* builder = nullptr;
        if (AAudio_createStreamBuilder(&builder) != AAUDIO_OK || !builder)
        {
            LOGE("AAudio_createStreamBuilder failed");
            StopPlaybackLocked();
            return false;
        }

        AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
        AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
        AAudioStreamBuilder_setSampleRate(builder, sampleRate);
        AAudioStreamBuilder_setChannelCount(builder, channels);
        AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
        AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
        using SetUsageFn = void (*)(AAudioStreamBuilder*, int32_t);
        if (auto fn = reinterpret_cast<SetUsageFn>(dlsym(RTLD_DEFAULT, "AAudioStreamBuilder_setUsage")))
        {
            fn(builder, AAUDIO_USAGE_GAME);
        }
        AAudioStreamBuilder_setDataCallback(builder, &AndroidAudioEngine::PlaybackCallback, this);

        aaudio_result_t res = AAudioStreamBuilder_openStream(builder, &m_PlayStream);
        AAudioStreamBuilder_delete(builder);
        if (res != AAUDIO_OK || !m_PlayStream)
        {
            LOGE("AAudio open output stream failed: %d", res);
            StopPlaybackLocked();
            return false;
        }

        res = AAudioStream_requestStart(m_PlayStream);
        if (res != AAUDIO_OK)
        {
            LOGE("AAudio start output stream failed: %d", res);
            StopPlaybackLocked();
            return false;
        }

        m_PlaySampleRate = sampleRate;
        m_PlayChannels = channels;
        LOGI("AAudio output started (%u Hz, %u ch)", sampleRate, channels);
        return true;
    }

    aaudio_data_callback_result_t AndroidAudioEngine::PlaybackCallback(
        AAudioStream* /*stream*/,
        void* userData,
        void* audioData,
        int32_t numFrames)
    {
        auto* self = static_cast<AndroidAudioEngine*>(userData);
        auto* out = static_cast<int16_t*>(audioData);
        const size_t totalSamples = static_cast<size_t>(numFrames) *
                                    std::max<uint8_t>(1, self->m_PlayChannels);

        if (self->m_Muted.load(std::memory_order_relaxed))
        {
            std::memset(out, 0, totalSamples * sizeof(int16_t));
            std::lock_guard<std::mutex> rl(self->m_RingMutex);
            size_t discard = std::min(totalSamples, self->m_RingCount);
            self->m_RingRead = (self->m_RingRead + discard) % self->m_PcmRing.size();
            self->m_RingCount -= discard;
            return AAUDIO_CALLBACK_RESULT_CONTINUE;
        }

        std::lock_guard<std::mutex> rl(self->m_RingMutex);
        const size_t cap = self->m_PcmRing.size();
        if (cap == 0 || self->m_RingCount == 0)
        {
            std::memset(out, 0, totalSamples * sizeof(int16_t));
            return AAUDIO_CALLBACK_RESULT_CONTINUE;
        }

        size_t toRead = std::min(totalSamples, self->m_RingCount);
        size_t firstPart = std::min(toRead, cap - self->m_RingRead);
        std::memcpy(out, self->m_PcmRing.data() + self->m_RingRead, firstPart * sizeof(int16_t));
        if (toRead > firstPart)
        {
            std::memcpy(out + firstPart, self->m_PcmRing.data(), (toRead - firstPart) * sizeof(int16_t));
        }
        self->m_RingRead = (self->m_RingRead + toRead) % cap;
        self->m_RingCount -= toRead;

        if (toRead < totalSamples)
        {
            std::memset(out + toRead, 0, (totalSamples - toRead) * sizeof(int16_t));
        }
        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }

    void AndroidAudioEngine::PushOpusFrame(
        const uint8_t* data,
        uint16_t bytes,
        uint16_t sampleRate,
        uint8_t channels,
        uint16_t /*sampleCount*/)
    {
        if (!data || bytes == 0) return;
        std::lock_guard<std::mutex> lock(m_PlayMutex);
        if (!EnsurePlaybackLocked(sampleRate, channels)) return;

        int decodedFrames = opus_decode(
            m_Decoder,
            data,
            bytes,
            m_DecodeScratch.data(),
            static_cast<int>(m_DecodeScratch.size() / m_PlayChannels),
            0);
        if (decodedFrames <= 0) return;

        const size_t samples = static_cast<size_t>(decodedFrames) * m_PlayChannels;
        std::lock_guard<std::mutex> rl(m_RingMutex);
        const size_t cap = m_PcmRing.size();
        if (cap == 0) return;

        // Drift catch-up: keep buffered audio below ~100ms
        if (m_RingCount + samples > kMaxBufferedSamples)
        {
            size_t target = kMaxBufferedSamples / 2;
            if (m_RingCount > target)
            {
                size_t drop = m_RingCount - target;
                m_RingRead = (m_RingRead + drop) % cap;
                m_RingCount -= drop;
            }
        }

        size_t writable = std::min(samples, cap - m_RingCount);
        size_t firstPart = std::min(writable, cap - m_RingWrite);
        std::memcpy(m_PcmRing.data() + m_RingWrite, m_DecodeScratch.data(), firstPart * sizeof(int16_t));
        if (writable > firstPart)
        {
            std::memcpy(m_PcmRing.data(), m_DecodeScratch.data() + firstPart, (writable - firstPart) * sizeof(int16_t));
        }
        m_RingWrite = (m_RingWrite + writable) % cap;
        m_RingCount += writable;
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
        opus_encoder_ctl(m_Encoder, OPUS_SET_DTX(1));

        m_MicCb = std::move(callback);
        m_MicAccum.clear();
        m_MicAccum.reserve(kMicFrameSamples * 2);

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

        while (self->m_MicAccum.size() >= kMicFrameSamples)
        {
            int encoded = opus_encode(
                self->m_Encoder,
                self->m_MicAccum.data(),
                kMicFrameSamples,
                self->m_MicOutBuf,
                sizeof(self->m_MicOutBuf));
            self->m_MicAccum.erase(self->m_MicAccum.begin(), self->m_MicAccum.begin() + kMicFrameSamples);
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
        return AAUDIO_CALLBACK_RESULT_CONTINUE;
    }
}
