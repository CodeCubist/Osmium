#include "HostVideoEncoder.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <d3d11_4.h>
#include <dxgi.h>
#include <mutex>
#include <unordered_map>

extern "C" {
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
}

namespace osmium
{
    namespace
    {
        std::mutex g_WorkingEncoderCacheMutex;
        std::unordered_map<uint64_t, std::string> g_WorkingEncoderCache;

        uint64_t MakeEncoderCacheKey(int codecPref, int encoderPref, bool enableHdr, bool enableYuv444)
        {
            return (static_cast<uint64_t>(codecPref & 0xFF) << 16) |
                (static_cast<uint64_t>(encoderPref & 0xFF) << 8) |
                (enableHdr ? 2ULL : 0ULL) |
                (enableYuv444 ? 1ULL : 0ULL);
        }

        UINT DetectD3d11VendorId(ID3D11Device* device)
        {
            if (!device) return 0;
            IDXGIDevice* dxgiDev = nullptr;
            if (FAILED(device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDev))) || !dxgiDev)
            {
                return 0;
            }
            IDXGIAdapter* adapter = nullptr;
            UINT vendorId = 0;
            if (SUCCEEDED(dxgiDev->GetAdapter(&adapter)) && adapter)
            {
                DXGI_ADAPTER_DESC desc{};
                if (SUCCEEDED(adapter->GetDesc(&desc)))
                {
                    vendorId = desc.VendorId;
                }
                adapter->Release();
            }
            dxgiDev->Release();
            return vendorId;
        }

        AVBufferRef* CreateD3D11Nv12Frames(ID3D11Device* device, int width, int height)
        {
            ID3D11Multithread* multithread = nullptr;
            ID3D11DeviceContext* immediate = nullptr;
            device->GetImmediateContext(&immediate);
            const HRESULT threadingSupport = immediate->QueryInterface(IID_PPV_ARGS(&multithread));
            immediate->Release();
            if (FAILED(threadingSupport)) return nullptr;
            multithread->SetMultithreadProtected(TRUE);
            multithread->Release();

            AVBufferRef* deviceRef = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
            if (!deviceRef) return nullptr;
            auto* deviceCtx = reinterpret_cast<AVHWDeviceContext*>(deviceRef->data);
            auto* d3d = static_cast<AVD3D11VADeviceContext*>(deviceCtx->hwctx);
            device->AddRef();
            d3d->device = device;
            AVBufferRef* frames = nullptr;
            if (av_hwdevice_ctx_init(deviceRef) >= 0) frames = av_hwframe_ctx_alloc(deviceRef);
            av_buffer_unref(&deviceRef);
            if (!frames) return nullptr;

            auto* ctx = reinterpret_cast<AVHWFramesContext*>(frames->data);
            ctx->format = AV_PIX_FMT_D3D11;
            ctx->sw_format = AV_PIX_FMT_NV12;
            ctx->width = width;
            ctx->height = height;
            if (av_hwframe_ctx_init(frames) < 0) av_buffer_unref(&frames);
            return frames;
        }

        struct Bt2020Lut
        {
            std::array<int32_t, 1024> yr{}, yg{}, yb{};
            std::array<int32_t, 1024> ur{}, ug{}, ub{};
            std::array<int32_t, 1024> vr{}, vg{}, vb{};
        };

        const Bt2020Lut& GetBt2020Lut()
        {
            static Bt2020Lut lut;
            static std::once_flag once;
            std::call_once(once, []()
            {
                constexpr double scaleQ16 = 65536.0;
                constexpr double yScale = (876.0 / 1023.0) * scaleQ16;
                constexpr double cbScale = (896.0 / (1023.0 * 1.8814)) * scaleQ16;
                constexpr double crScale = (896.0 / (1023.0 * 1.4746)) * scaleQ16;
                for (int i = 0; i < 1024; ++i)
                {
                    double v = static_cast<double>(i);
                    lut.yr[i] = static_cast<int32_t>(std::llround(v * 0.2627 * yScale));
                    lut.yg[i] = static_cast<int32_t>(std::llround(v * 0.6780 * yScale));
                    lut.yb[i] = static_cast<int32_t>(std::llround(v * 0.0593 * yScale));

                    lut.ur[i] = static_cast<int32_t>(std::llround(v * -0.2627 * cbScale));
                    lut.ug[i] = static_cast<int32_t>(std::llround(v * -0.6780 * cbScale));
                    lut.ub[i] = static_cast<int32_t>(std::llround(v * 0.9407 * cbScale));

                    lut.vr[i] = static_cast<int32_t>(std::llround(v * 0.7373 * crScale));
                    lut.vg[i] = static_cast<int32_t>(std::llround(v * -0.6780 * crScale));
                    lut.vb[i] = static_cast<int32_t>(std::llround(v * -0.0593 * crScale));
                }
            });
            return lut;
        }

        // Scans an Annex-B bitstream and extracts H.264 (SPS/PPS) or HEVC (VPS/SPS/PPS)
        // parameter-set NAL units so every IDR keyframe sent to Android/PC decoders is
        // guaranteed to carry inline codec configuration headers.
        bool ExtractAnnexBParamSets(
            proto::VideoCodecId codecId,
            const uint8_t* data,
            size_t len,
            std::vector<uint8_t>& outParamSets)
        {
            if (!data || len < 5) return false;
            if (codecId != proto::VideoCodecId::H264 && codecId != proto::VideoCodecId::HEVC) return false;

            auto findStartCode = [&](size_t from, size_t& scLen) -> size_t
            {
                for (size_t i = from; i + 3 < len; ++i)
                {
                    if (data[i] == 0 && data[i + 1] == 0)
                    {
                        if (data[i + 2] == 1)
                        {
                            scLen = 3;
                            return i;
                        }
                        if (i + 3 < len && data[i + 2] == 0 && data[i + 3] == 1)
                        {
                            scLen = 4;
                            return i;
                        }
                    }
                }
                scLen = 0;
                return len;
            };

            std::vector<uint8_t> collected;
            bool hasSps = false;
            bool hasPps = false;
            size_t scLen = 0;
            size_t pos = findStartCode(0, scLen);
            while (pos < len)
            {
                const size_t nalStart = pos + scLen;
                size_t nextScLen = 0;
                const size_t nextPos = findStartCode(nalStart, nextScLen);
                if (nalStart < nextPos)
                {
                    bool isParam = false;
                    if (codecId == proto::VideoCodecId::H264)
                    {
                        const uint8_t nalType = data[nalStart] & 0x1FU;
                        if (nalType == 7)
                        {
                            isParam = true;
                            hasSps = true;
                        }
                        else if (nalType == 8)
                        {
                            isParam = true;
                            hasPps = true;
                        }
                    }
                    else if (codecId == proto::VideoCodecId::HEVC)
                    {
                        const uint8_t nalType = (data[nalStart] >> 1) & 0x3FU;
                        if (nalType == 32) { isParam = true; }
                        else if (nalType == 33)
                        {
                            isParam = true;
                            hasSps = true;
                        }
                        else if (nalType == 34)
                        {
                            isParam = true;
                            hasPps = true;
                        }
                    }
                    if (isParam)
                    {
                        collected.insert(collected.end(), data + pos, data + nextPos);
                    }
                }
                pos = nextPos;
                scLen = nextScLen;
            }

            if (hasSps && hasPps && !collected.empty())
            {
                outParamSets = std::move(collected);
                return true;
            }
            return false;
        }
    }

    HostVideoEncoder::~HostVideoEncoder()
    {
        Shutdown();
    }

    void HostVideoEncoder::ExcludeEncoderForSession(const std::string& ffmpegName)
    {
        if (ffmpegName.empty()) return;
        if (std::find(m_ExcludedEncoders.begin(), m_ExcludedEncoders.end(), ffmpegName) == m_ExcludedEncoders.end())
        {
            m_ExcludedEncoders.push_back(ffmpegName);
        }
    }

    void HostVideoEncoder::UpdateBitrate(int bitrateKbps)
    {
        if (!m_CodecCtx) return;
        const int clamped = std::clamp(bitrateKbps, 800, 500000);
        if (clamped == m_ActiveBitrateKbps) return;
        const int64_t bps = static_cast<int64_t>(clamped) * 1000LL;
        m_CodecCtx->bit_rate = bps;
        m_CodecCtx->rc_max_rate = bps;
        m_CodecCtx->rc_buffer_size = static_cast<int>(std::max<int64_t>(
            16000, (bps * 5) / (std::max(1, m_ActiveFps) * 4)));
        m_ActiveBitrateKbps = clamped;
    }

    void HostVideoEncoder::Shutdown()
    {
        m_ConvertedPixelRevision = 0;
        if (m_SwsCtx)
        {
            sws_freeContext(m_SwsCtx);
            m_SwsCtx = nullptr;
        }
        m_SwsSrcFmt = AV_PIX_FMT_NONE;
        if (m_Pkt)
        {
            av_packet_free(&m_Pkt);
        }
        if (m_YuvFrame)
        {
            av_frame_free(&m_YuvFrame);
        }
        av_frame_free(&m_HardwareFrame);
        if (m_CodecCtx)
        {
            avcodec_free_context(&m_CodecCtx);
        }
        m_HdrActive = false;
        m_PqHdrActive = false;
        m_Yuv444Active = false;
        m_CachedParamSets.clear();
    }

    bool HostVideoEncoder::Initialize(
        int srcWidth,
        int srcHeight,
        int outWidth,
        int outHeight,
        int fps,
        int bitrateKbps,
        int codecPreference,
        int encoderPreference,
        bool enableHdr,
        bool enableYuv444,
        ID3D11Device* captureDevice)
    {
        Shutdown();

        m_SrcWidth = srcWidth;
        m_SrcHeight = srcHeight;
        m_OutWidth = (outWidth > 0 ? outWidth : srcWidth) & ~1;
        m_OutHeight = (outHeight > 0 ? outHeight : srcHeight) & ~1;
        m_OutWidth = std::max(256, m_OutWidth);
        m_OutHeight = std::max(144, m_OutHeight);
        m_Pts = 0;
        m_HdrActive = false;
        m_Yuv444Active = false;
        m_ForceIdr.store(true);

        const std::vector<EncoderCandidate> allCandidates = {
            {"av1_nvenc", proto::VideoCodecId::AV1, "AV1", true},
            {"av1_amf", proto::VideoCodecId::AV1, "AV1", true},
            {"av1_qsv", proto::VideoCodecId::AV1, "AV1", true},
            {"av1_mf", proto::VideoCodecId::AV1, "AV1", true},
            {"hevc_nvenc", proto::VideoCodecId::HEVC, "HEVC", true},
            {"hevc_amf", proto::VideoCodecId::HEVC, "HEVC", true},
            {"hevc_qsv", proto::VideoCodecId::HEVC, "HEVC", true},
            {"hevc_mf", proto::VideoCodecId::HEVC, "HEVC", true},
            {"h264_nvenc", proto::VideoCodecId::H264, "H.264", true},
            {"h264_amf", proto::VideoCodecId::H264, "H.264", true},
            {"h264_qsv", proto::VideoCodecId::H264, "H.264", true},
            {"h264_mf", proto::VideoCodecId::H264, "H.264", true},
            {"libopenh264", proto::VideoCodecId::H264, "H.264", false},
            {"libsvtav1", proto::VideoCodecId::AV1, "AV1", false},
        };

        auto matchesCodec = [codecPreference](proto::VideoCodecId cid)
        {
            if (codecPreference == 1) return cid == proto::VideoCodecId::H264;
            if (codecPreference == 2) return cid == proto::VideoCodecId::HEVC;
            if (codecPreference == 3) return cid == proto::VideoCodecId::AV1;
            return true;
        };

        std::vector<EncoderCandidate> ordered;
        for (const auto& c : allCandidates)
        {
            if (!matchesCodec(c.codecId)) continue;
            if (encoderPreference == 1 && !c.isHardware) continue;
            if (encoderPreference == 2 && c.isHardware) continue;
            if (codecPreference == 0 && enableHdr && c.codecId == proto::VideoCodecId::H264) continue;
            if (codecPreference == 0 && !enableHdr && c.codecId == proto::VideoCodecId::AV1) continue;
            ordered.push_back(c);
        }
        if (codecPreference == 0)
        {
            for (const auto& c : allCandidates)
            {
                if (encoderPreference == 1 && !c.isHardware) continue;
                if (encoderPreference == 2 && c.isHardware) continue;
                bool already = (enableHdr && c.codecId != proto::VideoCodecId::H264) || (!enableHdr && c.codecId !=
                    proto::VideoCodecId::AV1);
                if (!already) ordered.push_back(c);
            }
        }

        auto isExcluded = [this](const char* name)
        {
            return std::find(m_ExcludedEncoders.begin(), m_ExcludedEncoders.end(), name) != m_ExcludedEncoders.end();
        };

        // Prioritize the GPU vendor matching the active capture adapter so AMD/Intel hosts
        // don't burn 150-400ms probing unavailable NVENC/AMF/QSV DLLs on every cold connect.
        const UINT gpuVendorId = DetectD3d11VendorId(captureDevice);
        const char* preferredVendorTag = nullptr;
        if (gpuVendorId == 0x10DE) preferredVendorTag = "_nvenc";
        else if (gpuVendorId == 0x1002) preferredVendorTag = "_amf";
        else if (gpuVendorId == 0x8086) preferredVendorTag = "_qsv";

        if (preferredVendorTag != nullptr)
        {
            auto vendorPriority = [preferredVendorTag](const EncoderCandidate& c) -> int
            {
                if (!c.isHardware) return 2;
                return (std::strstr(c.ffmpegName, preferredVendorTag) != nullptr) ? 0 : 1;
            };
            // Preserve codec priority groups while putting the host's actual GPU vendor first within each codec.
            size_t groupStart = 0;
            while (groupStart < ordered.size())
            {
                size_t groupEnd = groupStart + 1;
                while (groupEnd < ordered.size() &&
                    ordered[groupEnd].codecId == ordered[groupStart].codecId &&
                    ordered[groupEnd].isHardware == ordered[groupStart].isHardware)
                {
                    ++groupEnd;
                }
                std::stable_sort(ordered.begin() + static_cast<ptrdiff_t>(groupStart),
                                 ordered.begin() + static_cast<ptrdiff_t>(groupEnd),
                                 [&](const EncoderCandidate& a, const EncoderCandidate& b)
                                 {
                                     return vendorPriority(a) < vendorPriority(b);
                                 });
                groupStart = groupEnd;
            }
        }

        // Put the last-known working encoder for this profile first for instant reconnect startup.
        const uint64_t encCacheKey = MakeEncoderCacheKey(codecPreference, encoderPreference, enableHdr, enableYuv444);
        std::string cachedEncName;
        {
            std::lock_guard<std::mutex> cl(g_WorkingEncoderCacheMutex);
            auto it = g_WorkingEncoderCache.find(encCacheKey);
            if (it != g_WorkingEncoderCache.end()) cachedEncName = it->second;
        }
        if (!cachedEncName.empty() && !isExcluded(cachedEncName.c_str()))
        {
            auto it = std::find_if(ordered.begin(), ordered.end(), [&](const EncoderCandidate& c)
            {
                return cachedEncName == c.ffmpegName;
            });
            if (it != ordered.end() && it != ordered.begin())
            {
                EncoderCandidate fastHit = *it;
                ordered.erase(it);
                ordered.insert(ordered.begin(), fastHit);
            }
        }

        std::vector<EncoderCandidate> candidatePasses[2];
        for (const auto& cand : ordered)
        {
            candidatePasses[isExcluded(cand.ffmpegName) ? 1 : 0].push_back(cand);
        }

        for (int pass = 0; pass < 2; ++pass)
        {
            for (const auto& cand : candidatePasses[pass])
            {
                if (TryOpenCandidate(cand, fps, bitrateKbps, enableHdr, enableYuv444, captureDevice))
                {
                    {
                        std::lock_guard<std::mutex> cl(g_WorkingEncoderCacheMutex);
                        g_WorkingEncoderCache[encCacheKey] = cand.ffmpegName;
                    }
                    m_YuvFrame = av_frame_alloc();
                    if (m_CodecCtx->hw_frames_ctx) m_HardwareFrame = av_frame_alloc();
                    if (!m_YuvFrame || (m_CodecCtx->hw_frames_ctx && !m_HardwareFrame))
                    {
                        Shutdown();
                        return false;
                    }
                    m_YuvFrame->format = m_HardwareFrame ? AV_PIX_FMT_NV12 : m_CodecCtx->pix_fmt;
                    m_YuvFrame->width = m_OutWidth;
                    m_YuvFrame->height = m_OutHeight;
                    m_YuvFrame->color_primaries = m_CodecCtx->color_primaries;
                    m_YuvFrame->color_trc = m_CodecCtx->color_trc;
                    m_YuvFrame->colorspace = m_CodecCtx->colorspace;
                    m_YuvFrame->color_range = m_CodecCtx->color_range;
                    if (av_frame_get_buffer(m_YuvFrame, 32) < 0)
                    {
                        Shutdown();
                        return false;
                    }

                    if (m_PqHdrActive)
                    {
                        m_HdrColumns.resize(m_OutWidth);
                        m_HdrRows.resize(m_OutHeight);
                        for (int x = 0; x < m_OutWidth; ++x)
                            m_HdrColumns[x] = static_cast<int>(static_cast<int64_t>(x) * m_SrcWidth / m_OutWidth);
                        for (int y = 0; y < m_OutHeight; ++y)
                            m_HdrRows[y] = static_cast<int>(static_cast<int64_t>(y) * m_SrcHeight / m_OutHeight);
                        if (AVMasteringDisplayMetadata* mdm =
                            av_mastering_display_metadata_create_side_data(m_YuvFrame))
                        {
                            mdm->display_primaries[0][0] = av_make_q(708, 1000);
                            mdm->display_primaries[0][1] = av_make_q(292, 1000);
                            mdm->display_primaries[1][0] = av_make_q(170, 1000);
                            mdm->display_primaries[1][1] = av_make_q(797, 1000);
                            mdm->display_primaries[2][0] = av_make_q(131, 1000);
                            mdm->display_primaries[2][1] = av_make_q(46, 1000);
                            mdm->white_point[0] = av_make_q(3127, 10000);
                            mdm->white_point[1] = av_make_q(3290, 10000);
                            mdm->min_luminance = av_make_q(50, 10000);
                            mdm->max_luminance = av_make_q(1000, 1);
                            mdm->has_primaries = 1;
                            mdm->has_luminance = 1;
                        }
                        if (AVContentLightMetadata* cll = av_content_light_metadata_create_side_data(m_YuvFrame))
                        {
                            cll->MaxCLL = 1000;
                            cll->MaxFALL = 400;
                        }
                    }

                    m_Pkt = av_packet_alloc();
                    if (!m_Pkt || (!m_PqHdrActive && !EnsureSwsContext(AV_PIX_FMT_BGRA)))
                    {
                        Shutdown();
                        return false;
                    }

                    OSM_LOG_INFO(
                        "HostVideoEncoder initialized: %s (%s, %dx%d @ %dfps, %d kbps, hdr=%d, pq=%d, yuv444=%d, D3D11 input=%d)",
                        m_EncoderName.c_str(), m_CodecName.c_str(), m_OutWidth, m_OutHeight, fps, bitrateKbps,
                        m_HdrActive ? 1 : 0, m_PqHdrActive ? 1 : 0, m_Yuv444Active ? 1 : 0, m_HardwareFrame ? 1 : 0);
                    return true;
                }
            }
        }

        OSM_LOG_ERROR("No suitable video encoder could be initialized");
        return false;
    }

    bool HostVideoEncoder::TryOpenCandidate(const EncoderCandidate& cand, int fps, int bitrateKbps, bool wantHdr,
                                            bool wantYuv444, ID3D11Device* captureDevice)
    {
        const AVCodec* codec = avcodec_find_encoder_by_name(cand.ffmpegName);
        if (!codec) return false;

        AVCodecContext* ctx = avcodec_alloc_context3(codec);
        if (!ctx) return false;

        ctx->width = m_OutWidth;
        ctx->height = m_OutHeight;
        ctx->time_base = AVRational{1, std::max(1, fps)};
        ctx->framerate = AVRational{std::max(1, fps), 1};
        ctx->bit_rate = static_cast<int64_t>(std::max(500, bitrateKbps)) * 1000LL;
        ctx->rc_max_rate = ctx->bit_rate;
        // Tight ~1.25-frame VBV smooths scene-change/keyframe spikes so 2.4GHz Wi-Fi AP
        // queues don't overflow on sudden motion or IDR keyframes.
        ctx->rc_buffer_size = static_cast<int>(std::max<
            int64_t>(16000, (ctx->bit_rate * 5) / (ctx->framerate.num * 4)));
        // Long periodic GOP (10s instead of 2s) eliminates frequent unsolicited keyframe
        // spikes on Wi-Fi/P2P/Relay while on-demand VideoIdrRequest handles loss recovery.
        ctx->gop_size = std::max(1, fps * 10);
        ctx->max_b_frames = 0;
        ctx->flags |= AV_CODEC_FLAG_LOW_DELAY;

        AVPixelFormat chosenFmt = AV_PIX_FMT_NV12;
        const enum AVPixelFormat* supportedFmts = nullptr;
        int numFmts = 0;
        bool supportsP010 = false, supportsYuv444 = false, supportsYuv444p10 = false;
        bool hasNv12 = false, hasYuv420p = false, supportsD3D11 = false;
        if (avcodec_get_supported_config(ctx, codec, AV_CODEC_CONFIG_PIX_FORMAT, 0,
                                         reinterpret_cast<const void**>(&supportedFmts), &numFmts) >= 0 &&
            supportedFmts && numFmts > 0)
        {
            for (int i = 0; i < numFmts; ++i)
            {
                if (supportedFmts[i] == AV_PIX_FMT_NV12) hasNv12 = true;
                if (supportedFmts[i] == AV_PIX_FMT_D3D11) supportsD3D11 = true;
                if (supportedFmts[i] == AV_PIX_FMT_YUV420P) hasYuv420p = true;
                if (supportedFmts[i] == AV_PIX_FMT_P010LE) supportsP010 = true;
                if (supportedFmts[i] == AV_PIX_FMT_YUV444P) supportsYuv444 = true;
                if (supportedFmts[i] == AV_PIX_FMT_YUV444P10LE) supportsYuv444p10 = true;
            }
            if (wantHdr && wantYuv444 && supportsYuv444p10) chosenFmt = AV_PIX_FMT_YUV444P10LE;
            else if (wantYuv444 && supportsYuv444) chosenFmt = AV_PIX_FMT_YUV444P;
            else if (wantHdr && supportsP010) chosenFmt = AV_PIX_FMT_P010LE;
            else if (hasNv12) chosenFmt = AV_PIX_FMT_NV12;
            else if (hasYuv420p) chosenFmt = AV_PIX_FMT_YUV420P;
            else chosenFmt = supportedFmts[0];
        }
        ctx->pix_fmt = chosenFmt;

        bool hdrUsed = wantHdr && (chosenFmt == AV_PIX_FMT_P010LE || chosenFmt == AV_PIX_FMT_YUV444P10LE);
        bool yuv444Used = (chosenFmt == AV_PIX_FMT_YUV444P || chosenFmt == AV_PIX_FMT_YUV444P10LE);
        if (hdrUsed)
        {
            ctx->color_primaries = AVCOL_PRI_BT2020;
            ctx->color_trc = AVCOL_TRC_SMPTE2084;
            ctx->colorspace = AVCOL_SPC_BT2020_NCL;
            ctx->color_range = AVCOL_RANGE_MPEG;
        }
        else
        {
            ctx->color_primaries = AVCOL_PRI_BT709;
            ctx->color_trc = AVCOL_TRC_BT709;
            ctx->colorspace = AVCOL_SPC_BT709;
            ctx->color_range = AVCOL_RANGE_MPEG;
        }

        std::string name(cand.ffmpegName);
        if (name.find("nvenc") != std::string::npos)
        {
            av_opt_set(ctx->priv_data, "preset", "p1", 0);
            av_opt_set(ctx->priv_data, "tune", "ull", 0);
            av_opt_set(ctx->priv_data, "rc", "cbr", 0);
            av_opt_set(ctx->priv_data, "zerolatency", "1", 0);
            av_opt_set(ctx->priv_data, "delay", "0", 0);
            av_opt_set(ctx->priv_data, "forced-idr", "1", 0);
            av_opt_set(ctx->priv_data, "strict_gop", "1", 0);
            av_opt_set(ctx->priv_data, "b_ref_mode", "disabled", 0);
            if (yuv444Used && name == "hevc_nvenc")
            {
                av_opt_set(ctx->priv_data, "profile", "rext", 0);
            }
            else if (hdrUsed && name == "hevc_nvenc")
            {
                av_opt_set(ctx->priv_data, "profile", "main10", 0);
            }
        }
        else if (name.find("amf") != std::string::npos)
        {
            av_opt_set(ctx->priv_data, "usage", "ultralowlatency", 0);
            av_opt_set(ctx->priv_data, "quality", "speed", 0);
            av_opt_set(ctx->priv_data, "rc", "cbr", 0);
            av_opt_set(ctx->priv_data, "header_insertion_mode", "idr", 0);
        }
        else if (name.find("qsv") != std::string::npos)
        {
            av_opt_set(ctx->priv_data, "preset", "veryfast", 0);
            av_opt_set(ctx->priv_data, "async_depth", "1", 0);
            av_opt_set_int(ctx->priv_data, "forced_idr", 1, 0);
        }
        else if (name.find("_mf") != std::string::npos)
        {
            av_opt_set(ctx->priv_data, "rate_control", "cbr", 0);
            av_opt_set_int(ctx->priv_data, "hw_encoding", 1, 0);
            av_opt_set_int(ctx->priv_data, "low_latency", 1, 0);
            av_opt_set(ctx->priv_data, "scenario", "live_streaming", 0);
        }
        else if (name == "libopenh264")
        {
            av_opt_set(ctx->priv_data, "slice_mode", "auto", 0);
            av_opt_set(ctx->priv_data, "rc_mode", "bitrate", 0);
            av_opt_set_int(ctx->priv_data, "allow_skip_frames", 0, 0);
        }
        else if (name == "libsvtav1")
        {
            av_opt_set_int(ctx->priv_data, "preset", 11, 0);
        }

        if (captureDevice && supportsD3D11 && chosenFmt == AV_PIX_FMT_NV12 && !wantHdr && !wantYuv444 &&
            (name.find("amf") != std::string::npos || name.find("nvenc") != std::string::npos ||
                name.find("qsv") != std::string::npos))
        {
            ctx->hw_frames_ctx = CreateD3D11Nv12Frames(captureDevice, m_OutWidth, m_OutHeight);
            if (ctx->hw_frames_ctx) ctx->pix_fmt = AV_PIX_FMT_D3D11;
        }
        if (avcodec_open2(ctx, codec, nullptr) < 0)
        {
            const bool retryWithCpuFrames = captureDevice && ctx->pix_fmt == AV_PIX_FMT_D3D11;
            avcodec_free_context(&ctx);
            if (retryWithCpuFrames) return TryOpenCandidate(cand, fps, bitrateKbps, wantHdr, wantYuv444, nullptr);
            return false;
        }

        m_CodecCtx = ctx;
        m_ActiveFps = std::max(1, fps);
        m_ActiveBitrateKbps = std::max(500, bitrateKbps);
        m_EncoderName = cand.ffmpegName;
        m_CodecName = cand.codecDisplay;
        m_CodecId = cand.codecId;
        m_IsHardware = cand.isHardware;
        m_HdrActive = hdrUsed;
        m_PqHdrActive = hdrUsed;
        m_Yuv444Active = yuv444Used;
        m_CachedParamSets.clear();
        if (ctx->extradata && ctx->extradata_size > 0)
        {
            ExtractAnnexBParamSets(m_CodecId, ctx->extradata, static_cast<size_t>(ctx->extradata_size),
                                   m_CachedParamSets);
        }
        return true;
    }

    bool HostVideoEncoder::EnsureSwsContext(AVPixelFormat srcFmt)
    {
        if (!m_CodecCtx) return false;
        if (m_SwsCtx && m_SwsSrcFmt == srcFmt) return true;
        if (m_SwsCtx)
        {
            sws_freeContext(m_SwsCtx);
            m_SwsCtx = nullptr;
        }
        const int swsFlags = (m_SrcWidth == m_OutWidth && m_SrcHeight == m_OutHeight) ? SWS_POINT : SWS_BILINEAR;
        m_SwsCtx = sws_getContext(
            m_SrcWidth, m_SrcHeight, srcFmt,
            m_OutWidth, m_OutHeight, static_cast<AVPixelFormat>(m_YuvFrame->format),
            swsFlags, nullptr, nullptr, nullptr);
        if (!m_SwsCtx)
        {
            m_SwsSrcFmt = AV_PIX_FMT_NONE;
            return false;
        }
        const int* csCoeff = sws_getCoefficients(m_PqHdrActive ? SWS_CS_BT2020 : SWS_CS_ITU709);
        sws_setColorspaceDetails(m_SwsCtx, csCoeff, 1, csCoeff, 0, 0, 1 << 16, 1 << 16);
        m_SwsSrcFmt = srcFmt;
        return true;
    }

    bool HostVideoEncoder::FillYuvFromRgb10Pq(const uint8_t* rgb10, int srcStride)
    {
        if (!m_CodecCtx || !m_YuvFrame || !rgb10) return false;

        const auto& lut = GetBt2020Lut();

        auto sampleRgb10 = [rgb10, srcStride](int sx, int sy, int& r, int& g, int& b)
        {
            const uint32_t px = *reinterpret_cast<const uint32_t*>(rgb10 + static_cast<size_t>(sy) * srcStride +
                static_cast<size_t>(sx) * 4);
            r = static_cast<int>(px & 0x3FFU);
            g = static_cast<int>((px >> 10) & 0x3FFU);
            b = static_cast<int>((px >> 20) & 0x3FFU);
        };

        if (m_CodecCtx->pix_fmt == AV_PIX_FMT_P010LE)
        {
            for (int y = 0; y < m_OutHeight; ++y)
            {
                const int sy = m_HdrRows[y];
                const uint32_t* srcRow = reinterpret_cast<const uint32_t*>(rgb10 + static_cast<size_t>(sy) * srcStride);
                auto* dstY = reinterpret_cast<uint16_t*>(m_YuvFrame->data[0] + static_cast<size_t>(y) * m_YuvFrame->
                    linesize[0]);
                for (int x = 0; x < m_OutWidth; ++x)
                {
                    uint32_t px = srcRow[m_HdrColumns[x]];
                    int r = static_cast<int>(px & 0x3FFU);
                    int g = static_cast<int>((px >> 10) & 0x3FFU);
                    int b = static_cast<int>((px >> 20) & 0x3FFU);
                    int yVal = 64 + ((lut.yr[r] + lut.yg[g] + lut.yb[b] + 32768) >> 16);
                    dstY[x] = static_cast<uint16_t>(std::clamp(yVal, 64, 940) << 6);
                }
            }

            const int uvH = m_OutHeight / 2;
            const int uvW = m_OutWidth / 2;
            for (int cy = 0; cy < uvH; ++cy)
            {
                const int sy0 = m_HdrRows[cy * 2];
                const int sy1 = m_HdrRows[cy * 2 + 1];
                auto* dstUv = reinterpret_cast<uint16_t*>(m_YuvFrame->data[1] + static_cast<size_t>(cy) * m_YuvFrame->
                    linesize[1]);
                for (int cx = 0; cx < uvW; ++cx)
                {
                    const int sx0 = m_HdrColumns[cx * 2];
                    const int sx1 = m_HdrColumns[cx * 2 + 1];
                    int r0, g0, b0, r1, g1, b1, r2, g2, b2, r3, g3, b3;
                    sampleRgb10(sx0, sy0, r0, g0, b0);
                    sampleRgb10(sx1, sy0, r1, g1, b1);
                    sampleRgb10(sx0, sy1, r2, g2, b2);
                    sampleRgb10(sx1, sy1, r3, g3, b3);
                    int r = (r0 + r1 + r2 + r3 + 2) >> 2;
                    int g = (g0 + g1 + g2 + g3 + 2) >> 2;
                    int b = (b0 + b1 + b2 + b3 + 2) >> 2;
                    int uVal = 512 + ((lut.ur[r] + lut.ug[g] + lut.ub[b] + 32768) >> 16);
                    int vVal = 512 + ((lut.vr[r] + lut.vg[g] + lut.vb[b] + 32768) >> 16);
                    dstUv[cx * 2 + 0] = static_cast<uint16_t>(std::clamp(uVal, 64, 960) << 6);
                    dstUv[cx * 2 + 1] = static_cast<uint16_t>(std::clamp(vVal, 64, 960) << 6);
                }
            }
            return true;
        }

        if (m_CodecCtx->pix_fmt == AV_PIX_FMT_YUV444P10LE)
        {
            for (int y = 0; y < m_OutHeight; ++y)
            {
                const int sy = m_HdrRows[y];
                const uint32_t* srcRow = reinterpret_cast<const uint32_t*>(rgb10 + static_cast<size_t>(sy) * srcStride);
                auto* dstY = reinterpret_cast<uint16_t*>(m_YuvFrame->data[0] + static_cast<size_t>(y) * m_YuvFrame->
                    linesize[0]);
                auto* dstU = reinterpret_cast<uint16_t*>(m_YuvFrame->data[1] + static_cast<size_t>(y) * m_YuvFrame->
                    linesize[1]);
                auto* dstV = reinterpret_cast<uint16_t*>(m_YuvFrame->data[2] + static_cast<size_t>(y) * m_YuvFrame->
                    linesize[2]);
                for (int x = 0; x < m_OutWidth; ++x)
                {
                    uint32_t px = srcRow[m_HdrColumns[x]];
                    int r = static_cast<int>(px & 0x3FFU);
                    int g = static_cast<int>((px >> 10) & 0x3FFU);
                    int b = static_cast<int>((px >> 20) & 0x3FFU);
                    int yVal = 64 + ((lut.yr[r] + lut.yg[g] + lut.yb[b] + 32768) >> 16);
                    int uVal = 512 + ((lut.ur[r] + lut.ug[g] + lut.ub[b] + 32768) >> 16);
                    int vVal = 512 + ((lut.vr[r] + lut.vg[g] + lut.vb[b] + 32768) >> 16);
                    dstY[x] = static_cast<uint16_t>(std::clamp(yVal, 64, 940));
                    dstU[x] = static_cast<uint16_t>(std::clamp(uVal, 64, 960));
                    dstV[x] = static_cast<uint16_t>(std::clamp(vVal, 64, 960));
                }
            }
            return true;
        }

        if (!EnsureSwsContext(AV_PIX_FMT_X2BGR10LE)) return false;
        const uint8_t* srcSlices[4] = {rgb10, nullptr, nullptr, nullptr};
        int srcLinesizes[4] = {srcStride, 0, 0, 0};
        sws_scale(m_SwsCtx, srcSlices, srcLinesizes, 0, m_SrcHeight, m_YuvFrame->data, m_YuvFrame->linesize);
        return true;
    }

    ID3D11Device* HostVideoEncoder::GetD3dInputDevice() const
    {
        if (!m_HardwareFrame) return nullptr;
        auto* frames = reinterpret_cast<AVHWFramesContext*>(m_CodecCtx->hw_frames_ctx->data);
        return static_cast<AVD3D11VADeviceContext*>(frames->device_ctx->hwctx)->device;
    }

    bool HostVideoEncoder::PrepareHardwareFrame(ID3D11Texture2D* nv12Texture)
    {
        av_frame_unref(m_HardwareFrame);
        if (av_hwframe_get_buffer(m_CodecCtx->hw_frames_ctx, m_HardwareFrame, 0) < 0) return false;
        if (nv12Texture)
        {
            auto* frames = reinterpret_cast<AVHWFramesContext*>(m_CodecCtx->hw_frames_ctx->data);
            auto* d3d = static_cast<AVD3D11VADeviceContext*>(frames->device_ctx->hwctx);
            ID3D11Device* textureDevice = nullptr;
            nv12Texture->GetDevice(&textureDevice);
            const bool sameDevice = textureDevice == d3d->device;
            textureDevice->Release();
            if (!sameDevice) return false;
            auto* target = reinterpret_cast<ID3D11Texture2D*>(m_HardwareFrame->data[0]);
            const auto slice = static_cast<UINT>(reinterpret_cast<intptr_t>(m_HardwareFrame->data[1]));
            // The encoder retains its own pooled texture while capture reuses its converter output.
            d3d->lock(d3d->lock_ctx);
            d3d->device_context->CopySubresourceRegion(target, slice, 0, 0, 0, nv12Texture, 0, nullptr);
            d3d->unlock(d3d->lock_ctx);
        }
        else if (av_hwframe_transfer_data(m_HardwareFrame, m_YuvFrame, 0) < 0)
        {
            return false;
        }
        return av_frame_copy_props(m_HardwareFrame, m_YuvFrame) >= 0;
    }

    bool HostVideoEncoder::EncodeFrame(
        const uint8_t* pixels,
        int srcStride,
        AVPixelFormat inputFormat,
        std::vector<EncodedPacket>& outPackets,
        uint64_t pixelRevision,
        ID3D11Texture2D* nv12Texture)
    {
        outPackets.clear();
        if (!m_CodecCtx || !m_YuvFrame || (nv12Texture ? !m_HardwareFrame : !pixels)) return false;

        if (pixelRevision == 0 || pixelRevision != m_ConvertedPixelRevision)
        {
            m_ConvertedPixelRevision = 0;
            if (!nv12Texture && av_frame_make_writable(m_YuvFrame) < 0) return false;

            if (nv12Texture)
            {
                if (!PrepareHardwareFrame(nv12Texture)) return false;
            }
            else if (inputFormat == AV_PIX_FMT_NV12)
            {
                if (!AcceptsNv12()) return false;
                const uint8_t* plane = pixels;
                for (int p = 0; p < 2; ++p)
                {
                    const int rows = p == 0 ? m_OutHeight : m_OutHeight / 2;
                    if (srcStride == m_OutWidth && m_YuvFrame->linesize[p] == m_OutWidth)
                    {
                        const size_t planeBytes = static_cast<size_t>(rows) * static_cast<size_t>(m_OutWidth);
                        std::memcpy(m_YuvFrame->data[p], plane, planeBytes);
                        plane += planeBytes;
                    }
                    else
                    {
                        for (int y = 0; y < rows; ++y)
                        {
                            std::memcpy(m_YuvFrame->data[p] + static_cast<size_t>(y) * m_YuvFrame->linesize[p],
                                        plane, m_OutWidth);
                            plane += srcStride;
                        }
                    }
                }
            }
            else if (inputFormat == AV_PIX_FMT_X2BGR10LE)
            {
                if (!FillYuvFromRgb10Pq(pixels, srcStride)) return false;
            }
            else
            {
                if (!EnsureSwsContext(inputFormat)) return false;
                const uint8_t* srcSlices[4] = {pixels, nullptr, nullptr, nullptr};
                int srcLinesizes[4] = {srcStride, 0, 0, 0};
                if (sws_scale(m_SwsCtx, srcSlices, srcLinesizes, 0, m_SrcHeight,
                              m_YuvFrame->data, m_YuvFrame->linesize) != m_OutHeight)
                {
                    return false;
                }
            }
            if (m_HardwareFrame && !nv12Texture && !PrepareHardwareFrame(nullptr)) return false;
            m_ConvertedPixelRevision = pixelRevision;
        }

        const bool forcedIdr = m_ForceIdr.exchange(false);
        AVFrame* submitFrame = m_HardwareFrame ? m_HardwareFrame : m_YuvFrame;
        submitFrame->pts = m_Pts++;
        if (forcedIdr)
        {
            submitFrame->pict_type = AV_PICTURE_TYPE_I;
            submitFrame->flags |= AV_FRAME_FLAG_KEY;
        }
        else
        {
            submitFrame->pict_type = AV_PICTURE_TYPE_NONE;
            submitFrame->flags &= ~AV_FRAME_FLAG_KEY;
        }

        auto drainPackets = [&]() -> bool
        {
            while (true)
            {
                int r = avcodec_receive_packet(m_CodecCtx, m_Pkt);
                if (r == AVERROR(EAGAIN) || r == AVERROR_EOF)
                {
                    return true;
                }
                if (r < 0)
                {
                    OSM_LOG_WARN("HostVideoEncoder(%s) avcodec_receive_packet failed: %d", m_EncoderName.c_str(), r);
                    return false;
                }

                EncodedPacket pkt;
                pkt.isKeyFrame = (m_Pkt->flags & AV_PKT_FLAG_KEY) != 0;
                pkt.codecId = m_CodecId;
                if (pkt.isKeyFrame && (m_CodecId == proto::VideoCodecId::H264 || m_CodecId ==
                    proto::VideoCodecId::HEVC))
                {
                    std::vector<uint8_t> inlinePs;
                    if (ExtractAnnexBParamSets(m_CodecId, m_Pkt->data, static_cast<size_t>(m_Pkt->size), inlinePs))
                    {
                        m_CachedParamSets = std::move(inlinePs);
                        pkt.data.assign(m_Pkt->data, m_Pkt->data + m_Pkt->size);
                    }
                    else if (!m_CachedParamSets.empty())
                    {
                        pkt.data.reserve(m_CachedParamSets.size() + static_cast<size_t>(m_Pkt->size));
                        pkt.data.insert(pkt.data.end(), m_CachedParamSets.begin(), m_CachedParamSets.end());
                        pkt.data.insert(pkt.data.end(), m_Pkt->data, m_Pkt->data + m_Pkt->size);
                    }
                    else
                    {
                        pkt.data.assign(m_Pkt->data, m_Pkt->data + m_Pkt->size);
                    }
                }
                else
                {
                    pkt.data.assign(m_Pkt->data, m_Pkt->data + m_Pkt->size);
                }
                outPackets.push_back(std::move(pkt));
                av_packet_unref(m_Pkt);
            }
        };

        int err = avcodec_send_frame(m_CodecCtx, submitFrame);
        if (err == AVERROR(EAGAIN))
        {
            if (!drainPackets())
            {
                if (forcedIdr) m_ForceIdr.store(true);
                return false;
            }
            err = avcodec_send_frame(m_CodecCtx, submitFrame);
        }
        if (err < 0)
        {
            if (forcedIdr) m_ForceIdr.store(true);
            if (err == AVERROR(EAGAIN))
            {
                drainPackets();
                return !outPackets.empty();
            }
            OSM_LOG_WARN("HostVideoEncoder(%s) avcodec_send_frame failed: %d", m_EncoderName.c_str(), err);
            return false;
        }

        if (!drainPackets())
        {
            return false;
        }

        return true;
    }
}
