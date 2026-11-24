#include "HostInputInjector.h"
#include "HostSystemAgent.h"
#include "DriverSetupHelper.h"
#include "UsbIpVirtualDevices.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

namespace osmium
{
    HostInputInjector::HostInputInjector(bool allowAgentForwarding)
        : m_AllowAgentForwarding(allowAgentForwarding)
    {
    }

    HostInputInjector::~HostInputInjector()
    {
        {
            std::lock_guard<std::mutex> lock(m_AgentMutex);
            CloseAgentPipeLocked();
        }
        if (m_LastInputDesk)
        {
            CloseDesktop(m_LastInputDesk);
            m_LastInputDesk = nullptr;
        }
    }

    void HostInputInjector::SetTargetDisplayRect(RECT rect)
    {
        {
            std::lock_guard<std::mutex> lock(m_RectMutex);
            m_DisplayRect = rect;
            if (m_Touch) m_Touch->SetTargetDisplayRect(rect);
        }
        m_AgentRectDirty.store(true);
    }

    bool HostInputInjector::WillUseVirtualMouse(const uint8_t* data, size_t len) const
    {
        if (!m_UseVirtualMouse.load() || !m_VirtualDevices || !m_VirtualDevices->HasActiveHidConsumer())
        {
            return false;
        }
        if (!data || len < sizeof(proto::AppPacketHeader))
        {
            return false;
        }

        const auto* hdr = reinterpret_cast<const proto::AppPacketHeader*>(data);
        switch (static_cast<proto::AppPacketType>(hdr->type))
        {
        case proto::AppPacketType::InputMouseMove:
            if (len >= sizeof(proto::InputMouseMovePayload))
            {
                const auto* pkt = reinterpret_cast<const proto::InputMouseMovePayload*>(data);
                return !pkt->isAbsolute;
            }
            return false;
        case proto::AppPacketType::InputMouseButton:
            return len >= sizeof(proto::InputMouseButtonPayload);
        case proto::AppPacketType::InputMouseWheel:
            return len >= sizeof(proto::InputMouseWheelPayload);
        default:
            return false;
        }
    }

    void HostInputInjector::CloseAgentPipeLocked()
    {
        if (m_AgentPipe != INVALID_HANDLE_VALUE)
        {
            CloseHandle(m_AgentPipe);
            m_AgentPipe = INVALID_HANDLE_VALUE;
        }
    }

    bool HostInputInjector::EnsureAgentConnectedLocked()
    {
        if (!m_AllowAgentForwarding)
        {
            return false;
        }

        static const bool s_RunningAsSystem = DriverSetupHelper::IsRunningAsSystem();
        if (s_RunningAsSystem)
        {
            return false;
        }

        if (m_AgentPipe != INVALID_HANDLE_VALUE)
        {
            return true;
        }

        const uint64_t nowMs = NowMillis();
        if (m_LastAgentProbeMs != 0 && (nowMs - m_LastAgentProbeMs) < 1500)
        {
            return false;
        }
        m_LastAgentProbeMs = nowMs;

        DWORD mySid = 0;
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &mySid))
        {
            return false;
        }

        std::wstring pipeName = HostSystemAgent::GetInputAgentPipeName(mySid);
        auto tryOpenPipe = [&]() -> HANDLE
        {
            HANDLE h = CreateFileW(
                pipeName.c_str(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                nullptr,
                OPEN_EXISTING,
                0,
                nullptr);
            if (h == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY)
            {
                if (WaitNamedPipeW(pipeName.c_str(), 150))
                {
                    h = CreateFileW(
                        pipeName.c_str(),
                        GENERIC_READ | GENERIC_WRITE,
                        0,
                        nullptr,
                        OPEN_EXISTING,
                        0,
                        nullptr);
                }
            }
            return h;
        };

        HANDLE h = tryOpenPipe();
        if (h == INVALID_HANDLE_VALUE)
        {
            if (HostSystemAgent::RequestServiceEnsureInputAgent(1500))
            {
                h = tryOpenPipe();
            }
        }

        if (h == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        DWORD mode = PIPE_READMODE_MESSAGE;
        SetNamedPipeHandleState(h, &mode, nullptr, nullptr);
        m_AgentPipe = h;
        m_AgentRectDirty.store(true);
        m_LoggedAgentErr = false;
        OSM_LOG_INFO("HostInputInjector connected to SYSTEM input agent (session=%lu)", mySid);
        return true;
    }

    bool HostInputInjector::SendAgentMessageLocked(AgentInputOp op, const void* payload, size_t payloadLen)
    {
        if (m_AgentPipe == INVALID_HANDLE_VALUE || payloadLen > 65536)
        {
            return false;
        }

        const size_t totalLen = sizeof(AgentInputMsgHeader) + payloadLen;
        AgentInputMsgHeader hdr{};
        hdr.magic = kAgentInputMagic;
        hdr.op = static_cast<uint8_t>(op);
        hdr.payloadLen = static_cast<uint32_t>(payloadLen);

        uint8_t stackBuf[512];
        std::vector<uint8_t> heapBuf;
        uint8_t* buf = stackBuf;
        if (totalLen > sizeof(stackBuf))
        {
            heapBuf.resize(totalLen);
            buf = heapBuf.data();
        }

        memcpy(buf, &hdr, sizeof(hdr));
        if (payloadLen > 0 && payload)
        {
            memcpy(buf + sizeof(hdr), payload, payloadLen);
        }

        DWORD written = 0;
        BOOL ok = WriteFile(m_AgentPipe, buf, static_cast<DWORD>(totalLen), &written, nullptr);
        if (!ok || written != static_cast<DWORD>(totalLen))
        {
            DWORD err = GetLastError();
            CloseAgentPipeLocked();
            if (!m_LoggedAgentErr)
            {
                m_LoggedAgentErr = true;
                OSM_LOG_WARN("HostInputInjector write to SYSTEM input agent failed (err=%lu), falling back locally", err);
            }
            const uint64_t nowMs = NowMillis();
            m_LastAgentProbeMs = (nowMs > 1200) ? (nowMs - 1200) : 0;
            return false;
        }
        return true;
    }

    bool HostInputInjector::TryForwardPacketToAgent(const uint8_t* data, size_t len)
    {
        std::lock_guard<std::mutex> lock(m_AgentMutex);
        if (!EnsureAgentConnectedLocked())
        {
            return false;
        }

        if (m_AgentRectDirty.exchange(false))
        {
            RECT rect{};
            {
                std::lock_guard<std::mutex> rlock(m_RectMutex);
                rect = m_DisplayRect;
            }
            if (!SendAgentMessageLocked(AgentInputOp::SetDisplayRect, &rect, sizeof(rect)))
            {
                m_AgentRectDirty.store(true);
                return false;
            }
        }

        return SendAgentMessageLocked(AgentInputOp::InjectPacket, data, len);
    }

    bool HostInputInjector::TryForwardReleaseAllToAgent()
    {
        std::lock_guard<std::mutex> lock(m_AgentMutex);
        if (!EnsureAgentConnectedLocked())
        {
            return false;
        }
        return SendAgentMessageLocked(AgentInputOp::ReleaseAllInputs, nullptr, 0);
    }

    void HostInputInjector::InjectPacket(const uint8_t* data, size_t len)
    {
        if (!m_Enabled.load() || !data || len < sizeof(proto::AppPacketHeader)) return;

        const auto* hdr = reinterpret_cast<const proto::AppPacketHeader*>(data);
        const auto pktType = static_cast<proto::AppPacketType>(hdr->type);
        const bool isInputPkt =
            pktType == proto::AppPacketType::InputKey ||
            pktType == proto::AppPacketType::InputMouseMove ||
            pktType == proto::AppPacketType::InputMouseButton ||
            pktType == proto::AppPacketType::InputMouseWheel ||
            pktType == proto::AppPacketType::InputUnicodeText ||
            pktType == proto::AppPacketType::InputTouch;

        if (!isInputPkt) return;

        if (m_AllowAgentForwarding && !WillUseVirtualMouse(data, len))
        {
            if (TryForwardPacketToAgent(data, len))
            {
                return;
            }
        }

        switch (pktType)
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
        case proto::AppPacketType::InputTouch:
            HandleTouch(data, len);
            break;
        default:
            break;
        }
    }

    void HostInputInjector::ReleaseAllInputs()
    {
        {
            std::lock_guard<std::mutex> lock(m_RectMutex);
            if (m_Touch) m_Touch->ReleaseAll();
        }

        if (m_AllowAgentForwarding && TryForwardReleaseAllToAgent())
        {
            if (m_LastInputDesk)
            {
                CloseDesktop(m_LastInputDesk);
                m_LastInputDesk = nullptr;
            }
            return;
        }

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

    namespace
    {
        UINT SendInputWithDesktopRetry(INPUT& input, HDESK* lastDesk)
        {
            UINT sent = SendInput(1, &input, sizeof(INPUT));
            if (sent == 0)
            {
                if (lastDesk && *lastDesk)
                {
                    CloseDesktop(*lastDesk);
                    *lastDesk = nullptr;
                }
                HostSystemAgent::SyncThreadDesktopToInput(lastDesk);
                sent = SendInput(1, &input, sizeof(INPUT));
            }
            return sent;
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
        SendInputWithDesktopRetry(input, &m_LastInputDesk);
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

        if (SendInputWithDesktopRetry(input, &m_LastInputDesk) == 0 && !m_LoggedSendErr)
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
            SendInputWithDesktopRetry(input, &m_LastInputDesk);
        }
        if (pkt.deltaX != 0)
        {
            INPUT input{};
            input.type = INPUT_MOUSE;
            input.mi.dwFlags = MOUSEEVENTF_HWHEEL;
            input.mi.mouseData = static_cast<DWORD>(static_cast<int32_t>(pkt.deltaX));
            SendInputWithDesktopRetry(input, &m_LastInputDesk);
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

    void HostInputInjector::HandleTouch(const uint8_t* data, size_t len)
    {
        HostTouchInjector* touch = nullptr;
        {
            std::lock_guard<std::mutex> lock(m_RectMutex);
            if (!m_Touch)
            {
                if (!HostTouchInjector::IsSupported()) return;
                m_Touch = std::make_unique<HostTouchInjector>();
                m_Touch->SetTargetDisplayRect(m_DisplayRect);
            }
            touch = m_Touch.get();
        }
        touch->Submit(data, len);
    }
}
