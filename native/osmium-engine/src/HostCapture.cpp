#include "HostCapture.h"
#include "HostSystemAgent.h"

#include <d3dcompiler.h>
#include <algorithm>
#include <cmath>
#include <cstring>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

namespace osmium
{
    using Microsoft::WRL::ComPtr;

    namespace
    {
        const char* kHdrTonemapVsSource = R"(
float4 main(uint vid : SV_VertexID) : SV_POSITION {
    float2 pos[4] = {
        float2(-1.0,  1.0),
        float2( 1.0,  1.0),
        float2(-1.0, -1.0),
        float2( 1.0, -1.0)
    };
    return float4(pos[vid], 0.0, 1.0);
}
)";

        const char* kHdrTonemapPsSource = R"(
cbuffer CbTonemap : register(b0) {
    float sdrScale;
    uint srcMode;
    uint outPq;
    float pad;
};
Texture2D<float4> tex0 : register(t0);

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
    const float knee = 0.95;
    float peak = max(lin.r, max(lin.g, lin.b));
    if (peak <= knee) {
        return lin;
    }
    float over = peak - knee;
    float newPeak = knee + (1.0 - knee) * (over / (over + 0.30));
    return saturate(lin * (newPeak / max(peak, 1e-6)));
}

float3 LinearToSrgb(float3 c) {
    c = saturate(c);
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(max(c, 1e-6), 1.0 / 2.4) - 0.055;
    return lerp(lo, hi, step(0.0031308, c));
}

float4 main(float4 pos : SV_POSITION) : SV_TARGET {
    float3 raw = tex0.Load(int3(int2(pos.xy), 0)).rgb;

    if (outPq != 0) {
        if (srcMode == 1) {
            return float4(saturate(raw), 1.0);
        }
        float3 scrgb = max(raw, 0.0);
        float3 bt2020Lin;
        bt2020Lin.r = dot(scrgb, float3(0.6274040, 0.3292830, 0.0433130));
        bt2020Lin.g = dot(scrgb, float3(0.0690970, 0.9195400, 0.0113620));
        bt2020Lin.b = dot(scrgb, float3(0.0163916, 0.0880132, 0.8955950));
        bt2020Lin = max(bt2020Lin, 0.0);
        return float4(NitsToPq(bt2020Lin * 80.0), 1.0);
    }

    if (srcMode == 2) {
        return float4(saturate(raw), 1.0);
    }

    float scale = max(sdrScale, 1.0);
    float3 lin = float3(0.0, 0.0, 0.0);

    if (srcMode == 1) {
        float3 bt2020 = PqToNits(saturate(raw)) / (scale * 80.0);
        lin.r = dot(bt2020, float3( 1.660491, -0.587641, -0.072850));
        lin.g = dot(bt2020, float3(-0.124550,  1.132900, -0.008349));
        lin.b = dot(bt2020, float3(-0.018151, -0.100579,  1.118730));
        lin = max(lin, 0.0);
    } else {
        lin = max(raw, 0.0) / scale;
    }

    float3 mapped = RollOffHighlights(lin);
    float3 srgb = LinearToSrgb(mapped);
    return float4(srgb, 1.0);
}
)";

        struct CbTonemapData
        {
            float sdrScale;
            uint32_t srcMode;
            uint32_t outPq;
            float pad;
        };

        // Composites any Win32 cursor (opaque, 32-bit alpha, monochrome mask, or XOR I-beam)
        // directly on GPU with zero GPU->CPU readback using the exact dual-background identity:
        //   out = B + dst * (W - B)
        // where B is the cursor drawn on black (0) and W is the cursor drawn on white (1).
        const char* kCursorCompositePsSource = R"(
cbuffer CbCursor : register(b0) {
    int2 cursorOrigin;
    int2 pad;
};
Texture2D<float4> cleanTex : register(t0);
Texture2D<float4> curBlack : register(t1);
Texture2D<float4> curWhite : register(t2);

float4 main(float4 pos : SV_POSITION) : SV_TARGET {
    int2 p = int2(pos.xy);
    int2 c = p - cursorOrigin;
    float3 dst = cleanTex.Load(int3(p, 0)).rgb;
    float3 b = curBlack.Load(int3(c, 0)).rgb;
    float3 w = curWhite.Load(int3(c, 0)).rgb;
    return float4(saturate(b + dst * (w - b)), 1.0);
}
)";

        struct CbCursorData
        {
            int32_t originX;
            int32_t originY;
            int32_t pad0;
            int32_t pad1;
        };

        std::wstring ResolveMonitorDeviceName(const wchar_t* gdiDeviceName)
        {
            if (!gdiDeviceName || !*gdiDeviceName) return L"";

            UINT32 pathCount = 0;
            UINT32 modeCount = 0;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) == ERROR_SUCCESS &&
                pathCount > 0 && modeCount > 0)
            {
                std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
                std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount, modes.data(),
                                       nullptr) == ERROR_SUCCESS)
                {
                    for (UINT32 i = 0; i < pathCount; ++i)
                    {
                        DISPLAYCONFIG_SOURCE_DEVICE_NAME srcName{};
                        srcName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                        srcName.header.size = sizeof(srcName);
                        srcName.header.adapterId = paths[i].sourceInfo.adapterId;
                        srcName.header.id = paths[i].sourceInfo.id;
                        if (DisplayConfigGetDeviceInfo(&srcName.header) != ERROR_SUCCESS ||
                            _wcsicmp(srcName.viewGdiDeviceName, gdiDeviceName) != 0)
                        {
                            continue;
                        }

                        DISPLAYCONFIG_TARGET_DEVICE_NAME tgtName{};
                        tgtName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                        tgtName.header.size = sizeof(tgtName);
                        tgtName.header.adapterId = paths[i].targetInfo.adapterId;
                        tgtName.header.id = paths[i].targetInfo.id;
                        if (DisplayConfigGetDeviceInfo(&tgtName.header) == ERROR_SUCCESS)
                        {
                            std::wstring friendly(tgtName.monitorFriendlyDeviceName);
                            if (!friendly.empty())
                            {
                                return friendly;
                            }
                        }
                    }
                }
            }

            DISPLAY_DEVICEW monDev{};
            monDev.cb = sizeof(monDev);
            if (EnumDisplayDevicesW(gdiDeviceName, 0, &monDev, 0) && monDev.DeviceString[0] != L'\0')
            {
                return monDev.DeviceString;
            }

            const wchar_t* shortName = wcsrchr(gdiDeviceName, L'\\');
            return shortName ? (shortName + 1) : gdiDeviceName;
        }
    }

    HostCapture::~HostCapture()
    {
        Shutdown();
    }

    std::vector<OsmiumDisplayInfo> HostCapture::EnumerateDisplays()
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        std::vector<OsmiumDisplayInfo> result;

        ComPtr<IDXGIFactory1> factory;
        if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))
        {
            return result;
        }

        int globalIndex = 0;
        for (UINT a = 0;; ++a)
        {
            ComPtr<IDXGIAdapter1> adapter;
            if (factory->EnumAdapters1(a, &adapter) == DXGI_ERROR_NOT_FOUND) break;

            for (UINT o = 0;; ++o)
            {
                ComPtr<IDXGIOutput> output;
                if (adapter->EnumOutputs(o, &output) == DXGI_ERROR_NOT_FOUND) break;

                DXGI_OUTPUT_DESC desc{};
                if (FAILED(output->GetDesc(&desc)) || !desc.AttachedToDesktop) continue;

                DEVMODEW dm{};
                dm.dmSize = sizeof(dm);
                int hz = 60;
                if (EnumDisplaySettingsW(desc.DeviceName, ENUM_CURRENT_SETTINGS, &dm) && dm.dmDisplayFrequency > 0)
                {
                    hz = static_cast<int>(dm.dmDisplayFrequency);
                }

                OsmiumDisplayInfo info{};
                info.index = globalIndex++;
                info.left = desc.DesktopCoordinates.left;
                info.top = desc.DesktopCoordinates.top;
                info.width = desc.DesktopCoordinates.right - desc.DesktopCoordinates.left;
                info.height = desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top;
                info.refreshRate = hz;
                info.isPrimary = (info.left == 0 && info.top == 0) ? 1 : 0;

                char utf8Name[128]{};
                WideCharToMultiByte(CP_UTF8, 0, desc.DeviceName, -1, utf8Name, sizeof(utf8Name), nullptr, nullptr);
                snprintf(info.name, sizeof(info.name), "Display %d (%s)", info.index + 1, utf8Name);

                std::wstring friendlyDev = ResolveMonitorDeviceName(desc.DeviceName);
                WideCharToMultiByte(CP_UTF8, 0, friendlyDev.c_str(), -1, info.deviceName, sizeof(info.deviceName),
                                    nullptr, nullptr);

                result.push_back(info);
            }
        }

        return result;
    }

    bool HostCapture::Initialize(int displayIndex, bool captureCursor)
    {
        Shutdown();
        m_DisplayIndex = displayIndex;
        SetCaptureCursor(captureCursor);

        if (InitDuplication())
        {
            OSM_LOG_INFO("HostCapture initialized DXGI Desktop Duplication on display %d (%dx%d)",
                         m_DisplayIndex, m_Width, m_Height);
            return true;
        }

        if (!RefreshMonitorBoundsForGdi())
        {
            m_DesktopRect.left = 0;
            m_DesktopRect.top = 0;
            m_Width = GetSystemMetrics(SM_CXSCREEN);
            m_Height = GetSystemMetrics(SM_CYSCREEN);
            if (m_Width <= 0) m_Width = 1920;
            if (m_Height <= 0) m_Height = 1080;
            m_DesktopRect.right = m_Width;
            m_DesktopRect.bottom = m_Height;
        }
        OSM_LOG_WARN("DXGI Desktop Duplication unavailable; using GDI capture fallback on display %d (%dx%d)",
                     m_DisplayIndex, m_Width, m_Height);
        return true;
    }

    void HostCapture::ReleaseD3DResources()
    {
        m_SrcCopySrv.Reset();
        m_SrcCopyTex.Reset();
        m_SrcCopyFmt = DXGI_FORMAT_UNKNOWN;
        m_SrcCopyW = 0;
        m_SrcCopyH = 0;
        m_TonemapRtv.Reset();
        m_TonemapRtTex.Reset();
        m_TonemapFmt = DXGI_FORMAT_UNKNOWN;
        m_TonemapW = 0;
        m_TonemapH = 0;
        m_TonemapVs.Reset();
        m_TonemapPs.Reset();
        m_TonemapCb.Reset();
        m_OutputIsHdr = false;
        m_OutputColorSpace = DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        m_SdrWhiteScale = 1.0f;
        m_LastWhiteQueryMs = 0;
        m_LastLoggedAcqFmt = DXGI_FORMAT_UNKNOWN;

        if (m_DuplicationFrameHeld)
        {
            m_Duplication->ReleaseFrame();
            m_DuplicationFrameHeld = false;
        }
        m_Duplication.Reset();
        m_StagingTex.Reset();
        m_StagingFmt = DXGI_FORMAT_UNKNOWN;
        m_Nv12 = {};
        m_Context.Reset();
        m_Device.Reset();
    }

    void HostCapture::Shutdown()
    {
        ReleaseD3DResources();
        ReleaseCursorScratch();
        m_LastFramePixels.clear();
        m_LastFrameIsHdr10Pq = false;
        m_HasLastFrame = false;
        m_LastDxgiFrameOrGdiProbeMs = 0;
        m_InitialHdrTimeouts = 0;
        m_ConsecutiveDuplFailures = 0;
        m_GdiFallbackUntilMs = 0;
    }

    bool HostCapture::EnsureCursorScratch()
    {
        if (m_CursorBlackDc && m_CursorBlackBits && m_CursorWhiteDc && m_CursorWhiteBits)
        {
            return true;
        }
        ReleaseCursorScratch();

        m_CursorScreenDc = GetDC(nullptr);
        if (!m_CursorScreenDc) return false;

        BITMAPINFO bmi{};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = kCursorBoxSize;
        bmi.bmiHeader.biHeight = -kCursorBoxSize;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        m_CursorBlackDc = CreateCompatibleDC(m_CursorScreenDc);
        m_CursorWhiteDc = CreateCompatibleDC(m_CursorScreenDc);
        if (!m_CursorBlackDc || !m_CursorWhiteDc)
        {
            ReleaseCursorScratch();
            return false;
        }

        m_CursorBlackBmp = CreateDIBSection(m_CursorScreenDc, &bmi, DIB_RGB_COLORS,
                                            reinterpret_cast<void**>(&m_CursorBlackBits), nullptr, 0);
        m_CursorWhiteBmp = CreateDIBSection(m_CursorScreenDc, &bmi, DIB_RGB_COLORS,
                                            reinterpret_cast<void**>(&m_CursorWhiteBits), nullptr, 0);
        if (!m_CursorBlackBmp || !m_CursorBlackBits || !m_CursorWhiteBmp || !m_CursorWhiteBits)
        {
            ReleaseCursorScratch();
            return false;
        }

        m_CursorBlackOld = SelectObject(m_CursorBlackDc, m_CursorBlackBmp);
        m_CursorWhiteOld = SelectObject(m_CursorWhiteDc, m_CursorWhiteBmp);
        return true;
    }

    void HostCapture::ReleaseCursorScratch()
    {
        if (m_CursorBlackDc && m_CursorBlackOld) SelectObject(m_CursorBlackDc, m_CursorBlackOld);
        if (m_CursorBlackBmp) DeleteObject(m_CursorBlackBmp);
        if (m_CursorBlackDc) DeleteDC(m_CursorBlackDc);
        if (m_CursorWhiteDc && m_CursorWhiteOld) SelectObject(m_CursorWhiteDc, m_CursorWhiteOld);
        if (m_CursorWhiteBmp) DeleteObject(m_CursorWhiteBmp);
        if (m_CursorWhiteDc) DeleteDC(m_CursorWhiteDc);
        if (m_CursorScreenDc) ReleaseDC(nullptr, m_CursorScreenDc);

        m_CursorScreenDc = nullptr;
        m_CursorBlackDc = nullptr;
        m_CursorBlackBmp = nullptr;
        m_CursorBlackOld = nullptr;
        m_CursorBlackBits = nullptr;
        m_CursorWhiteDc = nullptr;
        m_CursorWhiteBmp = nullptr;
        m_CursorWhiteOld = nullptr;
        m_CursorWhiteBits = nullptr;
        m_CursorPrevBlack.clear();
        m_CursorPrevWhite.clear();
    }

    bool HostCapture::RefreshMonitorBoundsForGdi()
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        auto displays = EnumerateDisplays();
        for (const auto& d : displays)
        {
            if (d.index == m_DisplayIndex || (m_DisplayIndex < 0 && d.isPrimary))
            {
                if (d.width > 0 && d.height > 0)
                {
                    m_DesktopRect = {d.left, d.top, d.left + d.width, d.top + d.height};
                    m_Width = d.width;
                    m_Height = d.height;
                    return true;
                }
            }
        }
        if (!displays.empty() && displays[0].width > 0 && displays[0].height > 0)
        {
            const auto& d = displays[0];
            m_DesktopRect = {d.left, d.top, d.left + d.width, d.top + d.height};
            m_Width = d.width;
            m_Height = d.height;
            return true;
        }
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        if (w > 0 && h > 0)
        {
            m_DesktopRect = {0, 0, w, h};
            m_Width = w;
            m_Height = h;
            return true;
        }
        return false;
    }

    void HostCapture::RefreshOutputHdrState()
    {
        uint64_t nowMs = NowMillis();
        if (m_LastWhiteQueryMs != 0 && (nowMs - m_LastWhiteQueryMs) < 2000)
        {
            return;
        }
        m_LastWhiteQueryMs = nowMs;
        QueryOutputHdrStateAndWhite(m_DeviceName.c_str(), m_OutputColorSpace, m_OutputIsHdr, m_SdrWhiteScale);
    }

    void HostCapture::QueryOutputHdrStateAndWhite(
        const wchar_t* gdiDeviceName,
        DXGI_COLOR_SPACE_TYPE dxgiColorSpace,
        bool& outIsHdr,
        float& outSdrScale) const
    {
        bool dxgiHdr = (dxgiColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ||
            dxgiColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709);
        outIsHdr = dxgiHdr;
        outSdrScale = dxgiHdr ? 2.5f : 1.0f;

        if (!gdiDeviceName || !*gdiDeviceName) return;

        UINT32 numPaths = 0, numModes = 0;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &numPaths, &numModes) != ERROR_SUCCESS || numPaths == 0)
        {
            return;
        }

        std::vector<DISPLAYCONFIG_PATH_INFO> paths(numPaths);
        std::vector<DISPLAYCONFIG_MODE_INFO> modes(numModes);
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &numPaths, paths.data(), &numModes, modes.data(), nullptr) !=
            ERROR_SUCCESS)
        {
            return;
        }

        for (UINT32 i = 0; i < numPaths; ++i)
        {
            DISPLAYCONFIG_SOURCE_DEVICE_NAME srcName{};
            srcName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            srcName.header.size = sizeof(srcName);
            srcName.header.adapterId = paths[i].sourceInfo.adapterId;
            srcName.header.id = paths[i].sourceInfo.id;
            if (DisplayConfigGetDeviceInfo(&srcName.header) != ERROR_SUCCESS) continue;

            if (_wcsicmp(srcName.viewGdiDeviceName, gdiDeviceName) == 0)
            {
                DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO advInfo{};
                advInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                advInfo.header.size = sizeof(advInfo);
                advInfo.header.adapterId = paths[i].targetInfo.adapterId;
                advInfo.header.id = paths[i].targetInfo.id;
                if (DisplayConfigGetDeviceInfo(&advInfo.header) == ERROR_SUCCESS)
                {
                    outIsHdr = (advInfo.advancedColorEnabled != 0) && (advInfo.wideColorEnforced == 0);
                }

                DISPLAYCONFIG_SDR_WHITE_LEVEL whiteLevel{};
                whiteLevel.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                whiteLevel.header.size = sizeof(whiteLevel);
                whiteLevel.header.adapterId = paths[i].targetInfo.adapterId;
                whiteLevel.header.id = paths[i].targetInfo.id;
                if (DisplayConfigGetDeviceInfo(&whiteLevel.header) == ERROR_SUCCESS && whiteLevel.SDRWhiteLevel >= 1000)
                {
                    outSdrScale = std::clamp(static_cast<float>(whiteLevel.SDRWhiteLevel) / 1000.0f, 1.0f, 12.5f);
                }
                else if (outIsHdr)
                {
                    outSdrScale = 2.5f;
                }
                else
                {
                    outSdrScale = 1.0f;
                }
                break;
            }
        }
    }

    bool HostCapture::EnsureHdrTonemapPipeline(UINT width, UINT height, DXGI_FORMAT srcFmt, DXGI_FORMAT dstFmt)
    {
        if (!m_Device || !m_Context || width == 0 || height == 0) return false;

        if (!m_TonemapVs || !m_TonemapPs || !m_TonemapCb)
        {
            ComPtr<ID3DBlob> vsBlob, psBlob, errBlob;
            if (FAILED(
                D3DCompile(kHdrTonemapVsSource, strlen(kHdrTonemapVsSource), nullptr, nullptr, nullptr, "main", "vs_5_0"
                    , 0, 0, &vsBlob, &errBlob)))
            {
                OSM_LOG_ERROR("HDR tonemap VS compile failed");
                return false;
            }
            if (FAILED(
                D3DCompile(kHdrTonemapPsSource, strlen(kHdrTonemapPsSource), nullptr, nullptr, nullptr, "main", "ps_5_0"
                    , 0, 0, &psBlob, &errBlob)))
            {
                OSM_LOG_ERROR("HDR tonemap PS compile failed: %s",
                              errBlob ? static_cast<const char*>(errBlob->GetBufferPointer()) : "unknown");
                return false;
            }
            if (FAILED(
                    m_Device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &
                        m_TonemapVs)) ||
                FAILED(
                    m_Device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &
                        m_TonemapPs)))
            {
                return false;
            }

            D3D11_BUFFER_DESC cbd{};
            cbd.ByteWidth = sizeof(CbTonemapData);
            cbd.Usage = D3D11_USAGE_DYNAMIC;
            cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            cbd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            if (FAILED(m_Device->CreateBuffer(&cbd, nullptr, &m_TonemapCb)))
            {
                return false;
            }
        }

        if (!m_SrcCopyTex || m_SrcCopyFmt != srcFmt || m_SrcCopyW != width || m_SrcCopyH != height)
        {
            m_SrcCopySrv.Reset();
            m_SrcCopyTex.Reset();
            D3D11_TEXTURE2D_DESC sd{};
            sd.Width = width;
            sd.Height = height;
            sd.MipLevels = 1;
            sd.ArraySize = 1;
            sd.Format = srcFmt;
            sd.SampleDesc.Count = 1;
            sd.Usage = D3D11_USAGE_DEFAULT;
            sd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            if (FAILED(m_Device->CreateTexture2D(&sd, nullptr, &m_SrcCopyTex)) ||
                FAILED(m_Device->CreateShaderResourceView(m_SrcCopyTex.Get(), nullptr, &m_SrcCopySrv)))
            {
                return false;
            }
            m_SrcCopyFmt = srcFmt;
            m_SrcCopyW = width;
            m_SrcCopyH = height;
        }

        if (!m_TonemapRtTex || !m_TonemapRtv || m_TonemapFmt != dstFmt || m_TonemapW != width || m_TonemapH != height)
        {
            m_TonemapRtv.Reset();
            m_TonemapRtTex.Reset();
            D3D11_TEXTURE2D_DESC rtd{};
            rtd.Width = width;
            rtd.Height = height;
            rtd.MipLevels = 1;
            rtd.ArraySize = 1;
            rtd.Format = dstFmt;
            rtd.SampleDesc.Count = 1;
            rtd.Usage = D3D11_USAGE_DEFAULT;
            rtd.BindFlags = D3D11_BIND_RENDER_TARGET;
            if (FAILED(m_Device->CreateTexture2D(&rtd, nullptr, &m_TonemapRtTex)) ||
                FAILED(m_Device->CreateRenderTargetView(m_TonemapRtTex.Get(), nullptr, &m_TonemapRtv)))
            {
                return false;
            }
            m_TonemapFmt = dstFmt;
            m_TonemapW = width;
            m_TonemapH = height;
        }

        if (!m_StagingTex || m_StagingFmt != dstFmt || m_Width != static_cast<int>(width) || m_Height != static_cast<
            int>(height))
        {
            m_StagingTex.Reset();
            D3D11_TEXTURE2D_DESC stDesc{};
            stDesc.Width = width;
            stDesc.Height = height;
            stDesc.MipLevels = 1;
            stDesc.ArraySize = 1;
            stDesc.Format = dstFmt;
            stDesc.SampleDesc.Count = 1;
            stDesc.Usage = D3D11_USAGE_STAGING;
            stDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            if (FAILED(m_Device->CreateTexture2D(&stDesc, nullptr, &m_StagingTex)))
            {
                m_StagingFmt = DXGI_FORMAT_UNKNOWN;
                return false;
            }
            m_StagingFmt = dstFmt;
            m_Width = static_cast<int>(width);
            m_Height = static_cast<int>(height);
            m_DesktopRect.right = m_DesktopRect.left + m_Width;
            m_DesktopRect.bottom = m_DesktopRect.top + m_Height;
        }

        return true;
    }

    bool HostCapture::ConvertAcquiredTexture(ID3D11Texture2D* srcTex, const D3D11_TEXTURE2D_DESC& srcDesc,
                                             bool outputPqHdr)
    {
        DXGI_FORMAT dstFmt = outputPqHdr ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;
        if (!EnsureHdrTonemapPipeline(srcDesc.Width, srcDesc.Height, srcDesc.Format, dstFmt))
        {
            return false;
        }

        RefreshOutputHdrState();

        m_Context->CopyResource(m_SrcCopyTex.Get(), srcTex);

        uint32_t mode = 0;
        if (srcDesc.Format == DXGI_FORMAT_R10G10B10A2_UNORM)
        {
            mode = (m_OutputColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 || m_OutputIsHdr) ? 1U : 2U;
        }

        D3D11_MAPPED_SUBRESOURCE cbMap{};
        if (SUCCEEDED(m_Context->Map(m_TonemapCb.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &cbMap)))
        {
            auto* cb = static_cast<CbTonemapData*>(cbMap.pData);
            cb->sdrScale = m_SdrWhiteScale;
            cb->srcMode = mode;
            cb->outPq = outputPqHdr ? 1U : 0U;
            cb->pad = 0.0f;
            m_Context->Unmap(m_TonemapCb.Get(), 0);
        }

        m_Context->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
        m_Context->OMSetDepthStencilState(nullptr, 0);
        m_Context->RSSetState(nullptr);

        ID3D11RenderTargetView* rtv = m_TonemapRtv.Get();
        m_Context->OMSetRenderTargets(1, &rtv, nullptr);

        D3D11_VIEWPORT vp{};
        vp.Width = static_cast<float>(srcDesc.Width);
        vp.Height = static_cast<float>(srcDesc.Height);
        vp.MinDepth = 0.0f;
        vp.MaxDepth = 1.0f;
        m_Context->RSSetViewports(1, &vp);

        m_Context->IASetInputLayout(nullptr);
        m_Context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        m_Context->VSSetShader(m_TonemapVs.Get(), nullptr, 0);
        m_Context->PSSetShader(m_TonemapPs.Get(), nullptr, 0);
        ID3D11Buffer* cbPtr = m_TonemapCb.Get();
        m_Context->PSSetConstantBuffers(0, 1, &cbPtr);
        ID3D11ShaderResourceView* srv = m_SrcCopySrv.Get();
        m_Context->PSSetShaderResources(0, 1, &srv);
        m_Context->Draw(4, 0);

        ID3D11ShaderResourceView* nullSrv = nullptr;
        ID3D11RenderTargetView* nullRtv = nullptr;
        m_Context->PSSetShaderResources(0, 1, &nullSrv);
        m_Context->OMSetRenderTargets(1, &nullRtv, nullptr);

        m_Context->CopyResource(m_StagingTex.Get(), m_TonemapRtTex.Get());
        return true;
    }

    bool HostCapture::InitDuplication()
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        ReleaseD3DResources();

        ComPtr<IDXGIFactory1> factory;
        if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return false;

        int curIndex = 0;
        ComPtr<IDXGIAdapter1> targetAdapter;
        ComPtr<IDXGIOutput> targetOutput;
        DXGI_OUTPUT_DESC targetDesc{};

        for (UINT a = 0; !targetOutput; ++a)
        {
            ComPtr<IDXGIAdapter1> adapter;
            if (factory->EnumAdapters1(a, &adapter) == DXGI_ERROR_NOT_FOUND) break;

            for (UINT o = 0;; ++o)
            {
                ComPtr<IDXGIOutput> output;
                if (adapter->EnumOutputs(o, &output) == DXGI_ERROR_NOT_FOUND) break;

                DXGI_OUTPUT_DESC desc{};
                if (FAILED(output->GetDesc(&desc)) || !desc.AttachedToDesktop) continue;

                if (curIndex == m_DisplayIndex || (m_DisplayIndex < 0 && desc.DesktopCoordinates.left == 0 && desc.
                    DesktopCoordinates.top == 0))
                {
                    targetAdapter = adapter;
                    targetOutput = output;
                    targetDesc = desc;
                    break;
                }
                curIndex++;
            }
        }

        if (!targetOutput) return false;

        D3D_FEATURE_LEVEL fl;
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        HRESULT hr = D3D11CreateDevice(
            targetAdapter.Get(),
            D3D_DRIVER_TYPE_UNKNOWN,
            nullptr,
            flags,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &m_Device,
            &fl,
            &m_Context);
        if (FAILED(hr)) return false;

        m_DeviceName = targetDesc.DeviceName;
        m_OutputColorSpace = DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        ComPtr<IDXGIOutput6> output6;
        if (SUCCEEDED(targetOutput.As(&output6)) && output6)
        {
            DXGI_OUTPUT_DESC1 desc1{};
            if (SUCCEEDED(output6->GetDesc1(&desc1)))
            {
                m_OutputColorSpace = desc1.ColorSpace;
            }
        }
        QueryOutputHdrStateAndWhite(m_DeviceName.c_str(), m_OutputColorSpace, m_OutputIsHdr, m_SdrWhiteScale);
        m_LastWhiteQueryMs = NowMillis();

        const DXGI_FORMAT kHdrCaptureFormats[] = {
            DXGI_FORMAT_R16G16B16A16_FLOAT,
            DXGI_FORMAT_R10G10B10A2_UNORM,
            DXGI_FORMAT_B8G8R8A8_UNORM
        };
        const DXGI_FORMAT kSdrCaptureFormats[] = {
            DXGI_FORMAT_B8G8R8A8_UNORM
        };
        ComPtr<IDXGIOutput5> output5;
        if (SUCCEEDED(targetOutput.As(&output5)) && output5)
        {
            if (m_OutputIsHdr)
            {
                hr = output5->DuplicateOutput1(m_Device.Get(), 0, ARRAYSIZE(kHdrCaptureFormats), kHdrCaptureFormats,
                                               &m_Duplication);
            }
            else
            {
                hr = output5->DuplicateOutput1(m_Device.Get(), 0, ARRAYSIZE(kSdrCaptureFormats), kSdrCaptureFormats,
                                               &m_Duplication);
            }
        }
        else
        {
            hr = E_NOINTERFACE;
        }
        if (FAILED(hr))
        {
            OSM_LOG_WARN("DuplicateOutput1 failed (hr=0x%08X), falling back to DuplicateOutput", hr);
            ComPtr<IDXGIOutput1> output1;
            if (FAILED(targetOutput.As(&output1)))
            {
                ReleaseD3DResources();
                return false;
            }
            hr = output1->DuplicateOutput(m_Device.Get(), &m_Duplication);
        }
        if (FAILED(hr))
        {
            ReleaseD3DResources();
            return false;
        }

        m_DesktopRect = targetDesc.DesktopCoordinates;
        m_Width = std::max<int>(1, m_DesktopRect.right - m_DesktopRect.left);
        m_Height = std::max<int>(1, m_DesktopRect.bottom - m_DesktopRect.top);

        D3D11_TEXTURE2D_DESC texDesc{};
        texDesc.Width = static_cast<UINT>(m_Width);
        texDesc.Height = static_cast<UINT>(m_Height);
        texDesc.MipLevels = 1;
        texDesc.ArraySize = 1;
        texDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        texDesc.SampleDesc.Count = 1;
        texDesc.Usage = D3D11_USAGE_STAGING;
        texDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

        if (FAILED(m_Device->CreateTexture2D(&texDesc, nullptr, &m_StagingTex)))
        {
            ReleaseD3DResources();
            return false;
        }

        m_StagingFmt = DXGI_FORMAT_B8G8R8A8_UNORM;
        m_LastFrameIsHdr10Pq = false;
        m_HasLastFrame = false;
        m_LastDxgiFrameOrGdiProbeMs = 0;
        m_InitialHdrTimeouts = 0;
        OSM_LOG_INFO("HostCapture DXGI duplication ready (%dx%d, cs=%d, hdr=%d, sdrWhite=%.2fx)",
                     m_Width, m_Height, static_cast<int>(m_OutputColorSpace), m_OutputIsHdr ? 1 : 0, m_SdrWhiteScale);
        return true;
    }

    bool HostCapture::PrepareNv12Conversion(int sourceWidth, int sourceHeight)
    {
        auto& nv12 = m_Nv12;
        if (nv12.sourceWidth == sourceWidth && nv12.sourceHeight == sourceHeight &&
            nv12.outputWidth == m_Nv12Width && nv12.outputHeight == m_Nv12Height)
        {
            return nv12.processor != nullptr;
        }

        if (m_LastFrameInGpu) m_HasLastFrame = false;
        nv12 = {};
        nv12.sourceWidth = sourceWidth;
        nv12.sourceHeight = sourceHeight;
        nv12.outputWidth = m_Nv12Width;
        nv12.outputHeight = m_Nv12Height;

        ComPtr<ID3D11VideoDevice> videoDevice;
        ComPtr<ID3D11VideoProcessorEnumerator> formats;
        ComPtr<ID3D11VideoProcessorEnumerator1> conversions;
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputFrameRate = {60, 1};
        content.OutputFrameRate = content.InputFrameRate;
        content.InputWidth = sourceWidth;
        content.InputHeight = sourceHeight;
        content.OutputWidth = nv12.outputWidth;
        content.OutputHeight = nv12.outputHeight;
        content.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
        constexpr auto inputColor = DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709;
        constexpr auto outputColor = DXGI_COLOR_SPACE_YCBCR_STUDIO_G22_LEFT_P709;
        BOOL supportsConversion = FALSE;
        if (FAILED(m_Device.As(&videoDevice)) || FAILED(m_Context.As(&nv12.context)) ||
            FAILED(videoDevice->CreateVideoProcessorEnumerator(&content, &formats)) ||
            FAILED(formats.As(&conversions)) ||
            FAILED(conversions->CheckVideoProcessorFormatConversion(
                DXGI_FORMAT_B8G8R8A8_UNORM, inputColor, DXGI_FORMAT_NV12, outputColor, &supportsConversion)) ||
            !supportsConversion)
        {
            return false;
        }

        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = sourceWidth;
        desc.Height = sourceHeight;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET;

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inView{};
        inView.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outView{};
        outView.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;

        bool inputSupportsRtv = false;
        if (SUCCEEDED(m_Device->CreateTexture2D(&desc, nullptr, &nv12.input)) &&
            SUCCEEDED(m_Device->CreateRenderTargetView(nv12.input.Get(), nullptr, &nv12.inputRtv)) &&
            SUCCEEDED(videoDevice->CreateVideoProcessorInputView(nv12.input.Get(), formats.Get(), &inView, &nv12.inputView)))
        {
            inputSupportsRtv = true;
        }
        else
        {
            nv12.inputView.Reset();
            nv12.inputRtv.Reset();
            nv12.input.Reset();
            desc.BindFlags = 0;
            if (FAILED(m_Device->CreateTexture2D(&desc, nullptr, &nv12.input)) ||
                FAILED(videoDevice->CreateVideoProcessorInputView(nv12.input.Get(), formats.Get(), &inView, &nv12.inputView)))
            {
                return false;
            }
        }

        if (inputSupportsRtv)
        {
            D3D11_TEXTURE2D_DESC cleanDesc = desc;
            cleanDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            if (SUCCEEDED(m_Device->CreateTexture2D(&cleanDesc, nullptr, &nv12.cleanInput)) &&
                SUCCEEDED(m_Device->CreateShaderResourceView(nv12.cleanInput.Get(), nullptr, &nv12.cleanInputSrv)) &&
                EnsureGpuCursorPipeline())
            {
                nv12.gpuCursorSupported = true;
            }
            else
            {
                nv12.cleanInputSrv.Reset();
                nv12.cleanInput.Reset();
                nv12.gpuCursorSupported = false;
            }
        }

        desc.Width = nv12.outputWidth;
        desc.Height = nv12.outputHeight;
        desc.Format = DXGI_FORMAT_NV12;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (FAILED(m_Device->CreateTexture2D(&desc, nullptr, &nv12.output))) return false;
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(m_Device->CreateTexture2D(&desc, nullptr, &nv12.readback))) return false;

        if (FAILED(videoDevice->CreateVideoProcessorOutputView(nv12.output.Get(), formats.Get(), &outView, &nv12.outputView)) ||
            FAILED(videoDevice->CreateVideoProcessor(formats.Get(), 0, &nv12.processor)))
        {
            return false;
        }

        auto* processor = nv12.processor.Get();
        const RECT srcRect{0, 0, sourceWidth, sourceHeight};
        const RECT dstRect{0, 0, nv12.outputWidth, nv12.outputHeight};
        nv12.context->VideoProcessorSetStreamFrameFormat(processor, 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        nv12.context->VideoProcessorSetStreamAutoProcessingMode(processor, 0, FALSE);
        nv12.context->VideoProcessorSetStreamSourceRect(processor, 0, TRUE, &srcRect);
        nv12.context->VideoProcessorSetStreamDestRect(processor, 0, TRUE, &dstRect);
        nv12.context->VideoProcessorSetOutputTargetRect(processor, TRUE, &dstRect);
        nv12.context->VideoProcessorSetStreamColorSpace1(processor, 0, inputColor);
        nv12.context->VideoProcessorSetOutputColorSpace1(processor, outputColor);
        OSM_LOG_INFO("HostCapture GPU NV12 conversion ready (%dx%d -> %dx%d, gpuCursor=%d)",
                     sourceWidth, sourceHeight, nv12.outputWidth, nv12.outputHeight,
                     nv12.gpuCursorSupported ? 1 : 0);
        return true;
    }

    bool HostCapture::EnsureGpuCursorPipeline()
    {
        auto& nv12 = m_Nv12;
        if (!m_Device || !m_Context) return false;
        if (nv12.cursorVs && nv12.cursorPs && nv12.cursorCb &&
            nv12.cursorBlackTex && nv12.cursorBlackSrv &&
            nv12.cursorWhiteTex && nv12.cursorWhiteSrv)
        {
            return true;
        }

        ComPtr<ID3DBlob> vsBlob, psBlob, errBlob;
        if (FAILED(D3DCompile(kHdrTonemapVsSource, strlen(kHdrTonemapVsSource), nullptr, nullptr, nullptr,
                              "main", "vs_5_0", 0, 0, &vsBlob, &errBlob)) ||
            FAILED(D3DCompile(kCursorCompositePsSource, strlen(kCursorCompositePsSource), nullptr, nullptr, nullptr,
                              "main", "ps_5_0", 0, 0, &psBlob, &errBlob)))
        {
            OSM_LOG_WARN("HostCapture GPU cursor shader compile failed: %s",
                         errBlob ? static_cast<const char*>(errBlob->GetBufferPointer()) : "unknown");
            return false;
        }
        if (FAILED(m_Device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &nv12.cursorVs)) ||
            FAILED(m_Device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &nv12.cursorPs)))
        {
            return false;
        }

        D3D11_BUFFER_DESC cbd{};
        cbd.ByteWidth = sizeof(CbCursorData);
        cbd.Usage = D3D11_USAGE_DYNAMIC;
        cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cbd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(m_Device->CreateBuffer(&cbd, nullptr, &nv12.cursorCb))) return false;

        D3D11_TEXTURE2D_DESC ctd{};
        ctd.Width = kCursorBoxSize;
        ctd.Height = kCursorBoxSize;
        ctd.MipLevels = 1;
        ctd.ArraySize = 1;
        ctd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        ctd.SampleDesc.Count = 1;
        ctd.Usage = D3D11_USAGE_DEFAULT;
        ctd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(m_Device->CreateTexture2D(&ctd, nullptr, &nv12.cursorBlackTex)) ||
            FAILED(m_Device->CreateShaderResourceView(nv12.cursorBlackTex.Get(), nullptr, &nv12.cursorBlackSrv)) ||
            FAILED(m_Device->CreateTexture2D(&ctd, nullptr, &nv12.cursorWhiteTex)) ||
            FAILED(m_Device->CreateShaderResourceView(nv12.cursorWhiteTex.Get(), nullptr, &nv12.cursorWhiteSrv)))
        {
            return false;
        }
        return true;
    }

    bool HostCapture::DrawGpuCursorOntoInput(bool freshFrame, bool& outChanged)
    {
        outChanged = false;
        auto& nv12 = m_Nv12;
        if (!nv12.gpuCursorSupported || !nv12.hasCleanInput || !nv12.inputRtv || !nv12.cleanInputSrv)
        {
            return false;
        }

        auto restoreOldBox = [&]()
        {
            if (!freshFrame && nv12.hasDrawnCursor &&
                nv12.lastBoxRight > nv12.lastBoxLeft && nv12.lastBoxBottom > nv12.lastBoxTop)
            {
                D3D11_BOX oldBox{};
                oldBox.left = static_cast<UINT>(nv12.lastBoxLeft);
                oldBox.top = static_cast<UINT>(nv12.lastBoxTop);
                oldBox.front = 0;
                oldBox.right = static_cast<UINT>(nv12.lastBoxRight);
                oldBox.bottom = static_cast<UINT>(nv12.lastBoxBottom);
                oldBox.back = 1;
                m_Context->CopySubresourceRegion(
                    nv12.input.Get(), 0, oldBox.left, oldBox.top, 0,
                    nv12.cleanInput.Get(), 0, &oldBox);
                nv12.hasDrawnCursor = false;
            }
        };

        CURSORINFO ci{};
        ci.cbSize = sizeof(ci);
        const bool cursorShowing = GetCursorInfo(&ci) && ((ci.flags & CURSOR_SHOWING) != 0) && (ci.hCursor != nullptr);
        if (!cursorShowing)
        {
            if (!freshFrame && nv12.hasDrawnCursor)
            {
                restoreOldBox();
                outChanged = true;
            }
            return true;
        }

        const int cx = ci.ptScreenPos.x - m_DesktopRect.left;
        const int cy = ci.ptScreenPos.y - m_DesktopRect.top;
        if (cx < -kCursorBoxSize || cy < -kCursorBoxSize ||
            cx >= nv12.sourceWidth + kCursorBoxSize || cy >= nv12.sourceHeight + kCursorBoxSize)
        {
            if (!freshFrame && nv12.hasDrawnCursor)
            {
                restoreOldBox();
                outChanged = true;
            }
            return true;
        }

        const uint64_t nowMs = NowMillis();
        bool shapeUpdated = false;
        const bool handleChanged = (ci.hCursor != nv12.uploadedCursorHandle);
        const bool animCheckDue = (!handleChanged && (nowMs - nv12.uploadedCursorMs) >= 120ULL);
        if (handleChanged || animCheckDue)
        {
            if (!EnsureCursorScratch()) return false;
            if (handleChanged)
            {
                ICONINFO ii{};
                if (!GetIconInfo(ci.hCursor, &ii))
                {
                    if (!freshFrame && nv12.hasDrawnCursor)
                    {
                        restoreOldBox();
                        outChanged = true;
                    }
                    return true;
                }
                nv12.uploadedHotX = static_cast<int>(ii.xHotspot);
                nv12.uploadedHotY = static_cast<int>(ii.yHotspot);
                if (ii.hbmMask) DeleteObject(ii.hbmMask);
                if (ii.hbmColor) DeleteObject(ii.hbmColor);
            }

            constexpr size_t kBoxPixels = static_cast<size_t>(kCursorBoxSize) * static_cast<size_t>(kCursorBoxSize);
            constexpr size_t kBoxBytes = kBoxPixels * 4;
            std::memset(m_CursorBlackBits, 0x00, kBoxBytes);
            std::memset(m_CursorWhiteBits, 0xFF, kBoxBytes);
            DrawIconEx(m_CursorBlackDc, 0, 0, ci.hCursor, 0, 0, 0, nullptr, DI_NORMAL);
            DrawIconEx(m_CursorWhiteDc, 0, 0, ci.hCursor, 0, 0, 0, nullptr, DI_NORMAL);
            GdiFlush();

            const bool pixelsChanged = handleChanged ||
                m_CursorPrevBlack.size() != kBoxPixels ||
                m_CursorPrevWhite.size() != kBoxPixels ||
                std::memcmp(m_CursorPrevBlack.data(), m_CursorBlackBits, kBoxBytes) != 0 ||
                std::memcmp(m_CursorPrevWhite.data(), m_CursorWhiteBits, kBoxBytes) != 0;

            nv12.uploadedCursorHandle = ci.hCursor;
            nv12.uploadedCursorMs = nowMs;
            if (pixelsChanged)
            {
                m_CursorPrevBlack.assign(m_CursorBlackBits, m_CursorBlackBits + kBoxPixels);
                m_CursorPrevWhite.assign(m_CursorWhiteBits, m_CursorWhiteBits + kBoxPixels);
                m_Context->UpdateSubresource(nv12.cursorBlackTex.Get(), 0, nullptr, m_CursorBlackBits, kCursorBoxSize * 4, 0);
                m_Context->UpdateSubresource(nv12.cursorWhiteTex.Get(), 0, nullptr, m_CursorWhiteBits, kCursorBoxSize * 4, 0);
                shapeUpdated = true;
            }
        }

        const int drawX = cx - nv12.uploadedHotX;
        const int drawY = cy - nv12.uploadedHotY;
        const int x0 = std::clamp(drawX, 0, nv12.sourceWidth);
        const int y0 = std::clamp(drawY, 0, nv12.sourceHeight);
        const int x1 = std::clamp(drawX + kCursorBoxSize, 0, nv12.sourceWidth);
        const int y1 = std::clamp(drawY + kCursorBoxSize, 0, nv12.sourceHeight);
        const bool inBounds = (x1 > x0 && y1 > y0);

        if (!freshFrame)
        {
            if (!inBounds)
            {
                if (nv12.hasDrawnCursor)
                {
                    restoreOldBox();
                    outChanged = true;
                }
                return true;
            }
            if (nv12.hasDrawnCursor && !shapeUpdated &&
                drawX == nv12.lastCursorX && drawY == nv12.lastCursorY)
            {
                return true;
            }
        }

        restoreOldBox();
        if (!inBounds) return true;

        D3D11_MAPPED_SUBRESOURCE cbMap{};
        if (SUCCEEDED(m_Context->Map(nv12.cursorCb.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &cbMap)))
        {
            auto* cb = static_cast<CbCursorData*>(cbMap.pData);
            cb->originX = drawX;
            cb->originY = drawY;
            cb->pad0 = 0;
            cb->pad1 = 0;
            m_Context->Unmap(nv12.cursorCb.Get(), 0);
        }

        m_Context->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
        m_Context->OMSetDepthStencilState(nullptr, 0);
        m_Context->RSSetState(nullptr);

        ID3D11RenderTargetView* rtv = nv12.inputRtv.Get();
        m_Context->OMSetRenderTargets(1, &rtv, nullptr);

        D3D11_VIEWPORT vp{};
        vp.TopLeftX = static_cast<float>(x0);
        vp.TopLeftY = static_cast<float>(y0);
        vp.Width = static_cast<float>(x1 - x0);
        vp.Height = static_cast<float>(y1 - y0);
        vp.MinDepth = 0.0f;
        vp.MaxDepth = 1.0f;
        m_Context->RSSetViewports(1, &vp);

        m_Context->IASetInputLayout(nullptr);
        m_Context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        m_Context->VSSetShader(nv12.cursorVs.Get(), nullptr, 0);
        m_Context->PSSetShader(nv12.cursorPs.Get(), nullptr, 0);
        ID3D11Buffer* cbPtr = nv12.cursorCb.Get();
        m_Context->PSSetConstantBuffers(0, 1, &cbPtr);
        ID3D11ShaderResourceView* srvs[3] = {
            nv12.cleanInputSrv.Get(), nv12.cursorBlackSrv.Get(), nv12.cursorWhiteSrv.Get()
        };
        m_Context->PSSetShaderResources(0, 3, srvs);
        m_Context->Draw(4, 0);

        ID3D11ShaderResourceView* nullSrvs[3] = {nullptr, nullptr, nullptr};
        ID3D11RenderTargetView* nullRtv = nullptr;
        m_Context->PSSetShaderResources(0, 3, nullSrvs);
        m_Context->OMSetRenderTargets(1, &nullRtv, nullptr);

        nv12.hasDrawnCursor = true;
        nv12.lastCursorX = drawX;
        nv12.lastCursorY = drawY;
        nv12.lastBoxLeft = x0;
        nv12.lastBoxTop = y0;
        nv12.lastBoxRight = x1;
        nv12.lastBoxBottom = y1;
        outChanged = true;
        return true;
    }

    bool HostCapture::RefreshCachedNv12Cursor(bool keepOnGpu, bool captureCursor)
    {
        auto& nv12 = m_Nv12;
        if (!nv12.processor || !nv12.gpuCursorSupported || !nv12.hasCleanInput) return false;

        bool changed = false;
        if (captureCursor)
        {
            if (!DrawGpuCursorOntoInput(false, changed)) return false;
        }
        else if (nv12.hasDrawnCursor &&
                 nv12.lastBoxRight > nv12.lastBoxLeft && nv12.lastBoxBottom > nv12.lastBoxTop)
        {
            D3D11_BOX oldBox{};
            oldBox.left = static_cast<UINT>(nv12.lastBoxLeft);
            oldBox.top = static_cast<UINT>(nv12.lastBoxTop);
            oldBox.front = 0;
            oldBox.right = static_cast<UINT>(nv12.lastBoxRight);
            oldBox.bottom = static_cast<UINT>(nv12.lastBoxBottom);
            oldBox.back = 1;
            m_Context->CopySubresourceRegion(
                nv12.input.Get(), 0, oldBox.left, oldBox.top, 0,
                nv12.cleanInput.Get(), 0, &oldBox);
            nv12.hasDrawnCursor = false;
            changed = true;
        }

        if (!changed) return true;

        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = nv12.inputView.Get();
        if (FAILED(nv12.context->VideoProcessorBlt(nv12.processor.Get(), nv12.outputView.Get(), 0, 1, &stream)))
        {
            return false;
        }
        if (!keepOnGpu)
        {
            m_Context->CopyResource(nv12.readback.Get(), nv12.output.Get());
            if (!ReadStagingPixels(nv12.readback.Get(), nv12.outputWidth,
                                   static_cast<size_t>(nv12.outputHeight) * 3 / 2))
            {
                return false;
            }
            ++m_LastFrameRevision;
        }
        return true;
    }

    bool HostCapture::CaptureNv12(ID3D11Texture2D* source, bool keepOnGpu, bool captureCursor)
    {
        if (!PrepareNv12Conversion(m_Width, m_Height)) return false;
        auto& nv12 = m_Nv12;
        if (captureCursor && !nv12.gpuCursorSupported) return false;

        m_Context->CopyResource(nv12.input.Get(), source);
        if (captureCursor)
        {
            m_Context->CopyResource(nv12.cleanInput.Get(), source);
            nv12.hasCleanInput = true;
            nv12.hasDrawnCursor = false;
            bool cursorChanged = false;
            if (!DrawGpuCursorOntoInput(true, cursorChanged))
            {
                return false;
            }
        }
        else
        {
            nv12.hasCleanInput = false;
            nv12.hasDrawnCursor = false;
        }

        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = nv12.inputView.Get();
        const HRESULT hr = nv12.context->VideoProcessorBlt(nv12.processor.Get(), nv12.outputView.Get(), 0, 1, &stream);
        if (SUCCEEDED(hr))
        {
            if (keepOnGpu) return true;
            m_Context->CopyResource(nv12.readback.Get(), nv12.output.Get());
            if (ReadStagingPixels(nv12.readback.Get(), nv12.outputWidth,
                                  static_cast<size_t>(nv12.outputHeight) * 3 / 2))
            {
                return true;
            }
        }
        OSM_LOG_WARN("HostCapture GPU NV12 readback failed; using BGRA capture");
        if (m_LastFrameInGpu) m_HasLastFrame = false;
        nv12.processor.Reset();
        return false;
    }

    bool HostCapture::ReadStagingPixels(ID3D11Texture2D* texture, size_t rowBytes, size_t rows)
    {
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(m_Context->Map(texture, 0, D3D11_MAP_READ, 0, &mapped))) return false;
        const auto* source = static_cast<const uint8_t*>(mapped.pData);
        m_LastFramePixels.resize(rowBytes * rows);
        if (mapped.RowPitch == rowBytes)
        {
            std::memcpy(m_LastFramePixels.data(), source, m_LastFramePixels.size());
        }
        else
        {
            for (size_t y = 0; y < rows; ++y)
            {
                std::memcpy(m_LastFramePixels.data() + y * rowBytes, source + y * mapped.RowPitch, rowBytes);
            }
        }
        m_Context->Unmap(texture, 0);
        return true;
    }

    bool HostCapture::CaptureNextFrame(CapturedFrame& outFrame, int initialWaitMs, bool preferPqHdr,
                                       const std::atomic<bool>* privacyScreenActive)
    {
        const bool captureCursor = m_CaptureCursor.load(std::memory_order_relaxed);
        const auto privacyActive = [&] { return privacyScreenActive && privacyScreenActive->load(); };
        const auto captureGdi = [&](bool refreshBounds)
        {
            // Only the duplication path is used while the excluded overlay is visible.
            if (privacyActive()) return false;
            if (refreshBounds) RefreshMonitorBoundsForGdi();
            return CaptureViaGdi(outFrame, captureCursor) && !privacyActive();
        };
        outFrame.captureTimeUs = NowMicros();
        outFrame.isHdr10Pq = false;
        outFrame.isNv12 = false;
        outFrame.nv12Texture.Reset();
        if (preferPqHdr || m_OutputIsHdr)
        {
            RefreshOutputHdrState();
        }

        const uint64_t nowMs = NowMillis();
        if (m_GdiFallbackUntilMs != 0 && nowMs < m_GdiFallbackUntilMs)
        {
            return captureGdi(true);
        }

        if (!m_Duplication)
        {
            if (nowMs - m_LastDuplRetryMs >= 250)
            {
                m_LastDuplRetryMs = nowMs;
                InitDuplication();
            }
            if (!m_Duplication)
            {
                return captureGdi(true);
            }
        }

        const bool wantPq = preferPqHdr && m_OutputIsHdr;
        const bool wantNv12 = !m_OutputIsHdr &&
            m_Nv12Width > 0 && m_Nv12Height > 0 && (m_Nv12Width % 2 == 0) && (m_Nv12Height % 2 == 0);
        const bool wantGpuFrame = wantNv12 && m_EncoderDevice && m_EncoderDevice == m_Device.Get();
        const bool hasUsableCache = m_HasLastFrame && m_LastFrameIsHdr10Pq == wantPq &&
            (!m_LastFrameInGpu || wantGpuFrame) &&
            (!m_LastFrameIsNv12 || (wantNv12 && m_LastFrameWidth == m_Nv12Width && m_LastFrameHeight == m_Nv12Height &&
                                    (!captureCursor || m_Nv12.hasCleanInput)));
        auto copyCachedFrame = [&](bool updated)
        {
            if (m_LastFrameIsNv12 && !updated)
            {
                RefreshCachedNv12Cursor(m_LastFrameInGpu, captureCursor);
            }
            const uint64_t revision = (captureCursor && !m_LastFrameIsNv12) ? 0 : m_LastFrameRevision;
            if (m_LastFrameInGpu)
            {
                outFrame.nv12Texture = m_Nv12.output;
            }
            else if (revision == 0 || revision != outFrame.pixelRevision)
            {
                outFrame.pixels = m_LastFramePixels;
            }
            outFrame.width = m_LastFrameWidth;
            outFrame.height = m_LastFrameHeight;
            outFrame.stride = m_LastFrameIsNv12 ? m_LastFrameWidth : m_LastFrameWidth * 4;
            outFrame.pixelRevision = revision;
            outFrame.updated = updated;
            outFrame.isHdr10Pq = m_LastFrameIsHdr10Pq;
            outFrame.isNv12 = m_LastFrameIsNv12;
            if (captureCursor && !m_LastFrameIsNv12) OverlayCursor(outFrame);
            return true;
        };

        // The host loop already paces frame sampling. Reuse the cached desktop if no update is ready.
        const int minWaitMs = !m_HasLastFrame && m_OutputIsHdr ? 100 : 0;
        const UINT effectiveTimeout = hasUsableCache ? 0 : static_cast<UINT>(std::max(initialWaitMs, minWaitMs));

        DXGI_OUTDUPL_FRAME_INFO frameInfo{};
        ComPtr<IDXGIResource> desktopRes;
        // Let DXGI coalesce desktop updates while encoding and pacing the last frame.
        if (m_DuplicationFrameHeld)
        {
            m_Duplication->ReleaseFrame();
            m_DuplicationFrameHeld = false;
        }
        HRESULT hr = m_Duplication->AcquireNextFrame(effectiveTimeout, &frameInfo, &desktopRes);

        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
        {
            if (hasUsableCache)
            {
                // Keep the SDR fallback check independent of the requested frame rate,
                // throttled to 2s so static desktop intervals do not trigger 20-40ms GDI hitches.
                if (!wantPq && nowMs - m_LastDxgiFrameOrGdiProbeMs >= 2000)
                {
                    m_LastDxgiFrameOrGdiProbeMs = nowMs;
                    if (captureGdi(false))
                    {
                        if (!captureCursor && !m_LastFrameIsNv12 &&
                            outFrame.width == m_LastFrameWidth && outFrame.height == m_LastFrameHeight)
                        {
                            m_LastFramePixels = outFrame.pixels;
                            outFrame.pixelRevision = ++m_LastFrameRevision;
                        }
                        return true;
                    }
                }
                return copyCachedFrame(false);
            }
            if (m_HasLastFrame && !m_LastFrameIsNv12)
            {
                return false;
            }
            if (m_OutputIsHdr && ++m_InitialHdrTimeouts < 8)
            {
                return false;
            }
            return captureGdi(true);
        }

        if (FAILED(hr))
        {
            m_ConsecutiveDuplFailures++;
            OSM_LOG_WARN("AcquireNextFrame failed (hr=0x%08X, count=%u); reinitializing duplication",
                         hr, m_ConsecutiveDuplFailures);
            m_LastDuplRetryMs = NowMillis();
            if (m_ConsecutiveDuplFailures >= 3)
            {
                OSM_LOG_WARN("DXGI Desktop Duplication repeatedly failed; falling back to GDI for 5s");
                ReleaseD3DResources();
                m_GdiFallbackUntilMs = m_LastDuplRetryMs + 5000;
                m_ConsecutiveDuplFailures = 0;
                return captureGdi(true);
            }
            InitDuplication();
            if (!m_Duplication || !m_HasLastFrame)
            {
                return captureGdi(true);
            }
            return false;
        }

        m_DuplicationFrameHeld = true;
        m_LastDxgiFrameOrGdiProbeMs = NowMillis();
        m_InitialHdrTimeouts = 0;

        // A pointer-only update can reuse the desktop unless a previous readback failed.
        if (frameInfo.LastPresentTime.QuadPart == 0 && hasUsableCache && m_ConsecutiveDuplFailures == 0)
        {
            return copyCachedFrame(false);
        }
        if (frameInfo.LastPresentTime.QuadPart == 0 && !m_HasLastFrame)
        {
            return captureGdi(true);
        }

        bool mapSucceeded = false;
        ComPtr<ID3D11Texture2D> acquiredTex;
        if (SUCCEEDED(desktopRes.As(&acquiredTex)))
        {
            D3D11_TEXTURE2D_DESC acqDesc{};
            acquiredTex->GetDesc(&acqDesc);

            if (acqDesc.Format != m_LastLoggedAcqFmt)
            {
                m_LastLoggedAcqFmt = acqDesc.Format;
                m_LastWhiteQueryMs = 0;
                RefreshOutputHdrState();
                OSM_LOG_INFO("HostCapture acquired frame format=%u (%ux%u, hdr=%d, sdrWhite=%.2fx)",
                             static_cast<unsigned>(acqDesc.Format), acqDesc.Width, acqDesc.Height,
                             m_OutputIsHdr ? 1 : 0, m_SdrWhiteScale);
            }

            if (acqDesc.Width > 0 && acqDesc.Height > 0 &&
                (acqDesc.Width != static_cast<UINT>(m_Width) || acqDesc.Height != static_cast<UINT>(m_Height)))
            {
                m_Width = static_cast<int>(acqDesc.Width);
                m_Height = static_cast<int>(acqDesc.Height);
                m_DesktopRect.right = m_DesktopRect.left + m_Width;
                m_DesktopRect.bottom = m_DesktopRect.top + m_Height;
                m_StagingTex.Reset();
                m_StagingFmt = DXGI_FORMAT_UNKNOWN;
            }

            const bool outputNv12 = wantNv12 && acqDesc.Format == DXGI_FORMAT_B8G8R8A8_UNORM &&
                CaptureNv12(acquiredTex.Get(), wantGpuFrame, captureCursor);
            bool copied = false;
            bool outputPq = false;
            if (outputNv12)
            {
                mapSucceeded = true;
            }
            else if (acqDesc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT ||
                acqDesc.Format == DXGI_FORMAT_R10G10B10A2_UNORM)
            {
                outputPq = preferPqHdr && m_OutputIsHdr;
                copied = ConvertAcquiredTexture(acquiredTex.Get(), acqDesc, outputPq);
            }
            else if (acqDesc.Format == DXGI_FORMAT_B8G8R8A8_UNORM)
            {
                if (!m_StagingTex || m_StagingFmt != DXGI_FORMAT_B8G8R8A8_UNORM)
                {
                    m_StagingTex.Reset();
                    D3D11_TEXTURE2D_DESC stDesc{};
                    stDesc.Width = acqDesc.Width;
                    stDesc.Height = acqDesc.Height;
                    stDesc.MipLevels = 1;
                    stDesc.ArraySize = 1;
                    stDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                    stDesc.SampleDesc.Count = 1;
                    stDesc.Usage = D3D11_USAGE_STAGING;
                    stDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                    if (SUCCEEDED(m_Device->CreateTexture2D(&stDesc, nullptr, &m_StagingTex)))
                    {
                        m_StagingFmt = DXGI_FORMAT_B8G8R8A8_UNORM;
                    }
                }
                if (m_StagingTex && m_StagingFmt == acqDesc.Format)
                {
                    m_Context->CopyResource(m_StagingTex.Get(), acquiredTex.Get());
                    copied = true;
                }
                outputPq = false;
            }

            if (copied)
            {
                mapSucceeded = ReadStagingPixels(m_StagingTex.Get(), static_cast<size_t>(m_Width) * 4, m_Height);
            }
            if (mapSucceeded)
            {
                ++m_LastFrameRevision;
                m_LastFrameIsHdr10Pq = outputPq;
                m_LastFrameIsNv12 = outputNv12;
                m_LastFrameInGpu = outputNv12 && wantGpuFrame;
                m_LastFrameWidth = outputNv12 ? m_Nv12Width : m_Width;
                m_LastFrameHeight = outputNv12 ? m_Nv12Height : m_Height;
                m_HasLastFrame = true;
            }
        }

        if (!mapSucceeded)
        {
            m_ConsecutiveDuplFailures++;
            if (m_ConsecutiveDuplFailures >= 3)
            {
                OSM_LOG_WARN("DXGI frame readback failed %u times; falling back to GDI for 5s",
                             m_ConsecutiveDuplFailures);
                ReleaseD3DResources();
                m_GdiFallbackUntilMs = NowMillis() + 5000;
                m_ConsecutiveDuplFailures = 0;
            }
            return captureGdi(true);
        }

        m_ConsecutiveDuplFailures = 0;
        m_GdiFallbackUntilMs = 0;

        return copyCachedFrame(true);
    }

    bool HostCapture::CaptureViaGdi(CapturedFrame& outFrame, bool captureCursor)
    {
        outFrame.pixelRevision = 0;
        outFrame.width = m_Width;
        outFrame.height = m_Height;
        outFrame.stride = m_Width * 4;
        outFrame.isHdr10Pq = false;
        outFrame.isNv12 = false;
        HostSystemAgent::SyncThreadDesktopToInput();
        HDC hScreen = GetDC(nullptr);
        if (!hScreen) return false;

        HDC hMem = CreateCompatibleDC(hScreen);
        if (!hMem)
        {
            ReleaseDC(nullptr, hScreen);
            return false;
        }

        BITMAPINFO bmi{};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = m_Width;
        bmi.bmiHeader.biHeight = -m_Height;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        HBITMAP hBmp = CreateDIBSection(hScreen, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!hBmp || !bits)
        {
            DeleteDC(hMem);
            ReleaseDC(nullptr, hScreen);
            return false;
        }

        HGDIOBJ oldObj = SelectObject(hMem, hBmp);
        if (!BitBlt(hMem, 0, 0, m_Width, m_Height, hScreen, m_DesktopRect.left, m_DesktopRect.top, SRCCOPY | CAPTUREBLT))
        {
            SelectObject(hMem, oldObj);
            DeleteObject(hBmp);
            DeleteDC(hMem);
            ReleaseDC(nullptr, hScreen);
            return false;
        }

        if (captureCursor)
        {
            CURSORINFO ci{};
            ci.cbSize = sizeof(ci);
            if (GetCursorInfo(&ci) && (ci.flags & CURSOR_SHOWING))
            {
                ICONINFO ii{};
                if (GetIconInfo(ci.hCursor, &ii))
                {
                    int drawX = ci.ptScreenPos.x - m_DesktopRect.left - static_cast<int>(ii.xHotspot);
                    int drawY = ci.ptScreenPos.y - m_DesktopRect.top - static_cast<int>(ii.yHotspot);
                    DrawIconEx(hMem, drawX, drawY, ci.hCursor, 0, 0, 0, nullptr, DI_NORMAL);
                    if (ii.hbmMask) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor) DeleteObject(ii.hbmColor);
                }
            }
        }

        const size_t totalBytes = static_cast<size_t>(m_Width) * static_cast<size_t>(m_Height) * 4;
        outFrame.pixels.resize(totalBytes);
        memcpy(outFrame.pixels.data(), bits, totalBytes);
        outFrame.updated = true;

        SelectObject(hMem, oldObj);
        DeleteObject(hBmp);
        DeleteDC(hMem);
        ReleaseDC(nullptr, hScreen);
        return true;
    }

    void HostCapture::OverlayCursor(CapturedFrame& frame)
    {
        CURSORINFO ci{};
        ci.cbSize = sizeof(ci);
        if (!GetCursorInfo(&ci) || !(ci.flags & CURSOR_SHOWING)) return;

        int cx = ci.ptScreenPos.x - m_DesktopRect.left;
        int cy = ci.ptScreenPos.y - m_DesktopRect.top;
        if (cx < 0 || cy < 0 || cx >= frame.width || cy >= frame.height) return;

        ICONINFO ii{};
        if (!GetIconInfo(ci.hCursor, &ii)) return;
        int hotX = static_cast<int>(ii.xHotspot);
        int hotY = static_cast<int>(ii.yHotspot);
        if (ii.hbmMask) DeleteObject(ii.hbmMask);
        if (ii.hbmColor) DeleteObject(ii.hbmColor);

        const int box = kCursorBoxSize;
        int drawX = cx - hotX;
        int drawY = cy - hotY;

        if (!EnsureCursorScratch()) return;
        HDC hMem = m_CursorBlackDc;
        uint32_t* pixels = m_CursorBlackBits;
        {
            std::vector<uint32_t> beforePatch;
            if (frame.isHdr10Pq)
            {
                beforePatch.assign(static_cast<size_t>(box * box), 0U);
            }

            auto pqToSrgbChan = [](double v) -> uint8_t
            {
                double c = std::clamp(v, 0.0, 1.0);
                double s = (c <= 0.0031308) ? (12.92 * c) : (1.055 * std::pow(c, 1.0 / 2.4) - 0.055);
                return static_cast<uint8_t>(std::clamp(std::lround(s * 255.0), 0L, 255L));
            };
            auto srgbToLin = [](uint8_t b) -> double
            {
                double s = static_cast<double>(b) / 255.0;
                return (s <= 0.04045) ? (s / 12.92) : std::pow((s + 0.055) / 1.055, 2.4);
            };
            auto nitsToPq10 = [](double nits) -> uint32_t
            {
                constexpr double m1 = 0.1593017578125;
                constexpr double m2 = 78.84375;
                constexpr double c1 = 0.8359375;
                constexpr double c2 = 18.8515625;
                constexpr double c3 = 18.6875;
                double L = std::clamp(nits / 10000.0, 0.0, 1.0);
                if (L <= 1e-9) return 0U;
                double Lm1 = std::pow(L, m1);
                double pq = std::pow(std::clamp((c1 + c2 * Lm1) / (1.0 + c3 * Lm1), 0.0, 1.0), m2);
                return static_cast<uint32_t>(std::clamp(std::lround(pq * 1023.0), 0L, 1023L));
            };
            auto pq10ToNits = [](uint32_t code) -> double
            {
                constexpr double m1 = 0.1593017578125;
                constexpr double m2 = 78.84375;
                constexpr double c1 = 0.8359375;
                constexpr double c2 = 18.8515625;
                constexpr double c3 = 18.6875;
                double pq = static_cast<double>(code & 0x3FFU) / 1023.0;
                if (pq <= 1e-6) return 0.0;
                double p = std::pow(pq, 1.0 / m2);
                double num = std::max(0.0, p - c1);
                double den = std::max(1e-6, c2 - c3 * p);
                return 10000.0 * std::pow(num / den, 1.0 / m1);
            };

            const double whiteNits = std::max(1.0f, m_SdrWhiteScale) * 80.0;

            for (int y = 0; y < box; ++y)
            {
                int sy = drawY + y;
                if (sy < 0 || sy >= frame.height) continue;
                for (int x = 0; x < box; ++x)
                {
                    int sx = drawX + x;
                    if (sx < 0 || sx >= frame.width) continue;
                    const uint8_t* src = frame.pixels.data() + (static_cast<size_t>(sy) * frame.stride) + static_cast<
                        size_t>(sx) * 4;
                    if (frame.isHdr10Pq)
                    {
                        uint32_t packed = *reinterpret_cast<const uint32_t*>(src);
                        double r20 = pq10ToNits(packed) / whiteNits;
                        double g20 = pq10ToNits(packed >> 10) / whiteNits;
                        double b20 = pq10ToNits(packed >> 20) / whiteNits;
                        double r709 = 1.660491 * r20 - 0.587641 * g20 - 0.072850 * b20;
                        double g709 = -0.124550 * r20 + 1.132900 * g20 - 0.008349 * b20;
                        double b709 = -0.018151 * r20 - 0.100579 * g20 + 1.118730 * b20;
                        uint8_t rb = pqToSrgbChan(r709);
                        uint8_t gb = pqToSrgbChan(g709);
                        uint8_t bb = pqToSrgbChan(b709);
                        uint32_t bg = uint32_t(bb) | (uint32_t(gb) << 8) | (uint32_t(rb) << 16) | 0xFF000000U;
                        pixels[y * box + x] = bg;
                        beforePatch[y * box + x] = bg;
                    }
                    else
                    {
                        uint32_t bg = uint32_t(src[0]) | (uint32_t(src[1]) << 8) | (uint32_t(src[2]) << 16) |
                            0xFF000000U;
                        pixels[y * box + x] = bg;
                    }
                }
            }

            DrawIconEx(hMem, 0, 0, ci.hCursor, 0, 0, 0, nullptr, DI_NORMAL);
            GdiFlush();

            for (int y = 0; y < box; ++y)
            {
                int sy = drawY + y;
                if (sy < 0 || sy >= frame.height) continue;
                for (int x = 0; x < box; ++x)
                {
                    int sx = drawX + x;
                    if (sx < 0 || sx >= frame.width) continue;
                    uint32_t px = pixels[y * box + x];
                    uint8_t* dst = frame.pixels.data() + (static_cast<size_t>(sy) * frame.stride) + static_cast<size_t>(
                        sx) * 4;
                    if (frame.isHdr10Pq)
                    {
                        if (px == beforePatch[y * box + x]) continue;
                        double r709 = srgbToLin(static_cast<uint8_t>((px >> 16) & 0xFF)) * whiteNits;
                        double g709 = srgbToLin(static_cast<uint8_t>((px >> 8) & 0xFF)) * whiteNits;
                        double b709 = srgbToLin(static_cast<uint8_t>(px & 0xFF)) * whiteNits;
                        double r20 = 0.6274040 * r709 + 0.3292830 * g709 + 0.0433130 * b709;
                        double g20 = 0.0690970 * r709 + 0.9195400 * g709 + 0.0113620 * b709;
                        double b20 = 0.0163916 * r709 + 0.0880132 * g709 + 0.8955950 * b709;
                        uint32_t packed = 0xC0000000U | nitsToPq10(r20) | (nitsToPq10(g20) << 10) | (nitsToPq10(b20) <<
                            20);
                        *reinterpret_cast<uint32_t*>(dst) = packed;
                    }
                    else
                    {
                        dst[0] = static_cast<uint8_t>(px & 0xFF);
                        dst[1] = static_cast<uint8_t>((px >> 8) & 0xFF);
                        dst[2] = static_cast<uint8_t>((px >> 16) & 0xFF);
                        dst[3] = 0xFF;
                    }
                }
            }
        }
    }
}
