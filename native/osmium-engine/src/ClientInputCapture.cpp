#include "ClientInputCapture.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace osmium
{
    ClientInputCapture* ClientInputCapture::s_HookInstance = nullptr;

    ClientInputCapture::~ClientInputCapture()
    {
        Shutdown();
    }

    LRESULT CALLBACK ClientInputCapture::LowLevelKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        auto* self = s_HookInstance;
        if (nCode == HC_ACTION && self && self->m_WindowFocused && self->m_Transport)
        {
            bool captureSysKeys =
                (self->m_Config.captureSysKeysMode == 2) ||
                (self->m_Config.captureSysKeysMode == 1 && self->m_IsFullscreen);

            if (captureSysKeys)
            {
                const auto* kb = reinterpret_cast<const KBDLLHOOKSTRUCT*>(lParam);
                bool isSysCombo =
                    kb->vkCode == VK_LWIN || kb->vkCode == VK_RWIN ||
                    (kb->vkCode == VK_TAB && (kb->flags & LLKHF_ALTDOWN)) ||
                    (kb->vkCode == VK_ESCAPE && (kb->flags & LLKHF_ALTDOWN)) ||
                    (kb->vkCode == VK_ESCAPE && (GetAsyncKeyState(VK_CONTROL) & 0x8000));

                if (isSysCombo)
                {
                    bool isDown = (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN);
                    proto::InputKeyPayload pkt{};
                    pkt.vkCode = static_cast<uint16_t>(kb->vkCode);
                    pkt.scanCode = static_cast<uint16_t>(kb->scanCode);
                    pkt.isDown = isDown ? 1 : 0;
                    pkt.isExtended = (kb->flags & LLKHF_EXTENDED) ? 1 : 0;
                    self->m_Transport->SendEncrypted(proto::AppPacketType::InputKey, &pkt, sizeof(pkt));
                    return 1;
                }
            }
        }
        return CallNextHookEx(nullptr, nCode, wParam, lParam);
    }

    void ClientInputCapture::UpdateKeyboardHook()
    {
        bool wantHook = (m_Config.captureSysKeysMode == 2) ||
            (m_Config.captureSysKeysMode == 1 && m_IsFullscreen);
        if (wantHook && !m_KeyboardHook)
        {
            s_HookInstance = this;
            m_KeyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, LowLevelKeyboardProc, GetModuleHandleW(nullptr), 0);
        }
        else if (!wantHook && m_KeyboardHook)
        {
            UnhookWindowsHookEx(m_KeyboardHook);
            m_KeyboardHook = nullptr;
            if (s_HookInstance == this) s_HookInstance = nullptr;
        }
    }

    void ClientInputCapture::Initialize(
        SDL_Window* window,
        OsmiumTransport* transport,
        const OsmiumClientConfig& config,
        ActionCallbacks callbacks)
    {
        Shutdown();
        m_Window = window;
        m_Transport = transport;
        m_Config = config;
        m_Callbacks = std::move(callbacks);
        m_VideoWidth = config.targetWidth > 0 ? config.targetWidth : 1920;
        m_VideoHeight = config.targetHeight > 0 ? config.targetHeight : 1080;
        m_IsFullscreen = (config.windowMode == 0);
        UpdateKeyboardHook();

        if (!m_Config.absoluteMouseMode)
        {
            UpdateMouseGrab(true);
        }

        int count = 0;
        SDL_JoystickID* ids = SDL_GetJoysticks(&count);
        if (ids)
        {
            int slot = 0;
            for (int i = 0; i < count && slot < 4; ++i)
            {
                if (SDL_IsGamepad(ids[i]))
                {
                    m_Gamepads[slot] = SDL_OpenGamepad(ids[i]);
                    m_GamepadIds[slot] = ids[i];
                    if (m_Gamepads[slot]) slot++;
                    if (!m_Config.multiController) break;
                }
            }
            SDL_free(ids);
        }
    }

    void ClientInputCapture::Shutdown()
    {
        if (m_KeyboardHook)
        {
            UnhookWindowsHookEx(m_KeyboardHook);
            m_KeyboardHook = nullptr;
        }
        if (s_HookInstance == this) s_HookInstance = nullptr;
        UpdateMouseGrab(false);
        for (int i = 0; i < 4; ++i)
        {
            if (m_Gamepads[i])
            {
                SDL_CloseGamepad(m_Gamepads[i]);
                m_Gamepads[i] = nullptr;
                m_GamepadIds[i] = 0;
            }
        }
        m_Window = nullptr;
        m_Transport = nullptr;
    }

    void ClientInputCapture::UpdateMouseGrab(bool capture)
    {
        m_MouseCaptured = capture;
        if (m_Window)
        {
            SDL_SetWindowRelativeMouseMode(m_Window, capture);
        }
    }

    void ClientInputCapture::ToggleMouseCapture()
    {
        m_Config.absoluteMouseMode = m_Config.absoluteMouseMode ? 0 : 1;
        UpdateMouseGrab(!m_Config.absoluteMouseMode);
    }

    void ClientInputCapture::OnFocusChanged(bool focused, bool isFullscreen)
    {
        m_WindowFocused = focused;
        m_IsFullscreen = isFullscreen;
        UpdateKeyboardHook();
        if (!focused && m_MouseCaptured)
        {
            UpdateMouseGrab(false);
        }
        else if (focused && !m_Config.absoluteMouseMode)
        {
            UpdateMouseGrab(true);
        }
    }

    uint16_t ClientInputCapture::SdlScancodeToWinVk(SDL_Scancode sc, SDL_Keycode key, bool& outExtended)
    {
        outExtended = false;
        switch (sc)
        {
        case SDL_SCANCODE_A: return 'A';
        case SDL_SCANCODE_B: return 'B';
        case SDL_SCANCODE_C: return 'C';
        case SDL_SCANCODE_D: return 'D';
        case SDL_SCANCODE_E: return 'E';
        case SDL_SCANCODE_F: return 'F';
        case SDL_SCANCODE_G: return 'G';
        case SDL_SCANCODE_H: return 'H';
        case SDL_SCANCODE_I: return 'I';
        case SDL_SCANCODE_J: return 'J';
        case SDL_SCANCODE_K: return 'K';
        case SDL_SCANCODE_L: return 'L';
        case SDL_SCANCODE_M: return 'M';
        case SDL_SCANCODE_N: return 'N';
        case SDL_SCANCODE_O: return 'O';
        case SDL_SCANCODE_P: return 'P';
        case SDL_SCANCODE_Q: return 'Q';
        case SDL_SCANCODE_R: return 'R';
        case SDL_SCANCODE_S: return 'S';
        case SDL_SCANCODE_T: return 'T';
        case SDL_SCANCODE_U: return 'U';
        case SDL_SCANCODE_V: return 'V';
        case SDL_SCANCODE_W: return 'W';
        case SDL_SCANCODE_X: return 'X';
        case SDL_SCANCODE_Y: return 'Y';
        case SDL_SCANCODE_Z: return 'Z';
        case SDL_SCANCODE_1: return '1';
        case SDL_SCANCODE_2: return '2';
        case SDL_SCANCODE_3: return '3';
        case SDL_SCANCODE_4: return '4';
        case SDL_SCANCODE_5: return '5';
        case SDL_SCANCODE_6: return '6';
        case SDL_SCANCODE_7: return '7';
        case SDL_SCANCODE_8: return '8';
        case SDL_SCANCODE_9: return '9';
        case SDL_SCANCODE_0: return '0';
        case SDL_SCANCODE_RETURN: return VK_RETURN;
        case SDL_SCANCODE_ESCAPE: return VK_ESCAPE;
        case SDL_SCANCODE_BACKSPACE: return VK_BACK;
        case SDL_SCANCODE_TAB: return VK_TAB;
        case SDL_SCANCODE_SPACE: return VK_SPACE;
        case SDL_SCANCODE_MINUS: return VK_OEM_MINUS;
        case SDL_SCANCODE_EQUALS: return VK_OEM_PLUS;
        case SDL_SCANCODE_LEFTBRACKET: return VK_OEM_4;
        case SDL_SCANCODE_RIGHTBRACKET: return VK_OEM_6;
        case SDL_SCANCODE_BACKSLASH: return VK_OEM_5;
        case SDL_SCANCODE_SEMICOLON: return VK_OEM_1;
        case SDL_SCANCODE_APOSTROPHE: return VK_OEM_7;
        case SDL_SCANCODE_GRAVE: return VK_OEM_3;
        case SDL_SCANCODE_COMMA: return VK_OEM_COMMA;
        case SDL_SCANCODE_PERIOD: return VK_OEM_PERIOD;
        case SDL_SCANCODE_SLASH: return VK_OEM_2;
        case SDL_SCANCODE_CAPSLOCK: return VK_CAPITAL;
        case SDL_SCANCODE_F1: return VK_F1;
        case SDL_SCANCODE_F2: return VK_F2;
        case SDL_SCANCODE_F3: return VK_F3;
        case SDL_SCANCODE_F4: return VK_F4;
        case SDL_SCANCODE_F5: return VK_F5;
        case SDL_SCANCODE_F6: return VK_F6;
        case SDL_SCANCODE_F7: return VK_F7;
        case SDL_SCANCODE_F8: return VK_F8;
        case SDL_SCANCODE_F9: return VK_F9;
        case SDL_SCANCODE_F10: return VK_F10;
        case SDL_SCANCODE_F11: return VK_F11;
        case SDL_SCANCODE_F12: return VK_F12;
        case SDL_SCANCODE_PRINTSCREEN: outExtended = true;
            return VK_SNAPSHOT;
        case SDL_SCANCODE_SCROLLLOCK: return VK_SCROLL;
        case SDL_SCANCODE_PAUSE: return VK_PAUSE;
        case SDL_SCANCODE_INSERT: outExtended = true;
            return VK_INSERT;
        case SDL_SCANCODE_HOME: outExtended = true;
            return VK_HOME;
        case SDL_SCANCODE_PAGEUP: outExtended = true;
            return VK_PRIOR;
        case SDL_SCANCODE_DELETE: outExtended = true;
            return VK_DELETE;
        case SDL_SCANCODE_END: outExtended = true;
            return VK_END;
        case SDL_SCANCODE_PAGEDOWN: outExtended = true;
            return VK_NEXT;
        case SDL_SCANCODE_RIGHT: outExtended = true;
            return VK_RIGHT;
        case SDL_SCANCODE_LEFT: outExtended = true;
            return VK_LEFT;
        case SDL_SCANCODE_DOWN: outExtended = true;
            return VK_DOWN;
        case SDL_SCANCODE_UP: outExtended = true;
            return VK_UP;
        case SDL_SCANCODE_NUMLOCKCLEAR: outExtended = true;
            return VK_NUMLOCK;
        case SDL_SCANCODE_KP_DIVIDE: outExtended = true;
            return VK_DIVIDE;
        case SDL_SCANCODE_KP_MULTIPLY: return VK_MULTIPLY;
        case SDL_SCANCODE_KP_MINUS: return VK_SUBTRACT;
        case SDL_SCANCODE_KP_PLUS: return VK_ADD;
        case SDL_SCANCODE_KP_ENTER: outExtended = true;
            return VK_RETURN;
        case SDL_SCANCODE_KP_1: return VK_NUMPAD1;
        case SDL_SCANCODE_KP_2: return VK_NUMPAD2;
        case SDL_SCANCODE_KP_3: return VK_NUMPAD3;
        case SDL_SCANCODE_KP_4: return VK_NUMPAD4;
        case SDL_SCANCODE_KP_5: return VK_NUMPAD5;
        case SDL_SCANCODE_KP_6: return VK_NUMPAD6;
        case SDL_SCANCODE_KP_7: return VK_NUMPAD7;
        case SDL_SCANCODE_KP_8: return VK_NUMPAD8;
        case SDL_SCANCODE_KP_9: return VK_NUMPAD9;
        case SDL_SCANCODE_KP_0: return VK_NUMPAD0;
        case SDL_SCANCODE_KP_PERIOD: return VK_DECIMAL;
        case SDL_SCANCODE_LCTRL: return VK_LCONTROL;
        case SDL_SCANCODE_LSHIFT: return VK_LSHIFT;
        case SDL_SCANCODE_LALT: return VK_LMENU;
        case SDL_SCANCODE_LGUI: outExtended = true;
            return VK_LWIN;
        case SDL_SCANCODE_RCTRL: outExtended = true;
            return VK_RCONTROL;
        case SDL_SCANCODE_RSHIFT: return VK_RSHIFT;
        case SDL_SCANCODE_RALT: outExtended = true;
            return VK_RMENU;
        case SDL_SCANCODE_RGUI: outExtended = true;
            return VK_RWIN;
        default:
            if (key >= 'a' && key <= 'z') return static_cast<uint16_t>(key - 'a' + 'A');
            if (key >= '0' && key <= '9') return static_cast<uint16_t>(key);
            return 0;
        }
    }

    void ClientInputCapture::ProcessSdlEvent(const SDL_Event& ev)
    {
        if (!m_Transport) return;

        if (ev.type == SDL_EVENT_KEY_DOWN || ev.type == SDL_EVENT_KEY_UP)
        {
            bool isDown = (ev.type == SDL_EVENT_KEY_DOWN);
            SDL_Keymod mod = ev.key.mod;
            bool hasShortcutCombo = (mod & SDL_KMOD_CTRL) && (mod & SDL_KMOD_ALT) && (mod & SDL_KMOD_SHIFT);

            if (isDown && hasShortcutCombo)
            {
                if (ev.key.scancode == SDL_SCANCODE_Q)
                {
                    if (m_Callbacks.onDisconnectRequested) m_Callbacks.onDisconnectRequested();
                    return;
                }
                if (ev.key.scancode == SDL_SCANCODE_S)
                {
                    if (m_Callbacks.onToggleOverlayRequested) m_Callbacks.onToggleOverlayRequested();
                    return;
                }
                if (ev.key.scancode == SDL_SCANCODE_M)
                {
                    ToggleMouseCapture();
                    return;
                }
                if (ev.key.scancode == SDL_SCANCODE_F)
                {
                    if (m_Callbacks.onToggleFullscreenRequested) m_Callbacks.onToggleFullscreenRequested();
                    return;
                }
            }

            bool extended = false;
            uint16_t vk = SdlScancodeToWinVk(ev.key.scancode, ev.key.key, extended);
            if (vk != 0)
            {
                proto::InputKeyPayload pkt{};
                pkt.vkCode = vk;
                pkt.scanCode = static_cast<uint16_t>(MapVirtualKeyW(vk, MAPVK_VK_TO_VSC));
                pkt.isDown = isDown ? 1 : 0;
                pkt.isExtended = extended ? 1 : 0;
                m_Transport->SendEncrypted(proto::AppPacketType::InputKey, &pkt, sizeof(pkt));
            }
            return;
        }

        if (ev.type == SDL_EVENT_MOUSE_MOTION)
        {
            proto::InputMouseMovePayload pkt{};
            if (m_Config.absoluteMouseMode && !m_MouseCaptured)
            {
                int winW = 1, winH = 1;
                SDL_GetWindowSize(m_Window, &winW, &winH);
                pkt.isAbsolute = 1;
                pkt.xOrDeltaX = std::clamp(ev.motion.x / static_cast<float>(std::max(1, winW)), 0.0f, 1.0f);
                pkt.yOrDeltaY = std::clamp(ev.motion.y / static_cast<float>(std::max(1, winH)), 0.0f, 1.0f);
            }
            else
            {
                pkt.isAbsolute = 0;
                pkt.xOrDeltaX = ev.motion.xrel;
                pkt.yOrDeltaY = ev.motion.yrel;
            }
            m_Transport->SendEncrypted(proto::AppPacketType::InputMouseMove, &pkt, sizeof(pkt));
            return;
        }

        if (ev.type == SDL_EVENT_MOUSE_BUTTON_DOWN || ev.type == SDL_EVENT_MOUSE_BUTTON_UP)
        {
            uint8_t btn = 0;
            switch (ev.button.button)
            {
            case SDL_BUTTON_LEFT: btn = m_Config.swapMouseButtons ? 3 : 1;
                break;
            case SDL_BUTTON_MIDDLE: btn = 2;
                break;
            case SDL_BUTTON_RIGHT: btn = m_Config.swapMouseButtons ? 1 : 3;
                break;
            case SDL_BUTTON_X1: btn = 4;
                break;
            case SDL_BUTTON_X2: btn = 5;
                break;
            default: break;
            }
            if (btn != 0)
            {
                proto::InputMouseButtonPayload pkt{};
                pkt.button = btn;
                pkt.isDown = (ev.type == SDL_EVENT_MOUSE_BUTTON_DOWN) ? 1 : 0;
                m_Transport->SendEncrypted(proto::AppPacketType::InputMouseButton, &pkt, sizeof(pkt));
            }
            return;
        }

        if (ev.type == SDL_EVENT_MOUSE_WHEEL)
        {
            proto::InputMouseWheelPayload pkt{};
            float dir = m_Config.reverseScrollDirection ? -1.0f : 1.0f;
            pkt.deltaX = static_cast<int16_t>(std::lround(ev.wheel.x * static_cast<float>(WHEEL_DELTA)));
            pkt.deltaY = static_cast<int16_t>(std::lround(ev.wheel.y * dir * static_cast<float>(WHEEL_DELTA)));
            if (pkt.deltaX != 0 || pkt.deltaY != 0)
            {
                m_Transport->SendEncrypted(proto::AppPacketType::InputMouseWheel, &pkt, sizeof(pkt));
            }
            return;
        }

        if (ev.type == SDL_EVENT_GAMEPAD_ADDED)
        {
            for (int i = 0; i < 4; ++i)
            {
                if (!m_Gamepads[i])
                {
                    m_Gamepads[i] = SDL_OpenGamepad(ev.gdevice.which);
                    m_GamepadIds[i] = ev.gdevice.which;
                    break;
                }
                if (!m_Config.multiController) break;
            }
        }
        else if (ev.type == SDL_EVENT_GAMEPAD_REMOVED)
        {
            for (int i = 0; i < 4; ++i)
            {
                if (m_Gamepads[i] && m_GamepadIds[i] == ev.gdevice.which)
                {
                    SDL_CloseGamepad(m_Gamepads[i]);
                    m_Gamepads[i] = nullptr;
                    m_GamepadIds[i] = 0;
                }
            }
        }
    }

    void ClientInputCapture::PollGamepads()
    {
        if (!m_Transport) return;
        if (!m_WindowFocused && !m_Config.backgroundGamepad) return;

        for (int i = 0; i < 4; ++i)
        {
            SDL_Gamepad* gp = m_Gamepads[i];
            if (!gp) continue;

            uint16_t mask = 0;
            bool swapFace = m_Config.swapFaceButtons != 0;
            if (SDL_GetGamepadButton(gp, swapFace ? SDL_GAMEPAD_BUTTON_EAST : SDL_GAMEPAD_BUTTON_SOUTH)) mask |= 0x1000;
            if (SDL_GetGamepadButton(gp, swapFace ? SDL_GAMEPAD_BUTTON_SOUTH : SDL_GAMEPAD_BUTTON_EAST)) mask |= 0x2000;
            if (SDL_GetGamepadButton(gp, swapFace ? SDL_GAMEPAD_BUTTON_NORTH : SDL_GAMEPAD_BUTTON_WEST)) mask |= 0x4000;
            if (SDL_GetGamepadButton(gp, swapFace ? SDL_GAMEPAD_BUTTON_WEST : SDL_GAMEPAD_BUTTON_NORTH)) mask |= 0x8000;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_DPAD_UP)) mask |= 0x0001;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_DPAD_DOWN)) mask |= 0x0002;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_DPAD_LEFT)) mask |= 0x0004;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_DPAD_RIGHT)) mask |= 0x0008;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_START)) mask |= 0x0010;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_BACK)) mask |= 0x0020;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_LEFT_STICK)) mask |= 0x0040;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_RIGHT_STICK))mask |= 0x0080;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_LEFT_SHOULDER)) mask |= 0x0100;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER)) mask |= 0x0200;
            if (SDL_GetGamepadButton(gp, SDL_GAMEPAD_BUTTON_GUIDE)) mask |= 0x0400;

            proto::InputGamepadPayload pkt{};
            pkt.controllerIndex = static_cast<uint8_t>(i);
            pkt.connected = 1;
            pkt.buttonsMask = mask;
            pkt.leftTrigger = static_cast<uint8_t>(SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_LEFT_TRIGGER) >> 7);
            pkt.rightTrigger = static_cast<uint8_t>(SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER) >> 7);
            pkt.leftThumbX = SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_LEFTX);
            pkt.leftThumbY = SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_LEFTY);
            pkt.rightThumbX = SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_RIGHTX);
            pkt.rightThumbY = SDL_GetGamepadAxis(gp, SDL_GAMEPAD_AXIS_RIGHTY);

            m_Transport->SendEncrypted(proto::AppPacketType::InputGamepad, &pkt, sizeof(pkt));
        }
    }
}
