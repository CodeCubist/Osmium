#include "AndroidVideoDecoder.h"
#include "MobileTransport.h"

#include <android/log.h>
#include <media/NdkMediaFormat.h>
#include <cstring>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "OsmiumVideoDec", __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, "OsmiumVideoDec", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "OsmiumVideoDec", __VA_ARGS__)

namespace osmium::mobile
{
    AndroidVideoDecoder::~AndroidVideoDecoder()
    {
        Release();
    }

    const char* AndroidVideoDecoder::CodecMimeFromId(uint8_t codecId)
    {
        switch (static_cast<proto::VideoCodecId>(codecId))
        {
            case proto::VideoCodecId::H264: return "video/avc";
            case proto::VideoCodecId::HEVC: return "video/hevc";
            case proto::VideoCodecId::AV1:  return "video/av01";
            default: return "video/avc";
        }
    }

    void AndroidVideoDecoder::SetSurface(ANativeWindow* window)
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        if (m_Window == window) return;

        DestroyCodecLocked();
        if (m_Window)
        {
            ANativeWindow_release(m_Window);
            m_Window = nullptr;
        }
        if (window)
        {
            ANativeWindow_acquire(window);
            m_Window = window;
        }
        m_WaitForKeyFrame = true;
    }

    void AndroidVideoDecoder::Release()
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        DestroyCodecLocked();
        if (m_Window)
        {
            ANativeWindow_release(m_Window);
            m_Window = nullptr;
        }
    }

    void AndroidVideoDecoder::DestroyCodecLocked()
    {
        if (m_Codec)
        {
            AMediaCodec_stop(m_Codec);
            AMediaCodec_delete(m_Codec);
            m_Codec = nullptr;
        }
        m_CodecId.store(0, std::memory_order_relaxed);
    }

    bool AndroidVideoDecoder::EnsureConfiguredLocked(uint8_t codecId, uint16_t width, uint16_t height)
    {
        if (!m_Window || width == 0 || height == 0) return false;

        if (m_Codec &&
            m_CodecId.load(std::memory_order_relaxed) == codecId &&
            m_Width.load(std::memory_order_relaxed) == width &&
            m_Height.load(std::memory_order_relaxed) == height)
        {
            return true;
        }

        DestroyCodecLocked();

        const char* mime = CodecMimeFromId(codecId);
        m_Codec = AMediaCodec_createDecoderByType(mime);
        if (!m_Codec)
        {
            LOGE("Failed to create AMediaCodec for %s", mime);
            return false;
        }

        auto tryConfigure = [&](bool lowLatency) -> bool {
            AMediaFormat* fmt = AMediaFormat_new();
            AMediaFormat_setString(fmt, AMEDIAFORMAT_KEY_MIME, mime);
            AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_WIDTH, width);
            AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_HEIGHT, height);
            AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_MAX_WIDTH, 3840);
            AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_MAX_HEIGHT, 2160);
            if (lowLatency)
            {
                AMediaFormat_setInt32(fmt, "low-latency", 1);
                AMediaFormat_setInt32(fmt, "priority", 0);
            }
            media_status_t st = AMediaCodec_configure(m_Codec, fmt, m_Window, nullptr, 0);
            AMediaFormat_delete(fmt);
            return st == AMEDIA_OK;
        };

        bool configured = tryConfigure(true);
        if (!configured)
        {
            LOGW("Low-latency AMediaCodec configure failed for %s; falling back to standard", mime);
            AMediaCodec_delete(m_Codec);
            m_Codec = AMediaCodec_createDecoderByType(mime);
            if (!m_Codec) return false;
            configured = tryConfigure(false);
        }

        if (!configured)
        {
            LOGE("AMediaCodec_configure failed for %s (%ux%u)", mime, width, height);
            AMediaCodec_delete(m_Codec);
            m_Codec = nullptr;
            return false;
        }

        if (AMediaCodec_start(m_Codec) != AMEDIA_OK)
        {
            LOGE("AMediaCodec_start failed for %s", mime);
            AMediaCodec_delete(m_Codec);
            m_Codec = nullptr;
            return false;
        }

        m_CodecId.store(codecId, std::memory_order_relaxed);
        m_Width.store(width, std::memory_order_relaxed);
        m_Height.store(height, std::memory_order_relaxed);
        m_WaitForKeyFrame = true;
        LOGI("AMediaCodec started: %s %ux%u", mime, width, height);
        return true;
    }

    uint32_t AndroidVideoDecoder::DecodeAndRender(const AssembledVideoFrame& frame, uint32_t& outDecodeTimeUs)
    {
        outDecodeTimeUs = 0;
        if (frame.payload.empty()) return 0;

        std::lock_guard<std::mutex> lock(m_Mutex);
        if (!EnsureConfiguredLocked(frame.codecId, frame.width, frame.height))
        {
            return 0;
        }

        if (m_WaitForKeyFrame)
        {
            if (!frame.isKeyFrame) return 0;
            m_WaitForKeyFrame = false;
        }

        uint64_t t0 = MobileTransport::NowMicros();

        ssize_t inIdx = AMediaCodec_dequeueInputBuffer(m_Codec, 8000);
        if (inIdx < 0)
        {
            return 0;
        }

        size_t inCap = 0;
        uint8_t* inBuf = AMediaCodec_getInputBuffer(m_Codec, static_cast<size_t>(inIdx), &inCap);
        if (!inBuf || frame.payload.size() > inCap)
        {
            AMediaCodec_queueInputBuffer(m_Codec, static_cast<size_t>(inIdx), 0, 0, 0, 0);
            return 0;
        }

        std::memcpy(inBuf, frame.payload.data(), frame.payload.size());
        uint32_t flags = frame.isKeyFrame ? 1u : 0u; // BUFFER_FLAG_KEY_FRAME
        int64_t ptsUs = static_cast<int64_t>(frame.captureTimeUs > 0 ? frame.captureTimeUs : t0);
        AMediaCodec_queueInputBuffer(
            m_Codec,
            static_cast<size_t>(inIdx),
            0,
            frame.payload.size(),
            ptsUs,
            flags);

        uint32_t rendered = 0;
        AMediaCodecBufferInfo info{};
        for (int spin = 0; spin < 4; ++spin)
        {
            ssize_t outIdx = AMediaCodec_dequeueOutputBuffer(m_Codec, &info, spin == 0 ? 4000 : 0);
            if (outIdx >= 0)
            {
                if (AMediaCodec_releaseOutputBuffer(m_Codec, static_cast<size_t>(outIdx), true) == AMEDIA_OK)
                {
                    ++rendered;
                }
            }
            else if (outIdx == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
            {
                // The output format reports the aligned buffer size (1920x1080
                // HEVC comes back as 1920x1088 on Qualcomm). m_Width/m_Height
                // key the configuration, so overwriting them made every next
                // frame look like a resolution change and rebuilt the codec.
                AMediaFormat* outFmt = AMediaCodec_getOutputFormat(m_Codec);
                if (outFmt)
                {
                    int32_t w = 0, h = 0;
                    AMediaFormat_getInt32(outFmt, AMEDIAFORMAT_KEY_WIDTH, &w);
                    AMediaFormat_getInt32(outFmt, AMEDIAFORMAT_KEY_HEIGHT, &h);
                    LOGI("Output format: %dx%d (stream %ux%u)",
                         w, h,
                         m_Width.load(std::memory_order_relaxed),
                         m_Height.load(std::memory_order_relaxed));
                    AMediaFormat_delete(outFmt);
                }
            }
            else
            {
                break;
            }
        }

        if (rendered)
        {
            outDecodeTimeUs = static_cast<uint32_t>(MobileTransport::NowMicros() - t0);
        }
        return rendered;
    }
}
