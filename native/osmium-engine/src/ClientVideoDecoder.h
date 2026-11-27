#pragma once

#include "OsmiumCommon.h"
#include "OsmiumProtocol.h"

#include <d3d11_4.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libswscale/swscale.h>
}

#include <atomic>
#include <map>
#include <mutex>
#include <string>
#include <vector>

namespace osmium
{
    class ClientVideoDecoder
    {
    public:
        ClientVideoDecoder() = default;
        ~ClientVideoDecoder();

        bool InitializeRenderer(HWND hwnd, bool enableVsync, int decoderPreference);

        bool InitializeCompositionRenderer(
            IUnknown* swapChainPanelUnk,
            int widthPx,
            int heightPx,
            float dpiScale,
            bool enableVsync,
            int decoderPreference,
            bool enableHdr);

        void Shutdown();

        void OnWindowResized(int width, int height, float dpiScale = 1.0f);
        void RepaintIfResized();
        void SetVsync(bool enableVsync) { m_Vsync.store(enableVsync); }
        void SetHdrRequested(bool hdr) { m_WantHdr.store(hdr); }
        void SetOverlayVisible(bool visible) { m_ShowOverlay.store(visible); }
        void ToggleOverlay() { m_ShowOverlay.store(!m_ShowOverlay.load()); }

        bool DecodeAndRenderFrame(
            proto::VideoCodecId codecId,
            bool isKeyFrame,
            int width,
            int height,
            uint8_t colorFlags,
            const uint8_t* data,
            size_t len,
            const OsmiumClientStats& statsSnapshot,
            double& outDecodeMs,
            bool forceImmediatePresent = false);

        void UpdateOverlayStats(const OsmiumClientStats& statsSnapshot);

        std::string GetDecoderName() const { return m_DecoderName; }
        std::string GetCodecName() const { return m_CodecName; }
        bool IsHardwareDecoding() const { return m_IsHardware; }
        int GetVideoWidth() const { return m_VideoWidth; }
        int GetVideoHeight() const { return m_VideoHeight; }

    private:
        bool CreateD3DDevice();
        bool InitD3D11Hwnd(HWND hwnd);
        bool InitD3D11Composition(IUnknown* panelUnk, bool hdr);
        bool InitVideoProcessor(int srcW, int srcH, DXGI_FORMAT srcFmt, bool srcHdr);
        bool InitQuadRenderer();
        bool EnsureDecoder(proto::VideoCodecId codecId, int width, int height, uint8_t colorFlags, bool isKeyFrame);
        bool PresentD3D11Frame(AVFrame* frame, bool srcHdr, const OsmiumClientStats& stats, bool forceImmediatePresent);
        bool PresentSoftwareFrame(AVFrame* frame, bool srcHdr, const OsmiumClientStats& stats,
                                  bool forceImmediatePresent);
        bool RenderHwSurfaceLocked(ID3D11Texture2D* srcTex, UINT subresource, int frameW, int frameH, bool srcHdr,
                                   UINT syncInterval, const OsmiumClientStats* stats);
        bool RenderBgraQuadLocked(int fw, int fh, bool srcHdr, UINT syncInterval, const OsmiumClientStats* stats);
        void DrawOverlay(const OsmiumClientStats& stats);
        void ResizeSwapChainIfNeeded(bool srcHdr);
        void RefreshClientHdrState(bool force = false);
        void ApplySwapChainHdrMetadata(bool enableHdrMeta);

        static enum AVPixelFormat GetHwFormat(AVCodecContext* ctx, const enum AVPixelFormat* pixFmts);

        HWND m_Hwnd = nullptr;
        HWND m_AnchorHwnd = nullptr;
        bool m_IsComposition = false;
        float m_DpiScale = 1.0f;
        std::atomic<bool> m_Vsync{true};
        std::atomic<bool> m_WantHdr{false};
        bool m_SwapChainIsHdr = false;
        bool m_ClientOutputIsHdr = false;
        float m_ClientSdrWhiteScale = 1.0f;
        uint64_t m_LastClientHdrQueryMs = 0;
        bool m_LastSrcHdr = false;
        int m_DecoderPref = 0;
        std::atomic<bool> m_ShowOverlay{false};

        Microsoft::WRL::ComPtr<IUnknown> m_PanelUnk;
        Microsoft::WRL::ComPtr<ID3D11Device> m_Device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_Context;
        Microsoft::WRL::ComPtr<IDXGIFactory2> m_Factory;
        Microsoft::WRL::ComPtr<IDXGISwapChain1> m_SwapChain;
        Microsoft::WRL::ComPtr<ID3D11RenderTargetView> m_BackbufferRtv;

        Microsoft::WRL::ComPtr<ID3D11VideoDevice> m_VideoDevice;
        Microsoft::WRL::ComPtr<ID3D11VideoContext> m_VideoContext;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> m_VpEnum;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessor> m_Vp;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> m_BackbufferVpOutView;
        int m_VpSrcW = 0;
        int m_VpSrcH = 0;
        DXGI_FORMAT m_VpSrcFmt = DXGI_FORMAT_UNKNOWN;
        bool m_VpSrcHdr = false;

        Microsoft::WRL::ComPtr<ID3D11VertexShader> m_VS;
        Microsoft::WRL::ComPtr<ID3D11PixelShader> m_PS;
        Microsoft::WRL::ComPtr<ID3D11Buffer> m_QuadCb;
        Microsoft::WRL::ComPtr<ID3D11SamplerState> m_Sampler;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_BgraTex;
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> m_BgraSrv;
        DXGI_FORMAT m_BgraTexFmt = DXGI_FORMAT_UNKNOWN;
        int m_BgraTexW = 0;
        int m_BgraTexH = 0;

        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_LastHwTex;
        int m_LastHwW = 0;
        int m_LastHwH = 0;
        DXGI_FORMAT m_LastHwFmt = DXGI_FORMAT_UNKNOWN;
        bool m_LastHwHdr = false;
        bool m_HasLastFrame = false;
        bool m_LastWasHw = false;
        bool m_VpOutputHdr = false;

        Microsoft::WRL::ComPtr<ID2D1Factory> m_D2DFactory;
        Microsoft::WRL::ComPtr<ID2D1RenderTarget> m_D2DRenderTarget;
        Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> m_BgBrush;
        Microsoft::WRL::ComPtr<ID2D1SolidColorBrush> m_TextBrush;
        Microsoft::WRL::ComPtr<IDWriteFactory> m_DWriteFactory;
        Microsoft::WRL::ComPtr<IDWriteTextFormat> m_TextFormat;

        AVCodecContext* m_CodecCtx = nullptr;
        AVBufferRef* m_HwDeviceCtx = nullptr;
        AVFrame* m_Frame = nullptr;
        AVFrame* m_SwFrame = nullptr;
        AVPacket* m_Pkt = nullptr;
        SwsContext* m_SwsCtx = nullptr;
        int m_SwsW = 0;
        int m_SwsH = 0;
        AVPixelFormat m_SwsSrcFmt = AV_PIX_FMT_NONE;
        int m_SwsSrcRange = 0;
        int m_SwsSrcHdr = 0;

        proto::VideoCodecId m_CurrentCodec = proto::VideoCodecId::H264;
        uint8_t m_CurrentColorFlags = 0;
        int m_VideoWidth = 0;
        int m_VideoHeight = 0;
        int m_WindowWidth = 1280;
        int m_WindowHeight = 720;
        bool m_NeedsResize = false;
        bool m_NeedsKeyframe = true;
        int m_ConsecutiveNoOutput = 0;
        bool m_IsHardware = false;
        std::string m_DecoderName = "Uninitialized";
        std::string m_CodecName = "H.264";

        std::mutex m_RenderMutex;
    };
}
