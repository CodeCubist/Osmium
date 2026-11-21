#include "HostVideoEncoder.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <mutex>

namespace osmium
{
    namespace
    {
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
    }

    HostVideoEncoder::~HostVideoEncoder()
    {
        Shutdown();
    }

    void HostVideoEncoder::Shutdown()
    {
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
        if (m_CodecCtx)
        {
            avcodec_free_context(&m_CodecCtx);
        }
        m_HdrActive = false;
        m_PqHdrActive = false;
        m_Yuv444Active = false;
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
        bool enableYuv444)
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

        for (const auto& cand : ordered)
        {
            if (TryOpenCandidate(cand, fps, bitrateKbps, enableHdr, enableYuv444))
            {
                m_YuvFrame = av_frame_alloc();
                m_YuvFrame->format = m_CodecCtx->pix_fmt;
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
                    if (AVMasteringDisplayMetadata* mdm = av_mastering_display_metadata_create_side_data(m_YuvFrame))
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

                OSM_LOG_INFO("HostVideoEncoder initialized: %s (%s, %dx%d @ %dfps, %d kbps, hdr=%d, pq=%d, yuv444=%d)",
                             m_EncoderName.c_str(), m_CodecName.c_str(), m_OutWidth, m_OutHeight, fps, bitrateKbps,
                             m_HdrActive ? 1 : 0, m_PqHdrActive ? 1 : 0, m_Yuv444Active ? 1 : 0);
                return true;
            }
        }

        OSM_LOG_ERROR("No suitable video encoder could be initialized");
        return false;
    }

    bool HostVideoEncoder::TryOpenCandidate(const EncoderCandidate& cand, int fps, int bitrateKbps, bool wantHdr,
                                            bool wantYuv444)
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
        ctx->rc_buffer_size = static_cast<int>(ctx->bit_rate / 2);
        ctx->gop_size = std::max(1, fps * 2);
        ctx->max_b_frames = 0;

        AVPixelFormat chosenFmt = AV_PIX_FMT_NV12;
        const enum AVPixelFormat* supportedFmts = nullptr;
        int numFmts = 0;
        bool supportsP010 = false, supportsYuv444 = false, supportsYuv444p10 = false, hasNv12 = false, hasYuv420p =
                 false;
        if (avcodec_get_supported_config(ctx, codec, AV_CODEC_CONFIG_PIX_FORMAT, 0,
                                         reinterpret_cast<const void**>(&supportedFmts), &numFmts) >= 0 &&
            supportedFmts && numFmts > 0)
        {
            for (int i = 0; i < numFmts; ++i)
            {
                if (supportedFmts[i] == AV_PIX_FMT_NV12) hasNv12 = true;
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
        }
        else if (name == "libopenh264")
        {
            av_opt_set(ctx->priv_data, "slice_mode", "fixed", 0);
            av_opt_set_int(ctx->priv_data, "allow_skip_frames", 0, 0);
        }
        else if (name == "libsvtav1")
        {
            av_opt_set_int(ctx->priv_data, "preset", 11, 0);
        }

        if (avcodec_open2(ctx, codec, nullptr) < 0)
        {
            avcodec_free_context(&ctx);
            return false;
        }

        m_CodecCtx = ctx;
        m_EncoderName = cand.ffmpegName;
        m_CodecName = cand.codecDisplay;
        m_CodecId = cand.codecId;
        m_IsHardware = cand.isHardware;
        m_HdrActive = hdrUsed;
        m_PqHdrActive = hdrUsed;
        m_Yuv444Active = yuv444Used;
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
        m_SwsCtx = sws_getContext(
            m_SrcWidth, m_SrcHeight, srcFmt,
            m_OutWidth, m_OutHeight, m_CodecCtx->pix_fmt,
            SWS_BILINEAR, nullptr, nullptr, nullptr);
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
        const bool sameSize = (m_SrcWidth == m_OutWidth && m_SrcHeight == m_OutHeight);

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
                int sy = sameSize
                             ? y
                             : std::min(m_SrcHeight - 1,
                                        static_cast<int>((static_cast<int64_t>(y) * m_SrcHeight) / m_OutHeight));
                const uint32_t* srcRow = reinterpret_cast<const uint32_t*>(rgb10 + static_cast<size_t>(sy) * srcStride);
                auto* dstY = reinterpret_cast<uint16_t*>(m_YuvFrame->data[0] + static_cast<size_t>(y) * m_YuvFrame->
                    linesize[0]);
                for (int x = 0; x < m_OutWidth; ++x)
                {
                    int sx = sameSize
                                 ? x
                                 : std::min(m_SrcWidth - 1,
                                            static_cast<int>((static_cast<int64_t>(x) * m_SrcWidth) / m_OutWidth));
                    uint32_t px = srcRow[sx];
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
                int sy0 = sameSize
                              ? (cy * 2)
                              : std::min(m_SrcHeight - 1,
                                         static_cast<int>((static_cast<int64_t>(cy * 2) * m_SrcHeight) / m_OutHeight));
                int sy1 = sameSize
                              ? std::min(m_SrcHeight - 1, cy * 2 + 1)
                              : std::min(m_SrcHeight - 1,
                                         static_cast<int>((static_cast<int64_t>(cy * 2 + 1) * m_SrcHeight) /
                                             m_OutHeight));
                auto* dstUv = reinterpret_cast<uint16_t*>(m_YuvFrame->data[1] + static_cast<size_t>(cy) * m_YuvFrame->
                    linesize[1]);
                for (int cx = 0; cx < uvW; ++cx)
                {
                    int sx0 = sameSize
                                  ? (cx * 2)
                                  : std::min(m_SrcWidth - 1,
                                             static_cast<int>((static_cast<int64_t>(cx * 2) * m_SrcWidth) /
                                                 m_OutWidth));
                    int sx1 = sameSize
                                  ? std::min(m_SrcWidth - 1, cx * 2 + 1)
                                  : std::min(m_SrcWidth - 1,
                                             static_cast<int>((static_cast<int64_t>(cx * 2 + 1) * m_SrcWidth) /
                                                 m_OutWidth));
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
                int sy = sameSize
                             ? y
                             : std::min(m_SrcHeight - 1,
                                        static_cast<int>((static_cast<int64_t>(y) * m_SrcHeight) / m_OutHeight));
                const uint32_t* srcRow = reinterpret_cast<const uint32_t*>(rgb10 + static_cast<size_t>(sy) * srcStride);
                auto* dstY = reinterpret_cast<uint16_t*>(m_YuvFrame->data[0] + static_cast<size_t>(y) * m_YuvFrame->
                    linesize[0]);
                auto* dstU = reinterpret_cast<uint16_t*>(m_YuvFrame->data[1] + static_cast<size_t>(y) * m_YuvFrame->
                    linesize[1]);
                auto* dstV = reinterpret_cast<uint16_t*>(m_YuvFrame->data[2] + static_cast<size_t>(y) * m_YuvFrame->
                    linesize[2]);
                for (int x = 0; x < m_OutWidth; ++x)
                {
                    int sx = sameSize
                                 ? x
                                 : std::min(m_SrcWidth - 1,
                                            static_cast<int>((static_cast<int64_t>(x) * m_SrcWidth) / m_OutWidth));
                    uint32_t px = srcRow[sx];
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

    bool HostVideoEncoder::EncodeFrame(
        const uint8_t* pixels,
        int srcStride,
        bool isRgb10Pq,
        std::vector<EncodedPacket>& outPackets)
    {
        outPackets.clear();
        if (!m_CodecCtx || !m_YuvFrame || !pixels) return false;

        if (av_frame_make_writable(m_YuvFrame) < 0) return false;

        if (isRgb10Pq)
        {
            if (!FillYuvFromRgb10Pq(pixels, srcStride)) return false;
        }
        else
        {
            if (!EnsureSwsContext(AV_PIX_FMT_BGRA)) return false;
            const uint8_t* srcSlices[4] = {pixels, nullptr, nullptr, nullptr};
            int srcLinesizes[4] = {srcStride, 0, 0, 0};
            sws_scale(m_SwsCtx, srcSlices, srcLinesizes, 0, m_SrcHeight,
                      m_YuvFrame->data, m_YuvFrame->linesize);
        }

        m_YuvFrame->pts = m_Pts++;
        if (m_ForceIdr.exchange(false))
        {
            m_YuvFrame->pict_type = AV_PICTURE_TYPE_I;
            m_YuvFrame->flags |= AV_FRAME_FLAG_KEY;
        }
        else
        {
            m_YuvFrame->pict_type = AV_PICTURE_TYPE_NONE;
            m_YuvFrame->flags &= ~AV_FRAME_FLAG_KEY;
        }

        int err = avcodec_send_frame(m_CodecCtx, m_YuvFrame);
        if (err < 0) return false;

        while (err >= 0)
        {
            err = avcodec_receive_packet(m_CodecCtx, m_Pkt);
            if (err == AVERROR(EAGAIN) || err == AVERROR_EOF)
            {
                break;
            }
            if (err < 0)
            {
                return false;
            }

            EncodedPacket pkt;
            pkt.isKeyFrame = (m_Pkt->flags & AV_PKT_FLAG_KEY) != 0;
            pkt.codecId = m_CodecId;
            pkt.data.assign(m_Pkt->data, m_Pkt->data + m_Pkt->size);
            outPackets.push_back(std::move(pkt));
            av_packet_unref(m_Pkt);
        }

        return true;
    }
}
