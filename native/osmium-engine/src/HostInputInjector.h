#pragma once

#include "OsmiumCommon.h"
#include "OsmiumProtocol.h"
#include <atomic>
#include <mutex>

namespace osmium
{
    class UsbIpVirtualDevices;

    class HostInputInjector
    {
    public:
        HostInputInjector() = default;

        void SetTargetDisplayRect(RECT rect);
        void SetEnabled(bool enabled) { m_Enabled = enabled; }
        void SetVirtualDevices(UsbIpVirtualDevices* virtualDevices) { m_VirtualDevices = virtualDevices; }
        void SetVirtualMousePreferred(bool preferred) { m_UseVirtualMouse.store(preferred); }

        void InjectPacket(const uint8_t* data, size_t len);
        void ReleaseAllInputs();

    private:
        void HandleKey(const proto::InputKeyPayload& pkt);
        void HandleMouseMove(const proto::InputMouseMovePayload& pkt);
        void HandleMouseButton(const proto::InputMouseButtonPayload& pkt);
        void HandleMouseWheel(const proto::InputMouseWheelPayload& pkt);
        void HandleUnicodeText(const uint8_t* data, size_t len);

        RECT m_DisplayRect{0, 0, 1920, 1080};
        std::mutex m_RectMutex;
        std::atomic<bool> m_Enabled{true};
        std::atomic<bool> m_UseVirtualMouse{false};
        UsbIpVirtualDevices* m_VirtualDevices = nullptr;
        HDESK m_LastInputDesk = nullptr;
        int32_t m_WheelAccumX = 0;
        int32_t m_WheelAccumY = 0;
        bool m_LoggedFirstMove = false;
        bool m_LoggedSendErr = false;
    };
}
