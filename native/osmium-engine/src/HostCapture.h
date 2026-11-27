#pragma once

#include "OsmiumCommon.h"
#include <d3d11_1.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <atomic>
#include <string>
#include <vector>

namespace osmium
{
    struct CapturedFrame
    {
        std::vector<uint8_t> pixels;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> nv12Texture;
        int width = 0;
        int height = 0;
        int stride = 0;
        uint64_t captureTimeUs = 0;
        // Zero denotes pixels that must be copied and converted for every capture.
        uint64_t pixelRevision = 0;
        bool updated = false;
        bool isHdr10Pq = false;
        bool isNv12 = false;
    };

    class HostCapture
    {
    public:
        HostCapture() = default;
        ~HostCapture();

        static std::vector<OsmiumDisplayInfo> EnumerateDisplays();

        bool Initialize(int displayIndex, bool captureCursor);
        void Shutdown();

        void SetCaptureCursor(bool captureCursor) { m_CaptureCursor.store(captureCursor, std::memory_order_relaxed); }

        void SetNv12OutputSize(int width, int height, ID3D11Device* encoderDevice = nullptr)
        {
            m_Nv12Width = width;
            m_Nv12Height = height;
            m_EncoderDevice = encoderDevice;
        }

        ID3D11Device* GetDevice() const { return m_Device.Get(); }
        bool CaptureNextFrame(CapturedFrame& outFrame, int initialWaitMs, bool preferPqHdr = false,
                              const std::atomic<bool>* privacyScreenActive = nullptr);
        bool HasDesktopDuplication() const { return m_Duplication != nullptr; }
        void RefreshOutputHdrState();

        int GetDisplayIndex() const { return m_DisplayIndex; }
        int GetWidth() const { return m_Width; }
        int GetHeight() const { return m_Height; }
        RECT GetDesktopRect() const { return m_DesktopRect; }
        bool IsOutputHdr() const { return m_OutputIsHdr; }
        float GetSdrWhiteScale() const { return m_SdrWhiteScale; }

    private:
        static constexpr int kMinCursorBoxSize = 64;
        static constexpr int kMaxCursorBoxSize = 512;

        bool InitDuplication();
        void ReleaseD3DResources();
        bool EnsureCursorScratch(int minSize);
        void ReleaseCursorScratch();
        bool QueryGdiCursorMetrics(HCURSOR hCursor, int& outHotX, int& outHotY, int& outW, int& outH) const;
        UINT ResolveGdiAniStep(HCURSOR hCursor, int curW, int curH, uint64_t nowMs);
        void UpdateDxgiPointer(const DXGI_OUTDUPL_FRAME_INFO& frameInfo);
        bool EnsureGpuCursorPipeline();
        bool EnsureGpuCursorTextures(int minWidth, int minHeight);
        bool DrawGpuCursorOntoInput(bool freshFrame, bool& outChanged);
        bool RefreshCachedNv12Cursor(bool keepOnGpu, bool captureCursor);
        bool RefreshMonitorBoundsForGdi();
        bool CaptureViaGdi(CapturedFrame& outFrame, bool captureCursor);
        void OverlayCursor(CapturedFrame& frame);
        bool EnsureHdrTonemapPipeline(UINT width, UINT height, DXGI_FORMAT srcFmt, DXGI_FORMAT dstFmt);
        bool ConvertAcquiredTexture(ID3D11Texture2D* srcTex, const D3D11_TEXTURE2D_DESC& srcDesc, bool outputPqHdr);
        bool PrepareNv12Conversion(int sourceWidth, int sourceHeight);
        bool CaptureNv12(ID3D11Texture2D* source, bool keepOnGpu, bool captureCursor);
        bool ReadStagingPixels(ID3D11Texture2D* texture, size_t rowBytes, size_t rows);
        void QueryOutputHdrStateAndWhite(const wchar_t* gdiDeviceName, DXGI_COLOR_SPACE_TYPE dxgiColorSpace,
                                         bool& outIsHdr, float& outSdrScale) const;

        int m_DisplayIndex = 0;
        std::atomic<bool> m_CaptureCursor{true};
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
        uint64_t m_LastDxgiFrameOrGdiProbeMs = 0;
        uint32_t m_InitialHdrTimeouts = 0;
        uint32_t m_ConsecutiveDuplFailures = 0;
        DXGI_FORMAT m_LastLoggedAcqFmt = DXGI_FORMAT_UNKNOWN;

        Microsoft::WRL::ComPtr<ID3D11Device> m_Device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> m_Context;
        Microsoft::WRL::ComPtr<IDXGIOutputDuplication> m_Duplication;
        bool m_DuplicationFrameHeld = false;
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

        int m_Nv12Width = 0;
        int m_Nv12Height = 0;
        ID3D11Device* m_EncoderDevice = nullptr;

        struct Nv12Conversion
        {
            int sourceWidth = 0;
            int sourceHeight = 0;
            int outputWidth = 0;
            int outputHeight = 0;
            bool gpuCursorSupported = false;
            bool hasCleanInput = false;
            bool hasDrawnCursor = false;
            int lastCursorX = 0;
            int lastCursorY = 0;
            int lastBoxLeft = 0;
            int lastBoxTop = 0;
            int lastBoxRight = 0;
            int lastBoxBottom = 0;
            int cursorTexWidth = 0;
            int cursorTexHeight = 0;
            int activeCursorW = 0;
            int activeCursorH = 0;
            bool uploadedFromDxgi = false;
            uint64_t uploadedDxgiShapeGen = 0;
            HCURSOR uploadedCursorHandle = nullptr;
            uint64_t uploadedCursorMs = 0;
            int uploadedHotX = 0;
            int uploadedHotY = 0;
            int uploadedGdiW = 0;
            int uploadedGdiH = 0;
            Microsoft::WRL::ComPtr<ID3D11VideoContext1> context;
            Microsoft::WRL::ComPtr<ID3D11VideoProcessor> processor;
            Microsoft::WRL::ComPtr<ID3D11VideoProcessorInputView> inputView;
            Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> outputView;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> input;
            Microsoft::WRL::ComPtr<ID3D11RenderTargetView> inputRtv;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> cleanInput;
            Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> cleanInputSrv;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> cursorBlackTex;
            Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> cursorBlackSrv;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> cursorWhiteTex;
            Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> cursorWhiteSrv;
            Microsoft::WRL::ComPtr<ID3D11VertexShader> cursorVs;
            Microsoft::WRL::ComPtr<ID3D11PixelShader> cursorPs;
            Microsoft::WRL::ComPtr<ID3D11Buffer> cursorCb;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> output;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> readback;
        } m_Nv12;

        HDC m_CursorScreenDc = nullptr;
        HDC m_CursorBlackDc = nullptr;
        HBITMAP m_CursorBlackBmp = nullptr;
        HGDIOBJ m_CursorBlackOld = nullptr;
        uint32_t* m_CursorBlackBits = nullptr;
        HDC m_CursorWhiteDc = nullptr;
        HBITMAP m_CursorWhiteBmp = nullptr;
        HGDIOBJ m_CursorWhiteOld = nullptr;
        uint32_t* m_CursorWhiteBits = nullptr;
        int m_CursorScratchSize = 0;
        std::vector<uint32_t> m_CursorPrevBlack;
        std::vector<uint32_t> m_CursorPrevWhite;

        // Animated (.ani) cursor stepping state for GDI / fallback path
        HCURSOR m_GdiAniHandle = nullptr;
        UINT m_GdiAniStep = 0;
        uint64_t m_GdiAniLastStepMs = 0;
        bool m_GdiAniProbedStatic = false;
        std::vector<uint32_t> m_GdiAniStep0Snapshot;

        // Native DXGI Desktop Duplication hardware pointer shape cache
        // (natively supports .ani frames at exact DWM timing + Accessibility oversized cursors)
        bool m_DxgiPointerSeen = false;
        bool m_DxgiPointerVisible = false;
        bool m_DxgiPointerHasShape = false;
        int m_DxgiPointerX = 0;
        int m_DxgiPointerY = 0;
        int m_DxgiPointerW = 0;
        int m_DxgiPointerH = 0;
        uint64_t m_DxgiPointerShapeGen = 0;
        std::vector<uint8_t> m_DxgiPointerRawBuf;
        std::vector<uint32_t> m_DxgiPointerBlack;
        std::vector<uint32_t> m_DxgiPointerWhite;

        std::vector<uint8_t> m_LastFramePixels;
        uint64_t m_LastFrameRevision = 0;
        bool m_LastFrameIsHdr10Pq = false;
        bool m_LastFrameIsNv12 = false;
        bool m_LastFrameInGpu = false;
        int m_LastFrameWidth = 0;
        int m_LastFrameHeight = 0;
        bool m_HasLastFrame = false;
    };
}
