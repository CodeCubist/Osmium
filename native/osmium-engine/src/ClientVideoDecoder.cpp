#include "ClientVideoDecoder.h"

#include <d3dcompiler.h>
#include <algorithm>
#include <cstring>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "d3dcompiler.lib")

namespace osmium
{
    using Microsoft::WRL::ComPtr;

    namespace
    {
        MIDL_INTERFACE("63aad0b8-7c24-40ff-85a8-640d944cc325")
            ISwapChainPanelNativeWinUI3 : public IUnknown
        {
        public:
            virtual HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain* swapChain) = 0;
        };

        const char* kQuadVsSource = R"(
struct VSOut {
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};
VSOut main(uint vid : SV_VertexID) {
    VSOut o;
    float2 pos[4] = {
        float2(-1.0,  1.0),
        float2( 1.0,  1.0),
        float2(-1.0, -1.0),
        float2( 1.0, -1.0)
    };
    float2 uv[4] = {
        float2(0.0, 0.0),
        float2(1.0, 0.0),
        float2(0.0, 1.0),
        float2(1.0, 1.0)
    };
    o.pos = float4(pos[vid], 0.0, 1.0);
    o.uv = uv[vid];
    return o;
}
)";

        const char* kQuadPsSource = R"(
cbuffer CbQuad : register(b0) {
    uint quadMode; // 0 = sRGB->sRGB, 1 = PQ->PQ, 2 = PQ->sRGB tonemap, 3 = sRGB->PQ (200 nits ref)
    float sdrRefNits;
    float2 pad;
};
Texture2D tex0 : register(t0);
SamplerState samp0 : register(s0);

float3 SrgbToLinear(float3 s) {
    s = saturate(s);
    float3 lo = s / 12.92;
    float3 hi = pow((s + 0.055) / 1.055, 2.4);
    return lerp(lo, hi, step(0.04045, s));
}

float3 LinearToSrgb(float3 c) {
    c = saturate(c);
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(max(c, 1e-6), 1.0 / 2.4) - 0.055;
    return lerp(lo, hi, step(0.0031308, c));
}

float3 PqToNits(float3 pq) {
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;
    float3 clamped = saturate(pq);
    float3 p = pow(max(clamped, 1e-12), 1.0 / m2);
    float3 num = max(p - c1, 0.0);
    float3 den = max(c2 - c3 * p, 1e-6);
    float3 nits = 10000.0 * pow(max(num / den, 1e-12), 1.0 / m1);
    return lerp(0.0, nits, step(1e-6, clamped));
}

float3 NitsToPq(float3 nits) {
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;
    float3 L = saturate(nits / 10000.0);
    float3 Lm1 = pow(max(L, 1e-12), m1);
    float3 num = c1 + c2 * Lm1;
    float3 den = 1.0 + c3 * Lm1;
    float3 pq = pow(saturate(num / den), m2);
    return lerp(0.0, pq, step(1e-9, L));
}

float3 RollOffHighlights(float3 lin) {
    lin = max(lin, 0.0);
    const float knee = 0.85;
    float peak = max(lin.r, max(lin.g, lin.b));
    if (peak <= knee) {
        return lin;
    }
    float over = peak - knee;
    float newPeak = knee + (1.0 - knee) * (over / (over + 0.45));
    return saturate(lin * (newPeak / max(peak, 1e-6)));
}

float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET {
    float3 raw = tex0.Sample(samp0, uv).rgb;
    if (quadMode == 1) {
        return float4(saturate(raw), 1.0);
    }
    if (quadMode == 2) {
        float refNits = max(sdrRefNits, 80.0);
        float3 bt2020 = PqToNits(raw) / refNits;
        float3 lin709;
        lin709.r = dot(bt2020, float3( 1.660491, -0.587641, -0.072850));
        lin709.g = dot(bt2020, float3(-0.124550,  1.132900, -0.008349));
        lin709.b = dot(bt2020, float3(-0.018151, -0.100579,  1.118730));
        return float4(LinearToSrgb(RollOffHighlights(max(lin709, 0.0))), 1.0);
    }
    if (quadMode == 3) {
        float3 lin709 = SrgbToLinear(raw) * max(sdrRefNits, 80.0);
        float3 lin2020;
        lin2020.r = dot(lin709, float3(0.6274040, 0.3292830, 0.0433130));
        lin2020.g = dot(lin709, float3(0.0690970, 0.9195400, 0.0113620));
        lin2020.b = dot(lin709, float3(0.0163916, 0.0880132, 0.8955950));
        return float4(NitsToPq(max(lin2020, 0.0)), 1.0);
    }
    return float4(raw, 1.0);
}
)";

        struct CbQuadData
        {
            uint32_t quadMode;
            float sdrRefNits;
            float pad[2];
        };

        DXGI_HDR_METADATA_HDR10 BuildStandardHdr10Metadata()
        {
            DXGI_HDR_METADATA_HDR10 meta{};
            meta.RedPrimary[0] = 35400;
            meta.RedPrimary[1] = 14600;
            meta.GreenPrimary[0] = 8500;
            meta.GreenPrimary[1] = 39850;
            meta.BluePrimary[0] = 6550;
            meta.BluePrimary[1] = 2300;
            meta.WhitePoint[0] = 15635;
            meta.WhitePoint[1] = 16450;
            meta.MaxMasteringLuminance = 1000;
            meta.MinMasteringLuminance = 50;
            meta.MaxContentLightLevel = 1000;
            meta.MaxFrameAverageLightLevel = 400;
            return meta;
        }

        HWND FindUiThreadAnchorWindow()
        {
            DWORD tid = GetCurrentThreadId();
            HWND active = GetActiveWindow();
            if (active && IsWindowVisible(active)) return active;
            HWND fg = GetForegroundWindow();
            if (fg && GetWindowThreadProcessId(fg, nullptr) == tid && IsWindowVisible(fg)) return fg;
            HWND best = nullptr;
            EnumThreadWindows(tid, [](HWND hwnd, LPARAM lp) -> BOOL
            {
                if (IsWindowVisible(hwnd))
                {
                    RECT rc{};
                    if (GetWindowRect(hwnd, &rc) && (rc.right - rc.left) > 200 && (rc.bottom - rc.top) > 150)
                    {
                        *reinterpret_cast<HWND*>(lp) = hwnd;
                        return FALSE;
                    }
                }
                return TRUE;
            }, reinterpret_cast<LPARAM>(&best));
            return best;
        }
    }

    ClientVideoDecoder::~ClientVideoDecoder()
    {
        Shutdown();
    }

    void ClientVideoDecoder::Shutdown()
    {
        std::lock_guard<std::mutex> lock(m_RenderMutex);

        if (m_SwsCtx)
        {
            sws_freeContext(m_SwsCtx);
            m_SwsCtx = nullptr;
        }
        if (m_Pkt) av_packet_free(&m_Pkt);
        if (m_SwFrame) av_frame_free(&m_SwFrame);
        if (m_Frame) av_frame_free(&m_Frame);
        if (m_CodecCtx) avcodec_free_context(&m_CodecCtx);
        if (m_HwDeviceCtx) av_buffer_unref(&m_HwDeviceCtx);

        m_BgBrush.Reset();
        m_TextBrush.Reset();
        m_D2DRenderTarget.Reset();
        m_TextFormat.Reset();
        m_DWriteFactory.Reset();
        m_D2DFactory.Reset();

        m_BgraSrv.Reset();
        m_BgraTex.Reset();
        m_BgraTexFmt = DXGI_FORMAT_UNKNOWN;
        m_BgraTexW = 0;
        m_BgraTexH = 0;
        m_LastHwTex.Reset();
        m_LastHwW = 0;
        m_LastHwH = 0;
        m_LastHwFmt = DXGI_FORMAT_UNKNOWN;
        m_LastHwHdr = false;
        m_LastSrcHdr = false;
        m_HasLastFrame = false;
        m_LastWasHw = false;
        m_NeedsKeyframe = true;
        m_ConsecutiveNoOutput = 0;
        m_Sampler.Reset();
        m_QuadCb.Reset();
        m_PS.Reset();
        m_VS.Reset();
        m_SwapChainIsHdr = false;
        m_ClientOutputIsHdr = false;
        m_ClientSdrWhiteScale = 1.0f;
        m_LastClientHdrQueryMs = 0;
        m_AnchorHwnd = nullptr;

        m_Vp.Reset();
        m_VpEnum.Reset();
        m_VideoContext.Reset();
        m_VideoDevice.Reset();

        m_BackbufferRtv.Reset();
        if (m_IsComposition && m_PanelUnk)
        {
            ComPtr<ISwapChainPanelNativeWinUI3> panelNative;
            if (SUCCEEDED(m_PanelUnk.As(&panelNative)) && panelNative)
            {
                panelNative->SetSwapChain(nullptr);
            }
        }
        m_PanelUnk.Reset();
        m_SwapChain.Reset();
        m_Factory.Reset();
        m_Context.Reset();
        m_Device.Reset();
    }

    bool ClientVideoDecoder::CreateD3DDevice()
    {
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        D3D_FEATURE_LEVEL outLevel{};

        HRESULT hr = D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &m_Device, &outLevel, &m_Context);

        if (FAILED(hr))
        {
            flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
            hr = D3D11CreateDevice(
                nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
                levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &m_Device, &outLevel, &m_Context);
        }
        if (FAILED(hr))
        {
            OSM_LOG_ERROR("D3D11CreateDevice failed: 0x%08X", hr);
            return false;
        }

        ComPtr<ID3D10Multithread> mt;
        if (SUCCEEDED(m_Device.As(&mt)))
        {
            mt->SetMultithreadProtected(TRUE);
        }

        ComPtr<IDXGIDevice> dxgiDevice;
        ComPtr<IDXGIAdapter> adapter;
        if (FAILED(m_Device.As(&dxgiDevice)) ||
            FAILED(dxgiDevice->GetAdapter(&adapter)) ||
            FAILED(adapter->GetParent(IID_PPV_ARGS(&m_Factory))))
        {
            return false;
        }

        m_Device.As(&m_VideoDevice);
        m_Context.As(&m_VideoContext);

        D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, m_D2DFactory.GetAddressOf());
        DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                            reinterpret_cast<IUnknown**>(m_DWriteFactory.GetAddressOf()));
        if (m_DWriteFactory)
        {
            m_DWriteFactory->CreateTextFormat(
                L"Consolas", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL,
                13.0f, L"en-us", &m_TextFormat);
        }
        return true;
    }

    bool ClientVideoDecoder::InitializeRenderer(HWND hwnd, bool enableVsync, int decoderPreference)
    {
        std::lock_guard<std::mutex> lock(m_RenderMutex);
        m_Hwnd = hwnd;
        m_IsComposition = false;
        m_Vsync.store(enableVsync);
        m_DecoderPref = decoderPreference;

        RECT rc{};
        if (GetClientRect(hwnd, &rc))
        {
            m_WindowWidth = std::max(1L, rc.right - rc.left);
            m_WindowHeight = std::max(1L, rc.bottom - rc.top);
        }

        if (!InitD3D11Hwnd(hwnd))
        {
            return false;
        }
        InitQuadRenderer();

        m_Frame = av_frame_alloc();
        m_SwFrame = av_frame_alloc();
        m_Pkt = av_packet_alloc();

        if (m_BackbufferRtv && m_SwapChain)
        {
            const float black[4] = {0.04f, 0.04f, 0.06f, 1.0f};
            m_Context->ClearRenderTargetView(m_BackbufferRtv.Get(), black);
            m_SwapChain->Present(0, 0);
        }

        return true;
    }

    bool ClientVideoDecoder::InitializeCompositionRenderer(
        IUnknown* swapChainPanelUnk,
        int widthPx,
        int heightPx,
        float dpiScale,
        bool enableVsync,
        int decoderPreference,
        bool enableHdr)
    {
        if (!swapChainPanelUnk) return false;
        std::lock_guard<std::mutex> lock(m_RenderMutex);

        m_Hwnd = nullptr;
        m_AnchorHwnd = FindUiThreadAnchorWindow();
        m_IsComposition = true;
        m_DpiScale = std::max(0.5f, dpiScale);
        m_WindowWidth = std::max(64, widthPx);
        m_WindowHeight = std::max(64, heightPx);
        m_Vsync.store(enableVsync);
        m_DecoderPref = decoderPreference;
        m_WantHdr.store(enableHdr);
        m_LastSrcHdr = false;
        RefreshClientHdrState(true);

        if (!InitD3D11Composition(swapChainPanelUnk, false))
        {
            return false;
        }
        InitQuadRenderer();

        m_Frame = av_frame_alloc();
        m_SwFrame = av_frame_alloc();
        m_Pkt = av_packet_alloc();

        if (m_BackbufferRtv && m_SwapChain)
        {
            const float black[4] = {0.043f, 0.051f, 0.071f, 1.0f};
            m_Context->ClearRenderTargetView(m_BackbufferRtv.Get(), black);
            m_SwapChain->Present(0, 0);
        }

        return true;
    }

    bool ClientVideoDecoder::InitD3D11Hwnd(HWND hwnd)
    {
        if (!CreateD3DDevice()) return false;

        DXGI_SWAP_CHAIN_DESC1 scDesc{};
        scDesc.Width = static_cast<UINT>(m_WindowWidth);
        scDesc.Height = static_cast<UINT>(m_WindowHeight);
        scDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        scDesc.SampleDesc.Count = 1;
        scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scDesc.BufferCount = 2;
        scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;

        HRESULT hr = m_Factory->CreateSwapChainForHwnd(m_Device.Get(), hwnd, &scDesc, nullptr, nullptr, &m_SwapChain);
        if (FAILED(hr))
        {
            OSM_LOG_ERROR("CreateSwapChainForHwnd failed: 0x%08X", hr);
            return false;
        }

        m_Factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);

        ComPtr<ID3D11Texture2D> backbuffer;
        if (FAILED(m_SwapChain->GetBuffer(0, IID_PPV_ARGS(&backbuffer))) ||
            FAILED(m_Device->CreateRenderTargetView(backbuffer.Get(), nullptr, &m_BackbufferRtv)))
        {
            return false;
        }
        return true;
    }

    bool ClientVideoDecoder::InitD3D11Composition(IUnknown* panelUnk, bool hdr)
    {
        if (!CreateD3DDevice()) return false;

        m_PanelUnk = panelUnk;
        ComPtr<ISwapChainPanelNativeWinUI3> panelNative;
        HRESULT hrQi = m_PanelUnk.As(&panelNative);
        if (FAILED(hrQi) || !panelNative)
        {
            OSM_LOG_ERROR("SwapChainPanel does not implement ISwapChainPanelNative (WinUI3): 0x%08X", hrQi);
            return false;
        }

        DXGI_SWAP_CHAIN_DESC1 scDesc{};
        scDesc.Width = static_cast<UINT>(m_WindowWidth);
        scDesc.Height = static_cast<UINT>(m_WindowHeight);
        scDesc.Format = hdr ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
        scDesc.SampleDesc.Count = 1;
        scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scDesc.BufferCount = 2;
        scDesc.Scaling = DXGI_SCALING_STRETCH;
        scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        scDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

        HRESULT hr = m_Factory->CreateSwapChainForComposition(m_Device.Get(), &scDesc, nullptr, &m_SwapChain);
        if (FAILED(hr) && hdr)
        {
            scDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            hr = m_Factory->CreateSwapChainForComposition(m_Device.Get(), &scDesc, nullptr, &m_SwapChain);
            hdr = false;
        }
        if (FAILED(hr) || !m_SwapChain)
        {
            OSM_LOG_ERROR("CreateSwapChainForComposition failed: 0x%08X", hr);
            return false;
        }

        m_SwapChainIsHdr = false;
        ComPtr<IDXGISwapChain3> sc3;
        if (hdr && SUCCEEDED(m_SwapChain.As(&sc3)))
        {
            UINT support = 0;
            if (SUCCEEDED(sc3->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, &support)) &&
                (support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT))
            {
                if (SUCCEEDED(sc3->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)))
                {
                    m_SwapChainIsHdr = true;
                }
            }
        }

        ComPtr<IDXGISwapChain2> sc2;
        if (SUCCEEDED(m_SwapChain.As(&sc2)))
        {
            DXGI_MATRIX_3X2_F scale{};
            scale._11 = 1.0f / m_DpiScale;
            scale._22 = 1.0f / m_DpiScale;
            sc2->SetMatrixTransform(&scale);
        }

        hr = panelNative->SetSwapChain(m_SwapChain.Get());
        if (FAILED(hr))
        {
            OSM_LOG_ERROR("ISwapChainPanelNative::SetSwapChain failed: 0x%08X", hr);
            return false;
        }

        ComPtr<ID3D11Texture2D> backbuffer;
        if (FAILED(m_SwapChain->GetBuffer(0, IID_PPV_ARGS(&backbuffer))) ||
            FAILED(m_Device->CreateRenderTargetView(backbuffer.Get(), nullptr, &m_BackbufferRtv)))
        {
            return false;
        }
        return true;
    }

    bool ClientVideoDecoder::InitQuadRenderer()
    {
        if (!m_Device) return false;
        ComPtr<ID3DBlob> vsBlob, psBlob, errBlob;
        if (FAILED(
            D3DCompile(kQuadVsSource, strlen(kQuadVsSource), nullptr, nullptr, nullptr, "main", "vs_5_0", 0, 0, &vsBlob,
                &errBlob)))
        {
            return false;
        }
        if (FAILED(
            D3DCompile(kQuadPsSource, strlen(kQuadPsSource), nullptr, nullptr, nullptr, "main", "ps_5_0", 0, 0, &psBlob,
                &errBlob)))
        {
            return false;
        }
        if (FAILED(m_Device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &m_VS)) ||
            FAILED(m_Device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &m_PS)))
        {
            return false;
        }

        D3D11_SAMPLER_DESC sampDesc{};
        sampDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        m_Device->CreateSamplerState(&sampDesc, &m_Sampler);

        D3D11_BUFFER_DESC cbd{};
        cbd.ByteWidth = sizeof(CbQuadData);
        cbd.Usage = D3D11_USAGE_DYNAMIC;
        cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cbd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        m_Device->CreateBuffer(&cbd, nullptr, &m_QuadCb);
        return true;
    }

    void ClientVideoDecoder::RefreshClientHdrState(bool force)
    {
        uint64_t nowMs = NowMillis();
        if (!force && m_LastClientHdrQueryMs != 0 && (nowMs - m_LastClientHdrQueryMs) < 1000)
        {
            return;
        }
        m_LastClientHdrQueryMs = nowMs;

        HWND refHwnd = (m_Hwnd && IsWindow(m_Hwnd))
                           ? m_Hwnd
                           : ((m_AnchorHwnd && IsWindow(m_AnchorHwnd)) ? m_AnchorHwnd : nullptr);
        HMONITOR mon = refHwnd
                           ? MonitorFromWindow(refHwnd, MONITOR_DEFAULTTONEAREST)
                           : MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);

        MONITORINFOEXW mi{};
        mi.cbSize = sizeof(mi);
        if (!mon || !GetMonitorInfoW(mon, &mi))
        {
            m_ClientOutputIsHdr = false;
            m_ClientSdrWhiteScale = 1.0f;
            return;
        }

        bool hdrEnabled = false;
        float sdrScale = 1.0f;
        bool resolvedByDc = false;

        UINT32 numPaths = 0, numModes = 0;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &numPaths, &numModes) == ERROR_SUCCESS && numPaths > 0)
        {
            std::vector<DISPLAYCONFIG_PATH_INFO> paths(numPaths);
            std::vector<DISPLAYCONFIG_MODE_INFO> modes(numModes);
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &numPaths, paths.data(), &numModes, modes.data(), nullptr) ==
                ERROR_SUCCESS)
            {
                for (UINT32 i = 0; i < numPaths; ++i)
                {
                    DISPLAYCONFIG_SOURCE_DEVICE_NAME srcName{};
                    srcName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    srcName.header.size = sizeof(srcName);
                    srcName.header.adapterId = paths[i].sourceInfo.adapterId;
                    srcName.header.id = paths[i].sourceInfo.id;
                    if (DisplayConfigGetDeviceInfo(&srcName.header) != ERROR_SUCCESS) continue;

                    if (_wcsicmp(srcName.viewGdiDeviceName, mi.szDevice) == 0)
                    {
                        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO advInfo{};
                        advInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                        advInfo.header.size = sizeof(advInfo);
                        advInfo.header.adapterId = paths[i].targetInfo.adapterId;
                        advInfo.header.id = paths[i].targetInfo.id;
                        if (DisplayConfigGetDeviceInfo(&advInfo.header) == ERROR_SUCCESS)
                        {
                            hdrEnabled = (advInfo.advancedColorEnabled != 0) && (advInfo.wideColorEnforced == 0);
                            resolvedByDc = true;
                        }

                        DISPLAYCONFIG_SDR_WHITE_LEVEL whiteLevel{};
                        whiteLevel.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                        whiteLevel.header.size = sizeof(whiteLevel);
                        whiteLevel.header.adapterId = paths[i].targetInfo.adapterId;
                        whiteLevel.header.id = paths[i].targetInfo.id;
                        if (DisplayConfigGetDeviceInfo(&whiteLevel.header) == ERROR_SUCCESS && whiteLevel.SDRWhiteLevel
                            >= 1000)
                        {
                            sdrScale = std::clamp(static_cast<float>(whiteLevel.SDRWhiteLevel) / 1000.0f, 1.0f, 12.5f);
                        }
                        else if (hdrEnabled)
                        {
                            sdrScale = 2.5f;
                        }
                        break;
                    }
                }
            }
        }

        if (!resolvedByDc && m_Factory)
        {
            for (UINT a = 0; !resolvedByDc; ++a)
            {
                ComPtr<IDXGIAdapter> adapter;
                if (m_Factory->EnumAdapters(a, &adapter) == DXGI_ERROR_NOT_FOUND) break;
                for (UINT o = 0;; ++o)
                {
                    ComPtr<IDXGIOutput> output;
                    if (adapter->EnumOutputs(o, &output) == DXGI_ERROR_NOT_FOUND) break;
                    ComPtr<IDXGIOutput6> output6;
                    if (SUCCEEDED(output.As(&output6)) && output6)
                    {
                        DXGI_OUTPUT_DESC1 d1{};
                        if (SUCCEEDED(output6->GetDesc1(&d1)) && _wcsicmp(d1.DeviceName, mi.szDevice) == 0)
                        {
                            hdrEnabled = (d1.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ||
                                d1.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709);
                            sdrScale = hdrEnabled ? std::max(1.0f, d1.MaxLuminance / 80.0f * 0.25f) : 1.0f;
                            resolvedByDc = true;
                            break;
                        }
                    }
                }
            }
        }

        m_ClientOutputIsHdr = hdrEnabled;
        m_ClientSdrWhiteScale = sdrScale;
    }

    void ClientVideoDecoder::ApplySwapChainHdrMetadata(bool enableHdrMeta)
    {
        if (!m_SwapChain) return;
        ComPtr<IDXGISwapChain4> sc4;
        if (FAILED(m_SwapChain.As(&sc4)) || !sc4) return;
        if (!enableHdrMeta)
        {
            sc4->SetHDRMetaData(DXGI_HDR_METADATA_TYPE_NONE, 0, nullptr);
            return;
        }
        DXGI_HDR_METADATA_HDR10 meta = BuildStandardHdr10Metadata();
        sc4->SetHDRMetaData(DXGI_HDR_METADATA_TYPE_HDR10, sizeof(meta), &meta);
    }

    bool ClientVideoDecoder::InitVideoProcessor(int srcW, int srcH, DXGI_FORMAT srcFmt, bool srcHdr)
    {
        if (!m_VideoDevice || !m_VideoContext) return false;
        if (m_Vp && m_VpSrcW == srcW && m_VpSrcH == srcH && m_VpSrcFmt == srcFmt &&
            m_VpSrcHdr == srcHdr && m_VpOutputHdr == m_SwapChainIsHdr)
        {
            return true;
        }

        m_Vp.Reset();
        m_VpEnum.Reset();

        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputWidth = static_cast<UINT>(srcW);
        content.InputHeight = static_cast<UINT>(srcH);
        content.OutputWidth = static_cast<UINT>(std::max(1, m_WindowWidth));
        content.OutputHeight = static_cast<UINT>(std::max(1, m_WindowHeight));
        content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

        if (FAILED(m_VideoDevice->CreateVideoProcessorEnumerator(&content, &m_VpEnum)) ||
            FAILED(m_VideoDevice->CreateVideoProcessor(m_VpEnum.Get(), 0, &m_Vp)))
        {
            return false;
        }

        m_VideoContext->VideoProcessorSetStreamAutoProcessingMode(m_Vp.Get(), 0, FALSE);

        ComPtr<ID3D11VideoContext1> vc1;
        if (SUCCEEDED(m_VideoContext.As(&vc1)))
        {
            vc1->VideoProcessorSetStreamColorSpace1(
                m_Vp.Get(), 0,
                srcHdr ? DXGI_COLOR_SPACE_YCBCR_STUDIO_G2084_LEFT_P2020 : DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709);
            vc1->VideoProcessorSetOutputColorSpace1(
                m_Vp.Get(),
                m_SwapChainIsHdr
                    ? DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020
                    : DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
        }

        ComPtr<ID3D11VideoContext2> vc2;
        if (SUCCEEDED(m_VideoContext.As(&vc2)))
        {
            DXGI_HDR_METADATA_HDR10 hdrMeta = BuildStandardHdr10Metadata();
            if (srcHdr)
            {
                vc2->VideoProcessorSetStreamHDRMetaData(m_Vp.Get(), 0, DXGI_HDR_METADATA_TYPE_HDR10, sizeof(hdrMeta),
                                                        &hdrMeta);
            }
            else
            {
                vc2->VideoProcessorSetStreamHDRMetaData(m_Vp.Get(), 0, DXGI_HDR_METADATA_TYPE_NONE, 0, nullptr);
            }

            if (m_SwapChainIsHdr)
            {
                vc2->VideoProcessorSetOutputHDRMetaData(m_Vp.Get(), DXGI_HDR_METADATA_TYPE_HDR10, sizeof(hdrMeta),
                                                        &hdrMeta);
            }
            else
            {
                vc2->VideoProcessorSetOutputHDRMetaData(m_Vp.Get(), DXGI_HDR_METADATA_TYPE_NONE, 0, nullptr);
            }
        }

        m_VpSrcW = srcW;
        m_VpSrcH = srcH;
        m_VpSrcFmt = srcFmt;
        m_VpSrcHdr = srcHdr;
        m_VpOutputHdr = m_SwapChainIsHdr;
        return true;
    }

    void ClientVideoDecoder::OnWindowResized(int width, int height, float dpiScale)
    {
        std::lock_guard<std::mutex> lock(m_RenderMutex);
        if (width <= 0 || height <= 0) return;
        float safeScale = std::max(0.5f, dpiScale);
        if (width != m_WindowWidth || height != m_WindowHeight || std::abs(safeScale - m_DpiScale) > 0.002f)
        {
            m_WindowWidth = width;
            m_WindowHeight = height;
            m_DpiScale = safeScale;
            m_NeedsResize = true;
        }
    }

    void ClientVideoDecoder::RepaintIfResized()
    {
        std::lock_guard<std::mutex> lock(m_RenderMutex);
        if (!m_NeedsResize || !m_SwapChain) return;
        ResizeSwapChainIfNeeded(m_LastSrcHdr);
        if (!m_BackbufferRtv || !m_SwapChain) return;

        if (m_HasLastFrame)
        {
            if (m_LastWasHw && m_LastHwTex && m_LastHwW > 0 && m_LastHwH > 0)
            {
                if (RenderHwSurfaceLocked(m_LastHwTex.Get(), 0, m_LastHwW, m_LastHwH, m_LastHwHdr, 0, nullptr))
                {
                    return;
                }
            }
            else if (m_BgraSrv && m_BgraTexW > 0 && m_BgraTexH > 0)
            {
                if (RenderBgraQuadLocked(m_BgraTexW, m_BgraTexH, m_LastSrcHdr, 0, nullptr))
                {
                    return;
                }
            }
        }

        const float bgSdr[4] = {0.043f, 0.051f, 0.071f, 1.0f};
        const float bgHdr[4] = {0.0f, 0.0f, 0.0f, 1.0f};
        m_Context->ClearRenderTargetView(m_BackbufferRtv.Get(), m_SwapChainIsHdr ? bgHdr : bgSdr);
        m_SwapChain->Present(0, 0);
    }

    void ClientVideoDecoder::ResizeSwapChainIfNeeded(bool srcHdr)
    {
        RefreshClientHdrState();
        bool wantHdr = srcHdr && m_WantHdr.load() && m_ClientOutputIsHdr;
        bool hdrModeChanged = (wantHdr != m_SwapChainIsHdr);
        if (!m_NeedsResize && !hdrModeChanged) return;
        if (!m_SwapChain) return;
        m_NeedsResize = false;

        m_BgBrush.Reset();
        m_TextBrush.Reset();
        m_D2DRenderTarget.Reset();
        m_BackbufferRtv.Reset();

        DXGI_FORMAT targetFmt = wantHdr ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
        HRESULT hr = m_SwapChain->ResizeBuffers(0, static_cast<UINT>(m_WindowWidth), static_cast<UINT>(m_WindowHeight),
                                                targetFmt, 0);
        if (FAILED(hr) && wantHdr)
        {
            wantHdr = false;
            targetFmt = DXGI_FORMAT_B8G8R8A8_UNORM;
            hr = m_SwapChain->ResizeBuffers(0, static_cast<UINT>(m_WindowWidth), static_cast<UINT>(m_WindowHeight),
                                            targetFmt, 0);
        }

        bool prevHdr = m_SwapChainIsHdr;
        if (hdrModeChanged || FAILED(hr))
        {
            m_SwapChainIsHdr = false;
            ComPtr<IDXGISwapChain3> sc3;
            if (SUCCEEDED(m_SwapChain.As(&sc3)))
            {
                if (wantHdr)
                {
                    UINT support = 0;
                    if (SUCCEEDED(sc3->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, &support)) &&
                        (support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT) &&
                        SUCCEEDED(sc3->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)))
                    {
                        m_SwapChainIsHdr = true;
                        ApplySwapChainHdrMetadata(true);
                    }
                    else
                    {
                        sc3->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
                        ApplySwapChainHdrMetadata(false);
                    }
                }
                else
                {
                    sc3->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709);
                    ApplySwapChainHdrMetadata(false);
                }
            }
            OSM_LOG_INFO("Client SwapChain color space switched: hdr=%d (srcHdr=%d, wantHdr=%d, clientWinHdr=%d)",
                         m_SwapChainIsHdr ? 1 : 0, srcHdr ? 1 : 0, m_WantHdr.load() ? 1 : 0,
                         m_ClientOutputIsHdr ? 1 : 0);
        }

        if (m_IsComposition)
        {
            ComPtr<IDXGISwapChain2> sc2;
            if (SUCCEEDED(m_SwapChain.As(&sc2)))
            {
                DXGI_MATRIX_3X2_F scale{};
                scale._11 = 1.0f / m_DpiScale;
                scale._22 = 1.0f / m_DpiScale;
                sc2->SetMatrixTransform(&scale);
            }
        }

        ComPtr<ID3D11Texture2D> backbuffer;
        if (SUCCEEDED(m_SwapChain->GetBuffer(0, IID_PPV_ARGS(&backbuffer))))
        {
            m_Device->CreateRenderTargetView(backbuffer.Get(), nullptr, &m_BackbufferRtv);
        }

        if (m_SwapChainIsHdr != prevHdr)
        {
            m_Vp.Reset();
            m_VpEnum.Reset();
        }
    }

    enum AVPixelFormat ClientVideoDecoder::GetHwFormat(AVCodecContext*, const enum AVPixelFormat* pixFmts)
    {
        for (const enum AVPixelFormat* p = pixFmts; *p != AV_PIX_FMT_NONE; ++p)
        {
            if (*p == AV_PIX_FMT_D3D11)
            {
                return *p;
            }
        }
        return pixFmts[0];
    }

    bool ClientVideoDecoder::EnsureDecoder(proto::VideoCodecId codecId, int width, int height, uint8_t colorFlags,
                                           bool isKeyFrame)
    {
        bool streamChanged = (!m_CodecCtx || m_CurrentCodec != codecId || m_VideoWidth != width || m_VideoHeight !=
            height ||
            (colorFlags != m_CurrentColorFlags && isKeyFrame));
        if (!streamChanged)
        {
            if (m_NeedsKeyframe && !isKeyFrame)
            {
                return false;
            }
            return true;
        }

        if (!isKeyFrame)
        {
            m_NeedsKeyframe = true;
            return false;
        }

        if (m_CodecCtx) avcodec_free_context(&m_CodecCtx);
        if (m_HwDeviceCtx) av_buffer_unref(&m_HwDeviceCtx);

        AVCodecID avCodecId = AV_CODEC_ID_H264;
        m_CodecName = "H.264";
        if (codecId == proto::VideoCodecId::HEVC)
        {
            avCodecId = AV_CODEC_ID_HEVC;
            m_CodecName = "HEVC";
        }
        else if (codecId == proto::VideoCodecId::AV1)
        {
            avCodecId = AV_CODEC_ID_AV1;
            m_CodecName = "AV1";
        }

        const AVCodec* decoder = nullptr;
        if (avCodecId == AV_CODEC_ID_AV1 && m_DecoderPref == 2)
        {
            decoder = avcodec_find_decoder_by_name("libdav1d");
        }
        if (!decoder)
        {
            decoder = avcodec_find_decoder(avCodecId);
        }
        if (!decoder)
        {
            OSM_LOG_ERROR("FFmpeg decoder not found for codec %s", m_CodecName.c_str());
            return false;
        }

        m_CodecCtx = avcodec_alloc_context3(decoder);
        if (!m_CodecCtx) return false;

        m_CodecCtx->width = width;
        m_CodecCtx->height = height;
        m_CodecCtx->flags |= AV_CODEC_FLAG_LOW_DELAY;
        m_CodecCtx->flags2 |= AV_CODEC_FLAG2_FAST;

        bool useHw = (m_DecoderPref != 2);
        m_IsHardware = false;

        if (useHw && m_Device && m_VideoDevice)
        {
            AVBufferRef* hwDev = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
            if (hwDev)
            {
                auto* hwCtx = reinterpret_cast<AVHWDeviceContext*>(hwDev->data);
                auto* d3dCtx = reinterpret_cast<AVD3D11VADeviceContext*>(hwCtx->hwctx);
                d3dCtx->device = m_Device.Get();
                m_Device->AddRef();
                d3dCtx->device_context = m_Context.Get();
                m_Context->AddRef();

                if (av_hwdevice_ctx_init(hwDev) >= 0)
                {
                    m_HwDeviceCtx = hwDev;
                    m_CodecCtx->hw_device_ctx = av_buffer_ref(m_HwDeviceCtx);
                    m_CodecCtx->get_format = ClientVideoDecoder::GetHwFormat;
                    m_IsHardware = true;
                }
                else
                {
                    av_buffer_unref(&hwDev);
                }
            }
        }

        if (!m_IsHardware)
        {
            m_CodecCtx->thread_count = 4;
            m_CodecCtx->thread_type = FF_THREAD_SLICE;
        }

        if (avcodec_open2(m_CodecCtx, decoder, nullptr) < 0)
        {
            avcodec_free_context(&m_CodecCtx);
            if (m_HwDeviceCtx) av_buffer_unref(&m_HwDeviceCtx);
            return false;
        }

        m_CurrentCodec = codecId;
        m_CurrentColorFlags = colorFlags;
        m_VideoWidth = width;
        m_VideoHeight = height;
        m_NeedsKeyframe = false;
        m_ConsecutiveNoOutput = 0;
        m_DecoderName = std::string(decoder->name) + (m_IsHardware ? " (D3D11VA)" : " (Software)");
        OSM_LOG_INFO("ClientVideoDecoder opened: %s (%dx%d)", m_DecoderName.c_str(), width, height);
        return true;
    }

    bool ClientVideoDecoder::DecodeAndRenderFrame(
        proto::VideoCodecId codecId,
        bool isKeyFrame,
        int width,
        int height,
        uint8_t colorFlags,
        const uint8_t* data,
        size_t len,
        const OsmiumClientStats& statsSnapshot,
        double& outDecodeMs)
    {
        std::lock_guard<std::mutex> lock(m_RenderMutex);
        outDecodeMs = 0.0;

        if (!EnsureDecoder(codecId, width, height, colorFlags, isKeyFrame))
        {
            return false;
        }

        uint64_t t0 = NowMicros();
        av_packet_unref(m_Pkt);
        m_Pkt->data = const_cast<uint8_t*>(data);
        m_Pkt->size = static_cast<int>(len);

        int err = avcodec_send_packet(m_CodecCtx, m_Pkt);
        if (err < 0)
        {
            avcodec_flush_buffers(m_CodecCtx);
            m_NeedsKeyframe = true;
            m_ConsecutiveNoOutput = 0;
            return false;
        }

        bool renderedAny = false;
        while (err >= 0)
        {
            err = avcodec_receive_frame(m_CodecCtx, m_Frame);
            if (err == AVERROR(EAGAIN) || err == AVERROR_EOF)
            {
                break;
            }
            if (err < 0)
            {
                avcodec_flush_buffers(m_CodecCtx);
                m_NeedsKeyframe = true;
                m_ConsecutiveNoOutput = 0;
                return false;
            }

            const bool srcHdr = ((colorFlags & proto::kVideoFlagHdr) != 0) &&
                (m_Frame->color_trc != AVCOL_TRC_BT709);
            m_LastSrcHdr = srcHdr;

            outDecodeMs = static_cast<double>(NowMicros() - t0) / 1000.0;
            ResizeSwapChainIfNeeded(srcHdr);

            if (m_Frame->format == AV_PIX_FMT_D3D11)
            {
                if (!PresentD3D11Frame(m_Frame, srcHdr, statsSnapshot))
                {
                    av_frame_unref(m_SwFrame);
                    if (av_hwframe_transfer_data(m_SwFrame, m_Frame, 0) >= 0)
                    {
                        PresentSoftwareFrame(m_SwFrame, srcHdr, statsSnapshot);
                    }
                }
            }
            else
            {
                PresentSoftwareFrame(m_Frame, srcHdr, statsSnapshot);
            }
            renderedAny = true;
            av_frame_unref(m_Frame);
        }

        if (renderedAny)
        {
            m_ConsecutiveNoOutput = 0;
            m_NeedsKeyframe = false;
            return true;
        }
        if (err == AVERROR(EAGAIN))
        {
            m_ConsecutiveNoOutput++;
            if (m_ConsecutiveNoOutput >= 3)
            {
                m_NeedsKeyframe = true;
                return false;
            }
            return true;
        }
        return false;
    }

    bool ClientVideoDecoder::RenderHwSurfaceLocked(
        ID3D11Texture2D* srcTex,
        UINT subresource,
        int frameW,
        int frameH,
        bool srcHdr,
        UINT syncInterval,
        const OsmiumClientStats* stats)
    {
        if (!m_SwapChain || !m_VideoDevice || !m_VideoContext || !m_BackbufferRtv || !srcTex) return false;

        D3D11_TEXTURE2D_DESC srcDesc{};
        srcTex->GetDesc(&srcDesc);
        if (!InitVideoProcessor(static_cast<int>(srcDesc.Width), static_cast<int>(srcDesc.Height), srcDesc.Format,
                                srcHdr))
        {
            return false;
        }

        ComPtr<ID3D11Texture2D> backbuffer;
        if (FAILED(m_SwapChain->GetBuffer(0, IID_PPV_ARGS(&backbuffer)))) return false;

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivDesc{};
        ivDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        ivDesc.Texture2D.ArraySlice = subresource;

        ComPtr<ID3D11VideoProcessorInputView> inputView;
        if (FAILED(m_VideoDevice->CreateVideoProcessorInputView(srcTex, m_VpEnum.Get(), &ivDesc, &inputView)))
        {
            m_Vp.Reset();
            m_VpEnum.Reset();
            if (!InitVideoProcessor(static_cast<int>(srcDesc.Width), static_cast<int>(srcDesc.Height), srcDesc.Format,
                                    srcHdr) ||
                FAILED(m_VideoDevice->CreateVideoProcessorInputView(srcTex, m_VpEnum.Get(), &ivDesc, &inputView)))
            {
                return false;
            }
        }

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovDesc{};
        ovDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        ComPtr<ID3D11VideoProcessorOutputView> outputView;
        if (FAILED(
            m_VideoDevice->CreateVideoProcessorOutputView(backbuffer.Get(), m_VpEnum.Get(), &ovDesc, &outputView)))
        {
            m_Vp.Reset();
            m_VpEnum.Reset();
            if (!InitVideoProcessor(static_cast<int>(srcDesc.Width), static_cast<int>(srcDesc.Height), srcDesc.Format,
                                    srcHdr) ||
                FAILED(m_VideoDevice->CreateVideoProcessorInputView(srcTex, m_VpEnum.Get(), &ivDesc, &inputView)) ||
                FAILED(
                    m_VideoDevice->CreateVideoProcessorOutputView(backbuffer.Get(), m_VpEnum.Get(), &ovDesc, &outputView
                    )))
            {
                return false;
            }
        }

        const float bgSdr[4] = {0.043f, 0.051f, 0.071f, 1.0f};
        const float bgHdr[4] = {0.0f, 0.0f, 0.0f, 1.0f};
        m_Context->ClearRenderTargetView(m_BackbufferRtv.Get(), m_SwapChainIsHdr ? bgHdr : bgSdr);

        double srcAspect = static_cast<double>(frameW) / std::max(1, frameH);
        double dstAspect = static_cast<double>(m_WindowWidth) / std::max(1, m_WindowHeight);
        RECT dstRect{0, 0, m_WindowWidth, m_WindowHeight};
        if (srcAspect > dstAspect)
        {
            int h = static_cast<int>(m_WindowWidth / srcAspect);
            int top = (m_WindowHeight - h) / 2;
            dstRect = {0, top, m_WindowWidth, top + h};
        }
        else
        {
            int w = static_cast<int>(m_WindowHeight * srcAspect);
            int left = (m_WindowWidth - w) / 2;
            dstRect = {left, 0, left + w, m_WindowHeight};
        }
        RECT srcRect{0, 0, frameW, frameH};

        m_VideoContext->VideoProcessorSetStreamSourceRect(m_Vp.Get(), 0, TRUE, &srcRect);
        m_VideoContext->VideoProcessorSetStreamDestRect(m_Vp.Get(), 0, TRUE, &dstRect);
        m_VideoContext->VideoProcessorSetOutputTargetRect(m_Vp.Get(), TRUE, &dstRect);

        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = inputView.Get();

        if (FAILED(m_VideoContext->VideoProcessorBlt(m_Vp.Get(), outputView.Get(), 0, 1, &stream)))
        {
            m_Vp.Reset();
            m_VpEnum.Reset();
            if (!InitVideoProcessor(static_cast<int>(srcDesc.Width), static_cast<int>(srcDesc.Height), srcDesc.Format,
                                    srcHdr) ||
                FAILED(m_VideoDevice->CreateVideoProcessorInputView(srcTex, m_VpEnum.Get(), &ivDesc, &inputView)) ||
                FAILED(
                    m_VideoDevice->CreateVideoProcessorOutputView(backbuffer.Get(), m_VpEnum.Get(), &ovDesc, &outputView
                    )))
            {
                return false;
            }
            m_VideoContext->VideoProcessorSetStreamSourceRect(m_Vp.Get(), 0, TRUE, &srcRect);
            m_VideoContext->VideoProcessorSetStreamDestRect(m_Vp.Get(), 0, TRUE, &dstRect);
            m_VideoContext->VideoProcessorSetOutputTargetRect(m_Vp.Get(), TRUE, &dstRect);
            stream.pInputSurface = inputView.Get();
            if (FAILED(m_VideoContext->VideoProcessorBlt(m_Vp.Get(), outputView.Get(), 0, 1, &stream)))
            {
                return false;
            }
        }

        if (stats && !m_IsComposition && m_ShowOverlay.load())
        {
            DrawOverlay(*stats);
        }

        m_SwapChain->Present(syncInterval, 0);
        return true;
    }

    bool ClientVideoDecoder::PresentD3D11Frame(AVFrame* frame, bool srcHdr, const OsmiumClientStats& stats)
    {
        auto* srcTex = reinterpret_cast<ID3D11Texture2D*>(frame->data[0]);
        UINT subresource = static_cast<UINT>(reinterpret_cast<intptr_t>(frame->data[1]));
        if (!srcTex) return false;

        if (!RenderHwSurfaceLocked(srcTex, subresource, frame->width, frame->height, srcHdr, m_Vsync.load() ? 1U : 0U,
                                   &stats))
        {
            return false;
        }

        D3D11_TEXTURE2D_DESC srcDesc{};
        srcTex->GetDesc(&srcDesc);
        if (!m_LastHwTex || m_LastHwW != static_cast<int>(srcDesc.Width) || m_LastHwH != static_cast<int>(srcDesc.
            Height) || m_LastHwFmt != srcDesc.Format)
        {
            m_LastHwTex.Reset();
            D3D11_TEXTURE2D_DESC copyDesc = srcDesc;
            copyDesc.ArraySize = 1;
            copyDesc.MipLevels = 1;
            copyDesc.Usage = D3D11_USAGE_DEFAULT;
            copyDesc.CPUAccessFlags = 0;
            copyDesc.MiscFlags = 0;
            copyDesc.BindFlags = (srcDesc.BindFlags & D3D11_BIND_DECODER)
                                     ? D3D11_BIND_DECODER
                                     : D3D11_BIND_SHADER_RESOURCE;
            if (SUCCEEDED(m_Device->CreateTexture2D(&copyDesc, nullptr, &m_LastHwTex)))
            {
                m_LastHwFmt = srcDesc.Format;
            }
        }
        if (m_LastHwTex)
        {
            m_Context->CopySubresourceRegion(m_LastHwTex.Get(), 0, 0, 0, 0, srcTex, subresource, nullptr);
        }
        m_LastHwW = frame->width;
        m_LastHwH = frame->height;
        m_LastHwHdr = srcHdr;
        m_HasLastFrame = true;
        m_LastWasHw = true;
        return true;
    }

    bool ClientVideoDecoder::RenderBgraQuadLocked(int fw, int fh, bool srcHdr, UINT syncInterval,
                                                  const OsmiumClientStats* stats)
    {
        if (!m_SwapChain || !m_BackbufferRtv || !m_VS || !m_PS || !m_BgraSrv) return false;

        const float bgSdr[4] = {0.043f, 0.051f, 0.071f, 1.0f};
        const float bgHdr[4] = {0.0f, 0.0f, 0.0f, 1.0f};
        m_Context->ClearRenderTargetView(m_BackbufferRtv.Get(), m_SwapChainIsHdr ? bgHdr : bgSdr);
        ID3D11RenderTargetView* rtv = m_BackbufferRtv.Get();
        m_Context->OMSetRenderTargets(1, &rtv, nullptr);

        uint32_t quadMode = 0;
        if (srcHdr && m_SwapChainIsHdr) quadMode = 1U;
        else if (srcHdr && !m_SwapChainIsHdr) quadMode = 2U;
        else if (!srcHdr && m_SwapChainIsHdr) quadMode = 3U;

        if (m_QuadCb)
        {
            D3D11_MAPPED_SUBRESOURCE cbMap{};
            if (SUCCEEDED(m_Context->Map(m_QuadCb.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &cbMap)))
            {
                auto* cb = static_cast<CbQuadData*>(cbMap.pData);
                cb->quadMode = quadMode;
                cb->sdrRefNits = 200.0f;
                cb->pad[0] = cb->pad[1] = 0.0f;
                m_Context->Unmap(m_QuadCb.Get(), 0);
            }
        }

        double srcAspect = static_cast<double>(fw) / std::max(1, fh);
        double dstAspect = static_cast<double>(m_WindowWidth) / std::max(1, m_WindowHeight);
        D3D11_VIEWPORT vp{};
        vp.MinDepth = 0.0f;
        vp.MaxDepth = 1.0f;
        if (srcAspect > dstAspect)
        {
            vp.Width = static_cast<float>(m_WindowWidth);
            vp.Height = static_cast<float>(m_WindowWidth / srcAspect);
            vp.TopLeftX = 0.0f;
            vp.TopLeftY = (m_WindowHeight - vp.Height) * 0.5f;
        }
        else
        {
            vp.Height = static_cast<float>(m_WindowHeight);
            vp.Width = static_cast<float>(m_WindowHeight * srcAspect);
            vp.TopLeftY = 0.0f;
            vp.TopLeftX = (m_WindowWidth - vp.Width) * 0.5f;
        }
        m_Context->RSSetViewports(1, &vp);

        m_Context->IASetInputLayout(nullptr);
        m_Context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        m_Context->VSSetShader(m_VS.Get(), nullptr, 0);
        m_Context->PSSetShader(m_PS.Get(), nullptr, 0);
        ID3D11Buffer* cbPtr = m_QuadCb.Get();
        m_Context->PSSetConstantBuffers(0, 1, &cbPtr);
        ID3D11ShaderResourceView* srv = m_BgraSrv.Get();
        m_Context->PSSetShaderResources(0, 1, &srv);
        ID3D11SamplerState* samp = m_Sampler.Get();
        m_Context->PSSetSamplers(0, 1, &samp);
        m_Context->Draw(4, 0);

        ID3D11ShaderResourceView* nullSrv = nullptr;
        m_Context->PSSetShaderResources(0, 1, &nullSrv);

        if (stats && !m_IsComposition && m_ShowOverlay.load())
        {
            DrawOverlay(*stats);
        }

        m_SwapChain->Present(syncInterval, 0);
        return true;
    }

    bool ClientVideoDecoder::PresentSoftwareFrame(AVFrame* frame, bool srcHdr, const OsmiumClientStats& stats)
    {
        if (!m_SwapChain || !m_BackbufferRtv || !m_VS || !m_PS) return false;

        int fw = frame->width;
        int fh = frame->height;
        auto fmt = static_cast<AVPixelFormat>(frame->format);
        int srcRange = (frame->color_range == AVCOL_RANGE_JPEG) ? 1 : 0;
        int srcHdrInt = srcHdr ? 1 : 0;
        AVPixelFormat dstSwsFmt = srcHdr ? AV_PIX_FMT_RGBA64LE : AV_PIX_FMT_BGRA;
        DXGI_FORMAT dstTexFmt = srcHdr ? DXGI_FORMAT_R16G16B16A16_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;

        if (!m_SwsCtx || m_SwsW != fw || m_SwsH != fh || m_SwsSrcFmt != fmt ||
            m_SwsSrcRange != srcRange || m_SwsSrcHdr != srcHdrInt)
        {
            if (m_SwsCtx) sws_freeContext(m_SwsCtx);
            m_SwsCtx = sws_getContext(fw, fh, fmt, fw, fh, dstSwsFmt, SWS_BILINEAR, nullptr, nullptr, nullptr);
            if (m_SwsCtx)
            {
                const int* coeff = sws_getCoefficients(srcHdr ? SWS_CS_BT2020 : SWS_CS_ITU709);
                sws_setColorspaceDetails(m_SwsCtx, coeff, srcRange, coeff, 1, 0, 1 << 16, 1 << 16);
            }
            m_SwsW = fw;
            m_SwsH = fh;
            m_SwsSrcFmt = fmt;
            m_SwsSrcRange = srcRange;
            m_SwsSrcHdr = srcHdrInt;
        }
        if (!m_SwsCtx) return false;

        if (!m_BgraTex || m_BgraTexFmt != dstTexFmt || m_BgraTexW != fw || m_BgraTexH != fh)
        {
            m_BgraSrv.Reset();
            m_BgraTex.Reset();
            D3D11_TEXTURE2D_DESC td{};
            td.Width = static_cast<UINT>(fw);
            td.Height = static_cast<UINT>(fh);
            td.MipLevels = 1;
            td.ArraySize = 1;
            td.Format = dstTexFmt;
            td.SampleDesc.Count = 1;
            td.Usage = D3D11_USAGE_DYNAMIC;
            td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            td.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            if (FAILED(m_Device->CreateTexture2D(&td, nullptr, &m_BgraTex)) ||
                FAILED(m_Device->CreateShaderResourceView(m_BgraTex.Get(), nullptr, &m_BgraSrv)))
            {
                m_BgraTexFmt = DXGI_FORMAT_UNKNOWN;
                return false;
            }
            m_BgraTexFmt = dstTexFmt;
            m_BgraTexW = fw;
            m_BgraTexH = fh;
        }

        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (SUCCEEDED(m_Context->Map(m_BgraTex.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        {
            uint8_t* dstSlices[4] = {static_cast<uint8_t*>(mapped.pData), nullptr, nullptr, nullptr};
            int dstLinesizes[4] = {static_cast<int>(mapped.RowPitch), 0, 0, 0};
            sws_scale(m_SwsCtx, frame->data, frame->linesize, 0, fh, dstSlices, dstLinesizes);
            m_Context->Unmap(m_BgraTex.Get(), 0);
        }

        if (!RenderBgraQuadLocked(fw, fh, srcHdr, m_Vsync.load() ? 1U : 0U, &stats))
        {
            return false;
        }
        m_HasLastFrame = true;
        m_LastWasHw = false;
        return true;
    }

    void ClientVideoDecoder::DrawOverlay(const OsmiumClientStats& stats)
    {
        if (!m_D2DFactory || !m_TextFormat || !m_SwapChain) return;

        if (!m_D2DRenderTarget)
        {
            ComPtr<IDXGISurface> surface;
            if (FAILED(m_SwapChain->GetBuffer(0, IID_PPV_ARGS(&surface)))) return;
            D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
                D2D1_RENDER_TARGET_TYPE_DEFAULT,
                D2D1::PixelFormat(DXGI_FORMAT_UNKNOWN, D2D1_ALPHA_MODE_PREMULTIPLIED));
            if (FAILED(m_D2DFactory->CreateDxgiSurfaceRenderTarget(surface.Get(), &props, &m_D2DRenderTarget)))
            {
                return;
            }
            m_D2DRenderTarget->CreateSolidColorBrush(D2D1::ColorF(0.05f, 0.06f, 0.09f, 0.78f), &m_BgBrush);
            m_D2DRenderTarget->CreateSolidColorBrush(D2D1::ColorF(0.45f, 0.85f, 1.0f, 1.0f), &m_TextBrush);
        }

        if (!m_D2DRenderTarget || !m_BgBrush || !m_TextBrush) return;

        const wchar_t* routeLabel =
            stats.transportRoute == 1 ? L"LAN IPv4" : (stats.transportRoute == 2 ? L"IPv6" : L"Direct");
        wchar_t hud[512];
        swprintf_s(
            hud,
            L"Osmium Stream  |  %dx%d  %.1f FPS  |  %.2f Mbps\n"
            L"Decoder: %S (%S)  |  Decode: %.2f ms  |  RTT: %u ms\n"
            L"Transport: %s (%S)  |  Dropped: %u  |  Ctrl+Alt+Shift+Q Exit",
            stats.videoWidth, stats.videoHeight, stats.fps, stats.bitrateMbps,
            m_CodecName.c_str(), m_DecoderName.c_str(), stats.decodeLatencyMs, stats.rttMs,
            routeLabel, stats.peerEndpoint, stats.droppedFrames);

        m_D2DRenderTarget->BeginDraw();
        D2D1_ROUNDED_RECT bgRect = D2D1::RoundedRect(D2D1::RectF(14.0f, 14.0f, 590.0f, 78.0f), 8.0f, 8.0f);
        m_D2DRenderTarget->FillRoundedRectangle(&bgRect, m_BgBrush.Get());
        D2D1_RECT_F textRect = D2D1::RectF(24.0f, 20.0f, 582.0f, 74.0f);
        m_D2DRenderTarget->DrawTextW(hud, static_cast<UINT32>(wcslen(hud)), m_TextFormat.Get(), &textRect,
                                     m_TextBrush.Get());
        m_D2DRenderTarget->EndDraw();
    }

    void ClientVideoDecoder::UpdateOverlayStats(const OsmiumClientStats&)
    {
    }
}
