#include "HostInputInjector.h"
#include "HostSystemAgent.h"
#include "UsbIpVirtualDevices.h"

#include <algorithm>
#include <cmath>
#include <vector>

namespace osmium
{
    void HostInputInjector::SetTargetDisplayRect(RECT rect)
    {
        std::lock_guard<std::mutex> lock(m_RectMutex);
        m_DisplayRect = rect;
    }

    void HostInputInjector::InjectPacket(const uint8_t* data, size_t len)
    {
        if (!m_Enabled.load() || !data || len < sizeof(proto::AppPacketHeader)) return;

        const auto* hdr = reinterpret_cast<const proto::AppPacketHeader*>(data);
        switch (static_cast<proto::AppPacketType>(hdr->type))
        {
        case proto::AppPacketType::InputKey:
            if (len >= sizeof(proto::InputKeyPayload))
            {
                HandleKey(*reinterpret_cast<const proto::InputKeyPayload*>(data));
            }
            break;
        case proto::AppPacketType::InputMouseMove:
            if (len >= sizeof(proto::InputMouseMovePayload))
            {
                HandleMouseMove(*reinterpret_cast<const proto::InputMouseMovePayload*>(data));
            }
            break;
        case proto::AppPacketType::InputMouseButton:
            if (len >= sizeof(proto::InputMouseButtonPayload))
            {
                HandleMouseButton(*reinterpret_cast<const proto::InputMouseButtonPayload*>(data));
            }
            break;
        case proto::AppPacketType::InputMouseWheel:
            if (len >= sizeof(proto::InputMouseWheelPayload))
            {
                HandleMouseWheel(*reinterpret_cast<const proto::InputMouseWheelPayload*>(data));
            }
            break;
        case proto::AppPacketType::InputUnicodeText:
            HandleUnicodeText(data, len);
            break;
        default:
            break;
        }
    }

    void HostInputInjector::ReleaseAllInputs()
    {
        HostSystemAgent::SyncThreadDesktopToInput(&m_LastInputDesk);

        const WORD modifiers[] = {VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN};
        for (WORD vk : modifiers)
        {
            if (GetAsyncKeyState(vk) & 0x8000)
            {
                INPUT in{};
                in.type = INPUT_KEYBOARD;
                in.ki.wVk = vk;
                in.ki.dwFlags = KEYEVENTF_KEYUP | ((vk == VK_RCONTROL || vk == VK_RMENU || vk == VK_LWIN || vk ==
                                                       VK_RWIN)
                                                       ? KEYEVENTF_EXTENDEDKEY
                                                       : 0);
                SendInput(1, &in, sizeof(INPUT));
            }
        }
        if (m_LastInputDesk)
        {
            CloseDesktop(m_LastInputDesk);
            m_LastInputDesk = nullptr;
        }
    }

    void HostInputInjector::HandleKey(const proto::InputKeyPayload& pkt)
    {
        HostSystemAgent::SyncThreadDesktopToInput(&m_LastInputDesk);

        INPUT input{};
        input.type = INPUT_KEYBOARD;
        input.ki.wVk = pkt.vkCode;
        input.ki.wScan = pkt.scanCode;
        if (pkt.scanCode == 0 && pkt.vkCode != 0)
        {
            input.ki.wScan = static_cast<WORD>(MapVirtualKeyW(pkt.vkCode, MAPVK_VK_TO_VSC));
        }
        DWORD flags = 0;
        if (!pkt.isDown) flags |= KEYEVENTF_KEYUP;
        if (pkt.isExtended) flags |= KEYEVENTF_EXTENDEDKEY;
        input.ki.dwFlags = flags;
        SendInput(1, &input, sizeof(INPUT));
    }

    void HostInputInjector::HandleMouseMove(const proto::InputMouseMovePayload& pkt)
    {
        if (m_UseVirtualMouse.load() && !pkt.isAbsolute && m_VirtualDevices && m_VirtualDevices->HasActiveHidConsumer())
        {
            int32_t dx = static_cast<int32_t>(std::lround(pkt.xOrDeltaX));
            int32_t dy = static_cast<int32_t>(std::lround(pkt.yOrDeltaY));
            if (dx != 0 || dy != 0)
            {
                m_VirtualDevices->PushMouseDelta(
                    static_cast<int16_t>(std::clamp(dx, -32767, 32767)),
                    static_cast<int16_t>(std::clamp(dy, -32767, 32767)));
            }
            return;
        }

        HostSystemAgent::SyncThreadDesktopToInput(&m_LastInputDesk);

        INPUT input{};
        input.type = INPUT_MOUSE;
        double targetScreenX = 0.0;
        double targetScreenY = 0.0;

        if (pkt.isAbsolute)
        {
            RECT disp{};
            {
                std::lock_guard<std::mutex> lock(m_RectMutex);
                disp = m_DisplayRect;
            }
            int dispW = std::max(1L, disp.right - disp.left);
            int dispH = std::max(1L, disp.bottom - disp.top);
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = std::max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
            int vh = std::max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

            double nx = std::clamp(static_cast<double>(pkt.xOrDeltaX), 0.0, 1.0);
            double ny = std::clamp(static_cast<double>(pkt.yOrDeltaY), 0.0, 1.0);

            targetScreenX = disp.left + nx * static_cast<double>(std::max(1, dispW - 1));
            targetScreenY = disp.top + ny * static_cast<double>(std::max(1, dispH - 1));

            const bool multiMon = (vx != 0 || vy != 0 || vw != dispW || vh != dispH || disp.left != 0 || disp.top != 0);
            if (multiMon)
            {
                LONG absX = static_cast<LONG>(std::lround(
                    ((targetScreenX - vx) * 65535.0) / static_cast<double>(std::max(1, vw - 1))));
                LONG absY = static_cast<LONG>(std::lround(
                    ((targetScreenY - vy) * 65535.0) / static_cast<double>(std::max(1, vh - 1))));
                input.mi.dx = std::clamp(absX, 0L, 65535L);
                input.mi.dy = std::clamp(absY, 0L, 65535L);
                input.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            }
            else
            {
                LONG absX = static_cast<LONG>(std::lround(
                    (targetScreenX * 65535.0) / static_cast<double>(std::max(1, dispW - 1))));
                LONG absY = static_cast<LONG>(std::lround(
                    (targetScreenY * 65535.0) / static_cast<double>(std::max(1, dispH - 1))));
                input.mi.dx = std::clamp(absX, 0L, 65535L);
                input.mi.dy = std::clamp(absY, 0L, 65535L);
                input.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
            }
        }
        else
        {
            input.mi.dx = static_cast<LONG>(std::lround(pkt.xOrDeltaX));
            input.mi.dy = static_cast<LONG>(std::lround(pkt.yOrDeltaY));
            input.mi.dwFlags = MOUSEEVENTF_MOVE;
        }

        UINT sent = SendInput(1, &input, sizeof(INPUT));
        if (sent == 0)
        {
            DWORD err = GetLastError();
            if (!m_LoggedSendErr)
            {
                m_LoggedSendErr = true;
                OSM_LOG_WARN("Host SendInput(mouse move) failed (err=%lu, abs=%d)", err, pkt.isAbsolute ? 1 : 0);
            }
            if (pkt.isAbsolute)
            {
                SetCursorPos(static_cast<int>(std::lround(targetScreenX)),
                             static_cast<int>(std::lround(targetScreenY)));
            }
        }
        else if (!m_LoggedFirstMove)
        {
            m_LoggedFirstMove = true;
            OSM_LOG_INFO("Host mouse input active (abs=%d)", pkt.isAbsolute ? 1 : 0);
        }
    }

    void HostInputInjector::HandleMouseButton(const proto::InputMouseButtonPayload& pkt)
    {
        if (m_UseVirtualMouse.load() && m_VirtualDevices && m_VirtualDevices->HasActiveHidConsumer())
        {
            uint8_t bit = 0;
            switch (pkt.button)
            {
            case 1: bit = 0x01;
                break;
            case 3: bit = 0x02;
                break;
            case 2: bit = 0x04;
                break;
            case 4: bit = 0x08;
                break;
            case 5: bit = 0x10;
                break;
            default: break;
            }
            if (bit != 0)
            {
                m_VirtualDevices->PushMouseButton(bit, pkt.isDown != 0);
                return;
            }
        }

        HostSystemAgent::SyncThreadDesktopToInput(&m_LastInputDesk);

        INPUT input{};
        input.type = INPUT_MOUSE;

        switch (pkt.button)
        {
        case 1: input.mi.dwFlags = pkt.isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
            break;
        case 2: input.mi.dwFlags = pkt.isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
            break;
        case 3: input.mi.dwFlags = pkt.isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
            break;
        case 4: input.mi.dwFlags = pkt.isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
            input.mi.mouseData = XBUTTON1;
            break;
        case 5: input.mi.dwFlags = pkt.isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
            input.mi.mouseData = XBUTTON2;
            break;
        default: return;
        }

        if (SendInput(1, &input, sizeof(INPUT)) == 0 && !m_LoggedSendErr)
        {
            m_LoggedSendErr = true;
            OSM_LOG_WARN("Host SendInput(mouse button %u) failed (err=%lu)", pkt.button, GetLastError());
        }
    }

    void HostInputInjector::HandleMouseWheel(const proto::InputMouseWheelPayload& pkt)
    {
        if (m_UseVirtualMouse.load() && m_VirtualDevices && m_VirtualDevices->HasActiveHidConsumer())
        {
            m_WheelAccumY += static_cast<int32_t>(pkt.deltaY);
            int32_t stepsY = m_WheelAccumY / WHEEL_DELTA;
            m_WheelAccumY -= stepsY * WHEEL_DELTA;

            m_WheelAccumX += static_cast<int32_t>(pkt.deltaX);
            int32_t stepsX = m_WheelAccumX / WHEEL_DELTA;
            m_WheelAccumX -= stepsX * WHEEL_DELTA;

            if (stepsY != 0 || stepsX != 0)
            {
                m_VirtualDevices->PushMouseWheel(
                    static_cast<int8_t>(std::clamp<int32_t>(stepsY, -127, 127)),
                    static_cast<int8_t>(std::clamp<int32_t>(stepsX, -127, 127)));
            }
            return;
        }

        HostSystemAgent::SyncThreadDesktopToInput(&m_LastInputDesk);

        if (pkt.deltaY != 0)
        {
            INPUT input{};
            input.type = INPUT_MOUSE;
            input.mi.dwFlags = MOUSEEVENTF_WHEEL;
            input.mi.mouseData = static_cast<DWORD>(static_cast<int32_t>(pkt.deltaY));
            SendInput(1, &input, sizeof(INPUT));
        }
        if (pkt.deltaX != 0)
        {
            INPUT input{};
            input.type = INPUT_MOUSE;
            input.mi.dwFlags = MOUSEEVENTF_HWHEEL;
            input.mi.mouseData = static_cast<DWORD>(static_cast<int32_t>(pkt.deltaX));
            SendInput(1, &input, sizeof(INPUT));
        }
    }

    void HostInputInjector::HandleUnicodeText(const uint8_t* data, size_t len)
    {
        if (len <= sizeof(proto::AppPacketHeader)) return;
        HostSystemAgent::SyncThreadDesktopToInput(&m_LastInputDesk);

        const char* utf8 = reinterpret_cast<const char*>(data + sizeof(proto::AppPacketHeader));
        int utf8Len = static_cast<int>(len - sizeof(proto::AppPacketHeader));

        int wlen = MultiByteToWideChar(CP_UTF8, 0, utf8, utf8Len, nullptr, 0);
        if (wlen <= 0) return;
        std::wstring wide(static_cast<size_t>(wlen), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, utf8, utf8Len, wide.data(), wlen);

        std::vector<INPUT> inputs;
        inputs.reserve(wide.size() * 2);
        for (wchar_t ch : wide)
        {
            INPUT down{};
            down.type = INPUT_KEYBOARD;
            down.ki.wScan = ch;
            down.ki.dwFlags = KEYEVENTF_UNICODE;
            inputs.push_back(down);

            INPUT up = down;
            up.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            inputs.push_back(up);
        }

        if (!inputs.empty())
        {
            SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT));
        }
    }
}
