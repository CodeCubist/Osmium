#pragma once

#include "OsmiumCommon.h"
#include "OsmiumProtocol.h"
#include "OsmiumTransport.h"

#include <SDL3/SDL.h>
#include <functional>

namespace osmium
{
    class ClientInputCapture
    {
    public:
        ClientInputCapture() = default;
        ~ClientInputCapture();

        struct ActionCallbacks
        {
            std::function<void()> onDisconnectRequested;
            std::function<void()> onToggleOverlayRequested;
            std::function<void()> onToggleFullscreenRequested;
        };

        void Initialize(
            SDL_Window* window,
            OsmiumTransport* transport,
            const OsmiumClientConfig& config,
            ActionCallbacks callbacks);

        void Shutdown();
        void ProcessSdlEvent(const SDL_Event& ev);
        void PollGamepads();
        void OnFocusChanged(bool focused, bool isFullscreen);

    private:
        static uint16_t SdlScancodeToWinVk(SDL_Scancode scancode, SDL_Keycode key, bool& outExtended);
        void ToggleMouseCapture();
        void UpdateMouseGrab(bool capture);

        SDL_Window* m_Window = nullptr;
        OsmiumTransport* m_Transport = nullptr;
        OsmiumClientConfig m_Config{};
        ActionCallbacks m_Callbacks;

        bool m_MouseCaptured = false;
        bool m_WindowFocused = true;
        int m_VideoWidth = 1920;
        int m_VideoHeight = 1080;

        SDL_Gamepad* m_Gamepads[4]{};
        SDL_JoystickID m_GamepadIds[4]{};

        HHOOK m_KeyboardHook = nullptr;
        bool m_IsFullscreen = false;
        static ClientInputCapture* s_HookInstance;
        static LRESULT CALLBACK LowLevelKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam);
        void UpdateKeyboardHook();
    };
}
