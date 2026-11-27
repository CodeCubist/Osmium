#include "HostPrivacyScreen.h"
#include "OsmiumCommon.h"

#include <objidl.h>
#include <gdiplus.h>
#include <algorithm>
#include <future>
#include <memory>
#include <utility>

#pragma comment(lib, "gdiplus.lib")

namespace osmium
{
    namespace
    {
        constexpr wchar_t kWindowClass[] = L"OsmiumPrivacyScreen";
        constexpr int kExitHotkey = 1;
        constexpr UINT_PTR kCoverTimer = 1;

        bool IsDefaultDesktop(HDESK desktop)
        {
            wchar_t name[64]{};
            return desktop && GetUserObjectInformationW(desktop, UOI_NAME, name, sizeof(name), nullptr) &&
                _wcsicmp(name, L"Default") == 0;
        }

        bool CanShowOnInputDesktop()
        {
            if (!IsDefaultDesktop(GetThreadDesktop(GetCurrentThreadId()))) return false;
            HDESK input = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
            if (!input) return false;
            bool same = IsDefaultDesktop(input);
            CloseDesktop(input);
            return same;
        }

        struct WindowState
        {
            Gdiplus::Image* wallpaper = nullptr;
            const std::atomic<bool>* sessionEnded = nullptr;
        };

        struct WallpaperPaint
        {
            Gdiplus::Graphics& graphics;
            Gdiplus::Image& image;
            RECT window;
        };

        BOOL CALLBACK PaintMonitor(HMONITOR, HDC, LPRECT monitor, LPARAM param)
        {
            auto& state = *reinterpret_cast<WallpaperPaint*>(param);
            const int w = monitor->right - monitor->left;
            const int h = monitor->bottom - monitor->top;
            if (w <= 0 || h <= 0) return TRUE;

            const double imageW = state.image.GetWidth(), imageH = state.image.GetHeight();
            const double scale = std::max(w / imageW, h / imageH);
            const double srcW = w / scale, srcH = h / scale;
            Gdiplus::RectF dst(static_cast<float>(monitor->left - state.window.left),
                               static_cast<float>(monitor->top - state.window.top),
                               static_cast<float>(w), static_cast<float>(h));
            state.graphics.DrawImage(&state.image, dst,
                                     static_cast<float>((imageW - srcW) / 2),
                                     static_cast<float>((imageH - srcH) / 2),
                                     static_cast<float>(srcW), static_cast<float>(srcH), Gdiplus::UnitPixel);
            return TRUE;
        }

        bool CoverDesktop(HWND window, bool show)
        {
            RECT bounds{};
            GetWindowRect(window, &bounds);
            const int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            const int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0) return false;
            const bool moved = bounds.left != x || bounds.top != y || bounds.right != x + w || bounds.bottom != y + h;
            bool needsRaise = false;
            if (!show && !moved)
            {
                for (HWND prev = GetWindow(window, GW_HWNDPREV); prev; prev = GetWindow(prev, GW_HWNDPREV))
                {
                    RECT other{}, overlap{};
                    if (IsWindowVisible(prev) && GetWindowRect(prev, &other) &&
                        IntersectRect(&overlap, &bounds, &other))
                    {
                        needsRaise = true;
                        break;
                    }
                }
            }
            if ((show || moved || needsRaise) &&
                !SetWindowPos(window, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : 0)))
            {
                return false;
            }
            if (moved) InvalidateRect(window, nullptr, FALSE);
            return true;
        }

        LRESULT CALLBACK PrivacyWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
        {
            switch (message)
            {
            case WM_NCCREATE:
                SetWindowLongPtrW(window, GWLP_USERDATA,
                                  reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams));
                break;
            case WM_PAINT:
                {
                    PAINTSTRUCT paint{};
                    HDC dc = BeginPaint(window, &paint);
                    FillRect(dc, &paint.rcPaint, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
                    auto* data = reinterpret_cast<WindowState*>(GetWindowLongPtrW(window, GWLP_USERDATA));
                    if (data && data->wallpaper)
                    {
                        Gdiplus::Graphics graphics(dc);
                        graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBilinear);
                        WallpaperPaint state{graphics, *data->wallpaper, {}};
                        GetWindowRect(window, &state.window);
                        EnumDisplayMonitors(nullptr, nullptr, PaintMonitor, reinterpret_cast<LPARAM>(&state));
                    }
                    EndPaint(window, &paint);
                    return 0;
                }
            case WM_DISPLAYCHANGE:
                if (!CoverDesktop(window, false)) DestroyWindow(window);
                else InvalidateRect(window, nullptr, FALSE);
                return 0;
            case WM_DPICHANGED:
                InvalidateRect(window, nullptr, FALSE);
                return 0;
            case WM_TIMER:
                {
                    auto* data = reinterpret_cast<WindowState*>(GetWindowLongPtrW(window, GWLP_USERDATA));
                    if ((data && data->sessionEnded && data->sessionEnded->load()) || !CoverDesktop(window, false))
                        DestroyWindow(window);
                    return 0;
                }
            case WM_HOTKEY:
                if (wParam == kExitHotkey) DestroyWindow(window);
                return 0;
            case WM_DESTROY:
                UnregisterHotKey(window, kExitHotkey);
                PostQuitMessage(0);
                return 0;
            }
            return DefWindowProcW(window, message, wParam, lParam);
        }

        struct WallpaperImage
        {
            explicit WallpaperImage(const std::wstring& path)
            {
                if (path.empty()) return;
                Gdiplus::GdiplusStartupInput input;
                if (Gdiplus::GdiplusStartup(&token, &input, nullptr) == Gdiplus::Ok)
                {
                    image.reset(Gdiplus::Image::FromFile(path.c_str(), FALSE));
                    if (image && image->GetLastStatus() == Gdiplus::Ok && image->GetWidth() && image->GetHeight())
                        return;
                    image.reset();
                }
                OSM_LOG_WARN("Privacy screen wallpaper unavailable; using black");
            }

            ~WallpaperImage()
            {
                image.reset();
                if (token) Gdiplus::GdiplusShutdown(token);
            }

            ULONG_PTR token = 0;
            std::unique_ptr<Gdiplus::Image> image;
        };
    }

    HostPrivacyScreen::~HostPrivacyScreen()
    {
        Hide();
    }

    bool HostPrivacyScreen::Show(std::wstring wallpaperPath, const std::atomic<bool>* sessionEnded)
    {
        if (sessionEnded && sessionEnded->load()) return false;
        if (IsActive()) return true;
        Hide();

        std::promise<DWORD> ready;
        auto started = ready.get_future();
        m_Thread = std::thread([this, path = std::move(wallpaperPath), sessionEnded, ready = std::move(ready)]() mutable
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (!CanShowOnInputDesktop())
            {
                ready.set_value(0);
                return;
            }
            WallpaperImage wallpaper(path);
            if (sessionEnded && sessionEnded->load())
            {
                ready.set_value(0);
                return;
            }
            WindowState state{wallpaper.image.get(), sessionEnded};
            HINSTANCE instance = GetModuleHandleW(nullptr);
            WNDCLASSW wc{};
            wc.lpfnWndProc = PrivacyWindowProc;
            wc.hInstance = instance;
            wc.lpszClassName = kWindowClass;
            if (!RegisterClassW(&wc))
            {
                ready.set_value(0);
                return;
            }
            HWND window = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
                kWindowClass, L"Osmium", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, instance, &state);
            DWORD affinity = 0;
            const bool configured = window && SetLayeredWindowAttributes(window, 0, 255, LWA_ALPHA) &&
                SetWindowDisplayAffinity(window, WDA_EXCLUDEFROMCAPTURE) &&
                GetWindowDisplayAffinity(window, &affinity) && affinity == WDA_EXCLUDEFROMCAPTURE &&
                RegisterHotKey(window, kExitHotkey, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 'P') &&
                SetTimer(window, kCoverTimer, 250, nullptr);
            if (configured) m_Active.store(true);
            if (configured && CoverDesktop(window, true))
            {
                UpdateWindow(window);
                ready.set_value(GetCurrentThreadId());
                MSG msg{};
                while (GetMessageW(&msg, nullptr, 0, 0) > 0) DispatchMessageW(&msg);
            }
            else
            {
                OSM_LOG_WARN("Privacy screen could not be shown (Win32 error %lu, affinity=0x%lx)", GetLastError(),
                             affinity);
                ready.set_value(0);
            }
            if (IsWindow(window)) DestroyWindow(window);
            m_Active.store(false);
            UnregisterClassW(kWindowClass, instance);
        });
        m_ThreadId = started.get();
        if (!m_ThreadId) Hide();
        return m_ThreadId != 0;
    }

    void HostPrivacyScreen::Hide()
    {
        if (!m_Thread.joinable()) return;
        if (m_ThreadId) PostThreadMessageW(m_ThreadId, WM_QUIT, 0, 0);
        m_Thread.join();
        m_ThreadId = 0;
    }
}
