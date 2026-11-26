#pragma once

#include <atomic>
#include <string>
#include <thread>

namespace osmium
{
    class HostPrivacyScreen
    {
    public:
        ~HostPrivacyScreen();

        bool Show(std::wstring wallpaperPath, const std::atomic<bool>* sessionEnded = nullptr);
        void Hide();
        bool IsActive() const { return m_Active.load(); }
        const std::atomic<bool>& ActiveFlag() const { return m_Active; }

    private:
        std::thread m_Thread;
        unsigned long m_ThreadId = 0;
        std::atomic<bool> m_Active{false};
    };
}
