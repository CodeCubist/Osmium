#pragma once

#include "OsmiumCommon.h"
#include <d3d11.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <string>
#include <vector>

namespace osmium
{
    struct CapturedFrame
    {
        std::vector<uint8_t> bgra;
        int width = 0;
        int height = 0;
        int stride = 0;
        uint64_t captureTimeUs = 0;
        bool updated = false;
        bool isHdr10Pq = false;
    };

    class HostCapture
    {
    public:
        HostCapture() = default;
        ~HostCapture();

        static std::vector<OsmiumDisplayInfo> EnumerateDisplays();

        bool Initialize(int displayIndex, bool captureCursor);
        void Shutdown();

        void SetCaptureCursor(bool captureCursor) { m_CaptureCursor = captureCursor; }
        bool CaptureNextFrame(CapturedFrame& outFrame, int timeoutMs, bool preferPqHdr = false);
        void RefreshOutputHdrState();

        int GetDisplayIndex() const { return m_DisplayIndex; }
        int GetWidth() const { return m_Width; }
        int GetHeight() const { return m_Height; }
        RECT GetDesktopRect() const { return m_DesktopRect; }
        bool IsOutputHdr() const { return m_OutputIsHdr; }
        float GetSdrWhiteScale() const { return m_SdrWhiteScale; }

    private:
        bool InitDuplication();
        void ReleaseD3DResources();
        bool RefreshMonitorBoundsForGdi();
        bool CaptureViaGdi(CapturedFrame& outFrame);
        void OverlayCursor(CapturedFrame& frame);
        bool EnsureHdrTonemapPipeline(UINT width, UINT height, DXGI_FORMAT srcFmt, DXGI_FORMAT dstFmt);
        bool ConvertAcquiredTexture(ID3D11Texture2D* srcTex, const D3D11_TEXTURE2D_DESC& srcDesc, bool outputPqHdr);
        void QueryOutputHdrStateAndWhite(const wchar_t* gdiDeviceName, DXGI_COLOR_SPACE_TYPE dxgiColorSpace,
                                         bool& outIsHdr, float& outSdrScale) const;

        int m_DisplayIndex = 0;
        bool m_CaptureCursor = true;
        int m_Width = 1920;
        int m_Height = 1080;
        RECT m_DesktopRect{0, 0, 1920, 1080};
        std::wstring m_DeviceName;
        bool m_OutputIsHdr = false;
        DXGI_COLOR_SPACE_TYPE m_OutputColorSpace = DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        float m_SdrWhiteScale = 1.0f;
        uint64_t m_LastWhiteQueryMs = 0;
        uint64_t m_LastDuplRetryMs = 0;
        uint64_t m_GdiFallbackUntilMs = 0;
        uint32_t m_ConsecutiveTimeouts = 0;
        uint32_t m_ConsecutiveDuplFailures = 0;
        DXGI_FORMAT m_LastLoggedAcqFmt = DXGI_FORMAT_UNKNOWN;

        Microsoft::WRL::ComPtr<ID3D11Device> m_Device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_Context;
        Microsoft::WRL::ComPtr<IDXGIOutputDuplication> m_Duplication;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_StagingTex;
        DXGI_FORMAT m_StagingFmt = DXGI_FORMAT_UNKNOWN;

        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_SrcCopyTex;
        DXGI_FORMAT m_SrcCopyFmt = DXGI_FORMAT_UNKNOWN;
        UINT m_SrcCopyW = 0;
        UINT m_SrcCopyH = 0;
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> m_SrcCopySrv;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_TonemapRtTex;
        DXGI_FORMAT m_TonemapFmt = DXGI_FORMAT_UNKNOWN;
        UINT m_TonemapW = 0;
        UINT m_TonemapH = 0;
        Microsoft::WRL::ComPtr<ID3D11RenderTargetView> m_TonemapRtv;
        Microsoft::WRL::ComPtr<ID3D11VertexShader> m_TonemapVs;
        Microsoft::WRL::ComPtr<ID3D11PixelShader> m_TonemapPs;
        Microsoft::WRL::ComPtr<ID3D11Buffer> m_TonemapCb;

        std::vector<uint8_t> m_LastFrameBgra;
        bool m_LastFrameIsHdr10Pq = false;
        bool m_HasLastFrame = false;
    };
}
