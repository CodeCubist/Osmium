#pragma once

#include "OsmiumCommon.h"
#include "OsmiumProtocol.h"

#include <atomic>
#include <string>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/mastering_display_metadata.h>
#include <libavutil/opt.h>
#include <libswscale/swscale.h>
}

namespace osmium
{
    struct EncodedPacket
    {
        std::vector<uint8_t> data;
        bool isKeyFrame = false;
        proto::VideoCodecId codecId = proto::VideoCodecId::H264;
    };

    class HostVideoEncoder
    {
    public:
        HostVideoEncoder() = default;
        ~HostVideoEncoder();

        bool Initialize(
            int srcWidth,
            int srcHeight,
            int outWidth,
            int outHeight,
            int fps,
            int bitrateKbps,
            int codecPreference,
            int encoderPreference,
            bool enableHdr = false,
            bool enableYuv444 = false);

        void Shutdown();
        void ForceKeyFrame() { m_ForceIdr.store(true); }

        bool EncodeFrame(
            const uint8_t* pixels,
            int srcStride,
            bool isRgb10Pq,
            std::vector<EncodedPacket>& outPackets);

        bool EncodeBgra(
            const uint8_t* bgra,
            int srcStride,
            std::vector<EncodedPacket>& outPackets)
        {
            return EncodeFrame(bgra, srcStride, false, outPackets);
        }

        std::string GetEncoderName() const { return m_EncoderName; }
        std::string GetCodecName() const { return m_CodecName; }
        proto::VideoCodecId GetCodecId() const { return m_CodecId; }
        int GetSrcWidth() const { return m_SrcWidth; }
        int GetSrcHeight() const { return m_SrcHeight; }
        int GetOutWidth() const { return m_OutWidth; }
        int GetOutHeight() const { return m_OutHeight; }
        bool IsHardwareEncoder() const { return m_IsHardware; }
        bool IsHdrActive() const { return m_HdrActive; }
        bool IsPqHdrActive() const { return m_PqHdrActive; }
        bool IsYuv444Active() const { return m_Yuv444Active; }

    private:
        struct EncoderCandidate
        {
            const char* ffmpegName;
            proto::VideoCodecId codecId;
            const char* codecDisplay;
            bool isHardware;
        };

        bool TryOpenCandidate(const EncoderCandidate& cand, int fps, int bitrateKbps, bool wantHdr, bool wantYuv444);
        bool EnsureSwsContext(AVPixelFormat srcFmt);
        bool FillYuvFromRgb10Pq(const uint8_t* rgb10, int srcStride);

        AVCodecContext* m_CodecCtx = nullptr;
        AVFrame* m_YuvFrame = nullptr;
        AVPacket* m_Pkt = nullptr;
        SwsContext* m_SwsCtx = nullptr;
        AVPixelFormat m_SwsSrcFmt = AV_PIX_FMT_NONE;

        int m_SrcWidth = 0;
        int m_SrcHeight = 0;
        int m_OutWidth = 0;
        int m_OutHeight = 0;
        int64_t m_Pts = 0;

        std::string m_EncoderName;
        std::string m_CodecName;
        proto::VideoCodecId m_CodecId = proto::VideoCodecId::H264;
        bool m_IsHardware = false;
        bool m_HdrActive = false;
        bool m_PqHdrActive = false;
        bool m_Yuv444Active = false;
        std::atomic<bool> m_ForceIdr{true};
    };
}
