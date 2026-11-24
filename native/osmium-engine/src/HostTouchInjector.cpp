#include "HostTouchInjector.h"
#include "HostSystemAgent.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <utility>

namespace osmium
{
    namespace
    {
        // Windows cancels injected contacts that are not refreshed within about
        // a second, so a resting finger is re-injected unchanged this often.
        constexpr uint64_t kRefreshMs = 50;
        // No frame from the client for this long while fingers are down means
        // the lift was lost or the client vanished: cancel rather than leave a
        // stuck press. Clients resend the current frame every 250 ms.
        constexpr uint64_t kClientTimeoutMs = 1500;
        // A gap this long ends any reordering window, so the next frame is
        // accepted whatever its sequence (new session, restarted client).
        constexpr uint64_t kSeqResetMs = 1000;
        constexpr size_t kMaxQueuedFrames = 128;
        constexpr int kPointerFeedbackDefault = 1;

        constexpr POINTER_FLAGS kFlagsDown = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
        constexpr POINTER_FLAGS kFlagsUpdate = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
        constexpr POINTER_FLAGS kFlagsUp = POINTER_FLAG_UP;
        constexpr POINTER_FLAGS kFlagsCancel = POINTER_FLAG_UP | POINTER_FLAG_CANCELED;

        using CreateSyntheticPointerDeviceFn = HANDLE(WINAPI*)(POINTER_INPUT_TYPE, ULONG, int);
        using InjectSyntheticPointerInputFn = BOOL(WINAPI*)(HANDLE, const POINTER_TYPE_INFO*, UINT32);
        using DestroySyntheticPointerDeviceFn = void(WINAPI*)(HANDLE);
        using InitializeTouchInjectionFn = BOOL(WINAPI*)(UINT32, DWORD);
        using InjectTouchInputFn = BOOL(WINAPI*)(UINT32, const POINTER_TOUCH_INFO*);
        using SetThreadDpiAwarenessContextFn = DPI_AWARENESS_CONTEXT(WINAPI*)(DPI_AWARENESS_CONTEXT);

        // Resolved at runtime so the engine still loads where the synthetic
        // pointer API (1809) or touch injection (Windows 8) is missing.
        struct TouchApi
        {
            CreateSyntheticPointerDeviceFn create = nullptr;
            InjectSyntheticPointerInputFn inject = nullptr;
            DestroySyntheticPointerDeviceFn destroy = nullptr;
            InitializeTouchInjectionFn legacyInit = nullptr;
            InjectTouchInputFn legacyInject = nullptr;
            SetThreadDpiAwarenessContextFn setThreadDpi = nullptr;

            bool HasSynthetic() const { return create && inject && destroy; }
            bool HasLegacy() const { return legacyInit && legacyInject; }
        };

        template <typename T>
        T Resolve(HMODULE module, const char* name)
        {
            return reinterpret_cast<T>(reinterpret_cast<void*>(GetProcAddress(module, name)));
        }

        const TouchApi& Api()
        {
            static const TouchApi api = []()
            {
                TouchApi a;
                HMODULE user32 = GetModuleHandleW(L"user32.dll");
                if (!user32) user32 = LoadLibraryW(L"user32.dll");
                if (!user32) return a;
                a.create = Resolve<CreateSyntheticPointerDeviceFn>(user32, "CreateSyntheticPointerDevice");
                a.inject = Resolve<InjectSyntheticPointerInputFn>(user32, "InjectSyntheticPointerInput");
                a.destroy = Resolve<DestroySyntheticPointerDeviceFn>(user32, "DestroySyntheticPointerDevice");
                a.legacyInit = Resolve<InitializeTouchInjectionFn>(user32, "InitializeTouchInjection");
                a.legacyInject = Resolve<InjectTouchInputFn>(user32, "InjectTouchInput");
                a.setThreadDpi = Resolve<SetThreadDpiAwarenessContextFn>(user32, "SetThreadDpiAwarenessContext");
                return a;
            }();
            return api;
        }

        bool SamePoint(POINT a, POINT b)
        {
            return a.x == b.x && a.y == b.y;
        }
    }

    HostTouchInjector::HostTouchInjector() = default;

    HostTouchInjector::~HostTouchInjector()
    {
        {
            std::lock_guard<std::mutex> lock(m_Mutex);
            m_Stop = true;
        }
        m_Cv.notify_one();
        if (m_Thread.joinable())
        {
            m_Thread.join();
        }
    }

    bool HostTouchInjector::IsSupported()
    {
        return Api().HasSynthetic() || Api().HasLegacy();
    }

    void HostTouchInjector::SetTargetDisplayRect(RECT rect)
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        m_DisplayRect = rect;
    }

    void HostTouchInjector::Submit(const uint8_t* data, size_t len)
    {
        if (!data || len < proto::kInputTouchHeaderSize) return;

        Frame frame;
        uint16_t seq = 0;
        frame.count = std::min(data[offsetof(proto::InputTouchPayload, count)], proto::kMaxTouchContacts);
        memcpy(&seq, data + offsetof(proto::InputTouchPayload, seq), sizeof(seq));
        const size_t contactBytes = frame.count * sizeof(proto::InputTouchContact);
        if (len < proto::kInputTouchHeaderSize + contactBytes) return;
        memcpy(frame.contacts, data + proto::kInputTouchHeaderSize, contactBytes);

        std::lock_guard<std::mutex> lock(m_Mutex);
        const uint64_t nowMs = NowMillis();
        const bool fresh = m_LastClientFrameMs == 0 || nowMs - m_LastClientFrameMs > kSeqResetMs;
        if (!fresh && static_cast<int16_t>(seq - m_LastSeq) <= 0) return;
        m_LastSeq = seq;
        m_LastClientFrameMs = nowMs;

        if (m_Queue.size() >= kMaxQueuedFrames) m_Queue.pop_front();
        m_Queue.push_back(frame);
        if (!m_Thread.joinable())
        {
            m_Thread = std::thread(&HostTouchInjector::Run, this);
        }
        m_Cv.notify_one();
    }

    void HostTouchInjector::ReleaseAll()
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        if (!m_Thread.joinable()) return;
        m_Queue.clear();
        m_ReleaseRequested = true;
        m_LastClientFrameMs = 0;
        m_Cv.notify_one();
    }

    void HostTouchInjector::Run()
    {
        // Pixel coordinates must be physical on every monitor.
        if (Api().setThreadDpi)
        {
            Api().setThreadDpi(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }

        std::unique_lock<std::mutex> lock(m_Mutex);
        while (!m_Stop)
        {
            auto ready = [&]() { return m_Stop || m_ReleaseRequested || !m_Queue.empty(); };
            if (m_Contacts.empty())
            {
                m_Cv.wait(lock, ready);
            }
            else
            {
                m_Cv.wait_for(lock, std::chrono::milliseconds(kRefreshMs), ready);
            }
            if (m_Stop) break;

            std::deque<Frame> frames;
            frames.swap(m_Queue);
            const bool release = std::exchange(m_ReleaseRequested, false);
            const uint64_t lastClientMs = m_LastClientFrameMs;
            m_Rect = m_DisplayRect;
            lock.unlock();

            for (const auto& frame : frames)
            {
                Apply(frame, true);
            }
            if (release)
            {
                CancelAll();
                DestroyDevice();
            }
            else if (!m_Contacts.empty())
            {
                const uint64_t nowMs = NowMillis();
                if (nowMs - lastClientMs > kClientTimeoutMs)
                {
                    OSM_LOG_WARN("Host touch: no frame from client for %llu ms, cancelling %zu contact(s)",
                                 nowMs - lastClientMs, m_Contacts.size());
                    CancelAll();
                }
                else if (nowMs - m_LastInjectMs >= kRefreshMs)
                {
                    Refresh();
                }
            }

            lock.lock();
        }
        lock.unlock();

        CancelAll();
        DestroyDevice();
        if (m_Desk)
        {
            CloseDesktop(m_Desk);
            m_Desk = nullptr;
        }
    }

    void HostTouchInjector::Apply(const Frame& frame, bool allowRetry)
    {
        if (!EnsureDevice()) return;

        auto find = [&](uint8_t id) -> const proto::InputTouchContact*
        {
            for (uint8_t i = 0; i < frame.count; ++i)
            {
                if (frame.contacts[i].id == id) return &frame.contacts[i];
            }
            return nullptr;
        };
        auto phaseOf = [](const proto::InputTouchContact& e)
        {
            return static_cast<proto::TouchPhase>(e.phase);
        };

        // A lift must land where the previous update did, so a finger that
        // moved since gets one more update first.
        bool liftMoved = false;
        for (auto& c : m_Contacts)
        {
            const auto* e = find(c.clientId);
            if (e && phaseOf(*e) == proto::TouchPhase::Up)
            {
                const POINT pt = MapToScreen(e->x, e->y);
                if (!SamePoint(pt, c.pt))
                {
                    c.pt = pt;
                    liftMoved = true;
                }
            }
        }
        if (liftMoved)
        {
            std::vector<POINTER_TOUCH_INFO> pre;
            pre.reserve(m_Contacts.size());
            for (const auto& c : m_Contacts)
            {
                pre.push_back(MakeInfo(c, kFlagsUpdate));
            }
            Inject(pre);
        }

        std::vector<POINTER_TOUCH_INFO> infos;
        std::vector<Contact> next;
        uint32_t usedIds = 0;
        for (const auto& c : m_Contacts)
        {
            usedIds |= 1u << c.pointerId;
            const auto* e = find(c.clientId);
            const auto phase = e ? phaseOf(*e) : proto::TouchPhase::Up;
            if (phase == proto::TouchPhase::Up)
            {
                infos.push_back(MakeInfo(c, kFlagsUp));
            }
            else if (phase == proto::TouchPhase::Cancel)
            {
                infos.push_back(MakeInfo(c, kFlagsCancel));
            }
            else
            {
                Contact moved = c;
                moved.pt = MapToScreen(e->x, e->y);
                infos.push_back(MakeInfo(moved, kFlagsUpdate));
                next.push_back(moved);
            }
        }

        for (uint8_t i = 0; i < frame.count; ++i)
        {
            const auto& e = frame.contacts[i];
            const auto phase = phaseOf(e);
            if (phase != proto::TouchPhase::Down && phase != proto::TouchPhase::Move) continue;
            auto sameId = [&](const Contact& c) { return c.clientId == e.id; };
            if (std::any_of(m_Contacts.begin(), m_Contacts.end(), sameId) ||
                std::any_of(next.begin(), next.end(), sameId))
            {
                continue;
            }

            // Lifted pointers keep their id until this frame is injected; with
            // all ids taken the newcomer goes down with the next frame.
            uint32_t pointerId = 0;
            while (pointerId < proto::kMaxTouchContacts && (usedIds & (1u << pointerId))) ++pointerId;
            if (pointerId >= proto::kMaxTouchContacts) continue;
            usedIds |= 1u << pointerId;

            Contact c{e.id, pointerId, MapToScreen(e.x, e.y)};
            infos.push_back(MakeInfo(c, kFlagsDown));
            next.push_back(c);
        }

        if (!infos.empty() && !Inject(infos))
        {
            // A rejected frame cancels every injected contact, so start over
            // with whatever is still down.
            const bool hadContacts = !m_Contacts.empty();
            m_Contacts.clear();
            if (allowRetry && hadContacts)
            {
                Apply(frame, false);
            }
            return;
        }
        m_Contacts = std::move(next);
    }

    void HostTouchInjector::Refresh()
    {
        std::vector<POINTER_TOUCH_INFO> infos;
        infos.reserve(m_Contacts.size());
        for (const auto& c : m_Contacts)
        {
            infos.push_back(MakeInfo(c, kFlagsUpdate));
        }
        if (!Inject(infos))
        {
            m_Contacts.clear();
        }
    }

    void HostTouchInjector::CancelAll()
    {
        if (m_Contacts.empty()) return;
        std::vector<POINTER_TOUCH_INFO> infos;
        infos.reserve(m_Contacts.size());
        for (const auto& c : m_Contacts)
        {
            infos.push_back(MakeInfo(c, kFlagsCancel));
        }
        Inject(infos);
        m_Contacts.clear();
    }

    bool HostTouchInjector::EnsureDevice()
    {
        const auto& api = Api();
        if (m_Device) return true;
        if (api.HasSynthetic() && !m_SyntheticFailed)
        {
            m_Device = api.create(PT_TOUCH, proto::kMaxTouchContacts, kPointerFeedbackDefault);
            if (m_Device)
            {
                OSM_LOG_INFO("Host touch input active (synthetic pointer device)");
                return true;
            }
            m_SyntheticFailed = true;
            OSM_LOG_WARN("CreateSyntheticPointerDevice failed (err=%lu), using InjectTouchInput", GetLastError());
        }
        if (!api.HasLegacy()) return false;
        if (!m_LegacyInitialized)
        {
            m_LegacyInitialized = api.legacyInit(proto::kMaxTouchContacts, TOUCH_FEEDBACK_DEFAULT) != FALSE;
            if (m_LegacyInitialized)
            {
                OSM_LOG_INFO("Host touch input active (InjectTouchInput)");
            }
            else if (!m_LoggedFail)
            {
                m_LoggedFail = true;
                OSM_LOG_WARN("InitializeTouchInjection failed (err=%lu)", GetLastError());
            }
        }
        return m_LegacyInitialized;
    }

    void HostTouchInjector::DestroyDevice()
    {
        // The device shows up as a touchscreen while it exists; drop it with
        // the session so the host does not look touch-capable afterwards.
        if (m_Device)
        {
            Api().destroy(m_Device);
            m_Device = nullptr;
        }
    }

    bool HostTouchInjector::Inject(const std::vector<POINTER_TOUCH_INFO>& infos)
    {
        if (infos.empty()) return true;
        const auto& api = Api();

        auto attempt = [&]() -> bool
        {
            if (m_Device)
            {
                // Synthetic pointer locations are relative to the virtual
                // screen's top-left, not to the primary monitor.
                const LONG vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
                const LONG vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
                std::vector<POINTER_TYPE_INFO> typed(infos.size());
                for (size_t i = 0; i < infos.size(); ++i)
                {
                    typed[i].type = PT_TOUCH;
                    typed[i].touchInfo = infos[i];
                    auto& t = typed[i].touchInfo;
                    t.pointerInfo.ptPixelLocation.x -= vx;
                    t.pointerInfo.ptPixelLocation.y -= vy;
                    if (t.touchMask & TOUCH_MASK_CONTACTAREA)
                    {
                        OffsetRect(&t.rcContact, -vx, -vy);
                    }
                }
                return api.inject(m_Device, typed.data(), static_cast<UINT32>(typed.size())) != FALSE;
            }
            return api.legacyInject(static_cast<UINT32>(infos.size()), infos.data()) != FALSE;
        };

        HostSystemAgent::SyncThreadDesktopToInput(&m_Desk);
        bool resynced = false;
        for (int i = 0; i < 4; ++i)
        {
            if (attempt())
            {
                m_LastInjectMs = NowMillis();
                m_LoggedFail = false;
                return true;
            }
            const DWORD err = GetLastError();
            if (err == ERROR_NOT_READY)
            {
                // Back-to-back injections closer than ~0.1 ms are refused.
                Sleep(1);
                continue;
            }
            if (!resynced)
            {
                resynced = true;
                if (m_Desk)
                {
                    CloseDesktop(m_Desk);
                    m_Desk = nullptr;
                }
                HostSystemAgent::SyncThreadDesktopToInput(&m_Desk);
                continue;
            }
            if (!m_LoggedFail)
            {
                m_LoggedFail = true;
                OSM_LOG_WARN("Host touch injection failed (err=%lu, contacts=%zu)", err, infos.size());
            }
            break;
        }
        return false;
    }

    POINT HostTouchInjector::MapToScreen(float nx, float ny) const
    {
        const LONG w = std::max(1L, m_Rect.right - m_Rect.left);
        const LONG h = std::max(1L, m_Rect.bottom - m_Rect.top);
        const double x = std::clamp(static_cast<double>(nx), 0.0, 1.0);
        const double y = std::clamp(static_cast<double>(ny), 0.0, 1.0);
        POINT pt;
        pt.x = m_Rect.left + static_cast<LONG>(std::lround(x * static_cast<double>(w - 1)));
        pt.y = m_Rect.top + static_cast<LONG>(std::lround(y * static_cast<double>(h - 1)));
        return pt;
    }

    POINTER_TOUCH_INFO HostTouchInjector::MakeInfo(const Contact& c, POINTER_FLAGS flags) const
    {
        POINTER_TOUCH_INFO info{};
        info.pointerInfo.pointerType = PT_TOUCH;
        info.pointerInfo.pointerId = c.pointerId;
        info.pointerInfo.pointerFlags = flags;
        info.pointerInfo.ptPixelLocation = c.pt;
        info.touchFlags = TOUCH_FLAG_NONE;
        if (flags & POINTER_FLAG_INCONTACT)
        {
            info.touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_PRESSURE;
            info.rcContact.left = std::max(c.pt.x - 2, m_Rect.left);
            info.rcContact.top = std::max(c.pt.y - 2, m_Rect.top);
            info.rcContact.right = std::min(c.pt.x + 2, m_Rect.right - 1);
            info.rcContact.bottom = std::min(c.pt.y + 2, m_Rect.bottom - 1);
            info.pressure = 512;
        }
        return info;
    }
}
