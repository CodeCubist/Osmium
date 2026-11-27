#pragma once

#include "OsmiumCommon.h"
#include <atomic>
#include <condition_variable>
#include <functional>
#include <mutex>
#include <thread>

namespace osmium
{
    // Watches which control has keyboard focus on the interactive desktop and
    // reports whether it takes text, with the caret normalized to the captured
    // display, so a touch client can raise its keyboard and keep the caret in
    // view. Runs on its own MTA thread: a hung app stalls UI Automation, never
    // the video or input paths.
    class HostTextFocus
    {
    public:
        struct State
        {
            bool active = false;
            bool hasRect = false;
            uint16_t serial = 0;
            float x = 0.0f;
            float y = 0.0f;
            float w = 0.0f;
            float h = 0.0f;
        };

        using Sink = std::function<void(const State&)>;

        HostTextFocus() = default;
        ~HostTextFocus();

        HostTextFocus(const HostTextFocus&) = delete;
        HostTextFocus& operator=(const HostTextFocus&) = delete;

        // Reports the current state at once, then on every change and once a
        // second so a lost packet heals itself.
        void Start(Sink sink);
        void RequestStop();
        void Stop();
        void SetDisplayRect(RECT rect);

    private:
        void Run();

        std::thread m_Thread;
        std::mutex m_ControlMutex;
        std::mutex m_Mutex;
        std::condition_variable m_Cv;
        bool m_Stop = false;
        RECT m_DisplayRect{0, 0, 1920, 1080};
        Sink m_Sink;
    };
}
