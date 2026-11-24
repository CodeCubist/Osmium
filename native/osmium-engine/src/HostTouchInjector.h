#pragma once

#include "OsmiumCommon.h"
#include "OsmiumProtocol.h"
#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

namespace osmium
{
    // Replays client touch frames as real Windows touch input: a synthetic
    // PT_TOUCH pointer device (Win10 1809+), or InjectTouchInput before that.
    // All injection happens on one plain thread, so it can follow the input
    // desktop (UAC / lock screen) and run per-monitor DPI aware.
    class HostTouchInjector
    {
    public:
        HostTouchInjector();
        ~HostTouchInjector();

        HostTouchInjector(const HostTouchInjector&) = delete;
        HostTouchInjector& operator=(const HostTouchInjector&) = delete;

        static bool IsSupported();

        void SetTargetDisplayRect(RECT rect);
        void Submit(const uint8_t* data, size_t len);
        void ReleaseAll();

    private:
        struct Contact
        {
            uint8_t clientId;
            uint32_t pointerId;
            POINT pt;
        };

        struct Frame
        {
            uint8_t count = 0;
            proto::InputTouchContact contacts[proto::kMaxTouchContacts]{};
        };

        void Run();
        void Apply(const Frame& frame, bool allowRetry);
        void Refresh();
        void CancelAll();
        bool EnsureDevice();
        void DestroyDevice();
        bool Inject(const std::vector<POINTER_TOUCH_INFO>& infos);
        POINT MapToScreen(float nx, float ny) const;
        POINTER_TOUCH_INFO MakeInfo(const Contact& c, POINTER_FLAGS flags) const;

        std::thread m_Thread;
        std::mutex m_Mutex;
        std::condition_variable m_Cv;
        std::deque<Frame> m_Queue;
        bool m_Stop = false;
        bool m_ReleaseRequested = false;
        RECT m_DisplayRect{0, 0, 1920, 1080};
        uint16_t m_LastSeq = 0;
        uint64_t m_LastClientFrameMs = 0;

        // Touch thread only.
        RECT m_Rect{0, 0, 1920, 1080};
        std::vector<Contact> m_Contacts;
        uint64_t m_LastInjectMs = 0;
        HANDLE m_Device = nullptr;
        bool m_SyntheticFailed = false;
        bool m_LegacyInitialized = false;
        HDESK m_Desk = nullptr;
        bool m_LoggedFail = false;
        bool m_LoggedActive = false;
    };
}
