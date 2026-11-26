#include "AndroidVideoDecoder.h"
#include "MobileTransport.h"

#include <android/log.h>
#include <media/NdkMediaFormat.h>
#include <algorithm>
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
        if (m_Window == window && !m_SurfaceStale.load(std::memory_order_acquire))
        {
            return;
        }

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
        m_HasSurface.store(m_Window != nullptr, std::memory_order_release);
        m_SurfaceStale.store(false, std::memory_order_release);
        m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
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
        m_HasSurface.store(false, std::memory_order_release);
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
        m_ConfiguredMaxInput = 0;
        m_ConsecutiveNoOutput = 0;
        m_FlushAttempts = 0;
        m_InFlightFrames.store(0, std::memory_order_relaxed);
    }

    void AndroidVideoDecoder::RecoverCodec(bool hardRebuild)
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
        if (!m_Codec) return;

        if (!hardRebuild && m_FlushAttempts < 2)
        {
            media_status_t st = AMediaCodec_flush(m_Codec);
            if (st == AMEDIA_OK)
            {
                m_FlushAttempts++;
                m_ConsecutiveNoOutput = 0;
                m_InFlightFrames.store(0, std::memory_order_relaxed);
                LOGW("AMediaCodec flushed for stall recovery (attempt=%u)", m_FlushAttempts);
                return;
            }
            LOGW("AMediaCodec_flush failed (%d); forcing full codec rebuild", static_cast<int>(st));
        }

        LOGW("Destroying AMediaCodec for clean rebuild (hard=%d, flushes=%u)",
             hardRebuild ? 1 : 0, m_FlushAttempts);
        if (!hardRebuild && m_FlushAttempts >= 2)
        {
            // If low-latency mode wedged repeatedly on this SoC, fall back to standard mode.
            m_ForceStandardMode = true;
        }
        DestroyCodecLocked();
    }

    bool AndroidVideoDecoder::EnsureConfiguredLocked(
        uint8_t codecId,
        uint16_t width,
        uint16_t height,
        size_t minInputBytes)
    {
        if (!m_Window || width == 0 || height == 0) return false;

        if (m_Codec &&
            m_CodecId.load(std::memory_order_relaxed) == codecId &&
            m_Width.load(std::memory_order_relaxed) == width &&
            m_Height.load(std::memory_order_relaxed) == height &&
            (m_ConfiguredMaxInput == 0 || minInputBytes <= m_ConfiguredMaxInput))
        {
            return true;
        }

        DestroyCodecLocked();

        const char* mime = CodecMimeFromId(codecId);
        const int32_t maxInputSize = static_cast<int32_t>(std::clamp<size_t>(
            std::max<size_t>({
                static_cast<size_t>(4 * 1024 * 1024),
                static_cast<size_t>(width) * static_cast<size_t>(height) * 2,
                minInputBytes * 2
            }),
            static_cast<size_t>(2 * 1024 * 1024),
            static_cast<size_t>(16 * 1024 * 1024)));

        auto tryConfigure = [&](int profile) -> bool
        {
            m_Codec = AMediaCodec_createDecoderByType(mime);
            if (!m_Codec)
            {
                LOGE("Failed to create AMediaCodec for %s", mime);
                return false;
            }

            AMediaFormat* fmt = AMediaFormat_new();
            AMediaFormat_setString(fmt, AMEDIAFORMAT_KEY_MIME, mime);
            AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_WIDTH, width);
            AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_HEIGHT, height);
            AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_MAX_WIDTH, std::max<int32_t>(3840, width));
            AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_MAX_HEIGHT, std::max<int32_t>(2160, height));
            if (profile <= 1)
            {
                AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_MAX_INPUT_SIZE, maxInputSize);
            }
            if (profile == 0)
            {
                AMediaFormat_setInt32(fmt, "low-latency", 1);
                AMediaFormat_setInt32(fmt, "priority", 0);
                // Keep mobile video hardware decoder clocks at high throughput (60-120fps)
                // instead of throttling down to 30fps power-saving clocks.
                AMediaFormat_setInt32(fmt, "operating-rate", 240);
                AMediaFormat_setInt32(fmt, "vendor.qti-ext-dec-low-latency.enable", 1);
                AMediaFormat_setInt32(fmt, "vendor.low-latency.enable", 1);
                AMediaFormat_setInt32(fmt, "vendor.rtc-ext-dec-low-latency.enable", 1);
            }

            media_status_t st = AMediaCodec_configure(m_Codec, fmt, m_Window, nullptr, 0);
            AMediaFormat_delete(fmt);
            if (st != AMEDIA_OK)
            {
                AMediaCodec_delete(m_Codec);
                m_Codec = nullptr;
                return false;
            }

            st = AMediaCodec_start(m_Codec);
            if (st != AMEDIA_OK)
            {
                LOGW("AMediaCodec_start failed for %s (profile=%d, st=%d)", mime, profile, static_cast<int>(st));
                AMediaCodec_delete(m_Codec);
                m_Codec = nullptr;
                return false;
            }
            return true;
        };

        bool configured = false;
        int usedProfile = -1;
        const int startProfile = m_ForceStandardMode ? 1 : 0;
        for (int profile = startProfile; profile <= 2; ++profile)
        {
            if (tryConfigure(profile))
            {
                configured = true;
                usedProfile = profile;
                break;
            }
            LOGW("AMediaCodec configure/start failed for %s (profile=%d); trying fallback", mime, profile);
        }

        if (!configured)
        {
            LOGE("AMediaCodec failed all configure profiles for %s (%ux%u)", mime, width, height);
            m_SurfaceStale.store(true, std::memory_order_release);
            return false;
        }

        m_CodecId.store(codecId, std::memory_order_relaxed);
        m_Width.store(width, std::memory_order_relaxed);
        m_Height.store(height, std::memory_order_relaxed);
        m_ConfiguredMaxInput = (usedProfile <= 1) ? static_cast<size_t>(maxInputSize) : 0;
        m_ConsecutiveNoOutput = 0;
        m_FlushAttempts = 0;
        m_InFlightFrames.store(0, std::memory_order_relaxed);
        m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
        LOGI("AMediaCodec started: %s %ux%u (profile=%d, maxIn=%d)", mime, width, height, usedProfile, maxInputSize);
        return true;
    }

    uint32_t AndroidVideoDecoder::DrainOutputsLocked(int64_t firstTimeoutUs, bool& outFatalError)
    {
        outFatalError = false;
        if (!m_Codec) return 0;

        uint32_t rendered = 0;
        AMediaCodecBufferInfo info{};
        for (int spin = 0; spin < 16; ++spin)
        {
            ssize_t outIdx = AMediaCodec_dequeueOutputBuffer(m_Codec, &info, spin == 0 ? firstTimeoutUs : 0);
            if (outIdx >= 0)
            {
                media_status_t relSt = AMediaCodec_releaseOutputBuffer(m_Codec, static_cast<size_t>(outIdx), true);
                int32_t curInFlight = m_InFlightFrames.load(std::memory_order_relaxed);
                if (curInFlight > 0) m_InFlightFrames.store(curInFlight - 1, std::memory_order_relaxed);
                if (relSt == AMEDIA_OK)
                {
                    ++rendered;
                }
                else
                {
                    LOGW("AMediaCodec_releaseOutputBuffer failed (%d); marking surface/codec for recovery",
                         static_cast<int>(relSt));
                    m_SurfaceStale.store(true, std::memory_order_release);
                    outFatalError = true;
                    break;
                }
            }
            else if (outIdx == AMEDIACODEC_INFO_TRY_AGAIN_LATER)
            {
                break;
            }
            else if (outIdx == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED)
            {
                // The output format reports the aligned buffer size (1920x1080
                // HEVC comes back as 1920x1088 on Qualcomm). Never overwrite
                // m_Width/m_Height or every next frame looks like a resolution change.
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
            else if (outIdx == AMEDIACODEC_INFO_OUTPUT_BUFFERS_CHANGED)
            {
                continue;
            }
            else
            {
                LOGW("AMediaCodec_dequeueOutputBuffer error (%zd); rebuilding codec", outIdx);
                outFatalError = true;
                break;
            }
        }
        return rendered;
    }

    uint32_t AndroidVideoDecoder::DrainPendingOutput(uint32_t& outDecodeTimeUs)
    {
        outDecodeTimeUs = 0;
        std::lock_guard<std::mutex> lock(m_Mutex);
        if (!m_Codec || m_InFlightFrames.load(std::memory_order_relaxed) <= 0) return 0;

        uint64_t t0 = MobileTransport::NowMicros();
        bool fatalError = false;
        uint32_t rendered = DrainOutputsLocked(0, fatalError);
        if (fatalError)
        {
            DestroyCodecLocked();
            m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
            return rendered;
        }
        if (rendered > 0)
        {
            m_ConsecutiveNoOutput = 0;
            m_FlushAttempts = 0;
            outDecodeTimeUs = static_cast<uint32_t>(MobileTransport::NowMicros() - t0);
        }
        return rendered;
    }

    uint32_t AndroidVideoDecoder::DecodeAndRender(
        const AssembledVideoFrame& frame,
        uint32_t& outDecodeTimeUs,
        bool hasBacklog)
    {
        outDecodeTimeUs = 0;
        if (frame.payload.empty()) return 0;

        std::lock_guard<std::mutex> lock(m_Mutex);
        if (!EnsureConfiguredLocked(frame.codecId, frame.width, frame.height, frame.payload.size()))
        {
            return 0;
        }

        if (m_WaitForKeyFrame.load(std::memory_order_relaxed))
        {
            if (!frame.isKeyFrame) return 0;
            m_WaitForKeyFrame.store(false, std::memory_order_relaxed);
        }

        uint64_t t0 = MobileTransport::NowMicros();
        bool fatalError = false;

        // Drain any ready output surfaces BEFORE dequeuing an input buffer so the
        // hardware codec never stalls on a full output ring and silently drops a P-frame.
        uint32_t rendered = DrainOutputsLocked(0, fatalError);
        if (fatalError)
        {
            DestroyCodecLocked();
            m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
            return rendered;
        }

        ssize_t inIdx = AMediaCodec_dequeueInputBuffer(m_Codec, 8000);
        if (inIdx < 0 && inIdx != AMEDIACODEC_INFO_TRY_AGAIN_LATER)
        {
            LOGW("AMediaCodec_dequeueInputBuffer fatal error (%zd); rebuilding codec", inIdx);
            DestroyCodecLocked();
            m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
            return rendered;
        }
        if (inIdx < 0)
        {
            rendered += DrainOutputsLocked(2000, fatalError);
            if (fatalError)
            {
                DestroyCodecLocked();
                m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
                return rendered;
            }
            inIdx = AMediaCodec_dequeueInputBuffer(m_Codec, 4000);
            if (inIdx < 0)
            {
                if (inIdx != AMEDIACODEC_INFO_TRY_AGAIN_LATER)
                {
                    LOGW("AMediaCodec_dequeueInputBuffer retry error (%zd); rebuilding codec", inIdx);
                    DestroyCodecLocked();
                }
                else if (++m_ConsecutiveNoOutput >= 6)
                {
                    LOGW("AMediaCodec input ring wedged (%u consecutive); rebuilding codec", m_ConsecutiveNoOutput);
                    DestroyCodecLocked();
                }
                m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
                return rendered;
            }
        }

        size_t inCap = 0;
        uint8_t* inBuf = AMediaCodec_getInputBuffer(m_Codec, static_cast<size_t>(inIdx), &inCap);
        if (!inBuf || frame.payload.size() > inCap)
        {
            LOGW("AMediaCodec input buffer too small (%zu > %zu); rebuilding codec with larger max-input-size",
                 frame.payload.size(), inCap);
            AMediaCodec_queueInputBuffer(m_Codec, static_cast<size_t>(inIdx), 0, 0, 0, 0);
            DestroyCodecLocked();
            m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
            return rendered;
        }

        std::memcpy(inBuf, frame.payload.data(), frame.payload.size());
        uint32_t flags = frame.isKeyFrame ? 1u : 0u; // BUFFER_FLAG_KEY_FRAME
        int64_t ptsUs = static_cast<int64_t>(frame.captureTimeUs > 0 ? frame.captureTimeUs : t0);
        media_status_t qSt = AMediaCodec_queueInputBuffer(
            m_Codec,
            static_cast<size_t>(inIdx),
            0,
            frame.payload.size(),
            ptsUs,
            flags);
        if (qSt != AMEDIA_OK)
        {
            LOGW("AMediaCodec_queueInputBuffer failed (%d); rebuilding codec", static_cast<int>(qSt));
            DestroyCodecLocked();
            m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
            return rendered;
        }
        m_InFlightFrames.fetch_add(1, std::memory_order_relaxed);

        rendered += DrainOutputsLocked(hasBacklog ? 0 : 4000, fatalError);
        if (fatalError)
        {
            DestroyCodecLocked();
            m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
            return rendered;
        }

        if (rendered > 0)
        {
            m_ConsecutiveNoOutput = 0;
            m_FlushAttempts = 0;
            outDecodeTimeUs = static_cast<uint32_t>(MobileTransport::NowMicros() - t0);
        }
        else
        {
            m_ConsecutiveNoOutput++;
            if (m_ConsecutiveNoOutput >= 8)
            {
                if (m_FlushAttempts < 1 && AMediaCodec_flush(m_Codec) == AMEDIA_OK)
                {
                    m_FlushAttempts++;
                    m_ConsecutiveNoOutput = 0;
                    m_InFlightFrames.store(0, std::memory_order_relaxed);
                    m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
                    LOGW("AMediaCodec produced no output for 8 frames; flushed and requesting IDR");
                }
                else
                {
                    LOGW("AMediaCodec still produced no output after flush; rebuilding codec");
                    m_ForceStandardMode = true;
                    DestroyCodecLocked();
                    m_WaitForKeyFrame.store(true, std::memory_order_relaxed);
                }
            }
        }
        return rendered;
    }
}
