#pragma once

#include "OsmiumCommon.h"
#include <atomic>
#include <mutex>
#include <string>

namespace osmium
{
    class HostSystemAgent
    {
    public:
        HostSystemAgent() = default;
        ~HostSystemAgent();

        static bool SyncThreadDesktopToInput(HDESK* inOutLastDesk = nullptr);
        static bool IsSessionLocked();
        static bool IsSmartFullscreenOrCursorLocked(RECT targetDisplayRect);

        bool SendCtrlAltDel();
        bool OpenTaskManager();

        void SetAutoUnlockCredential(bool enabled, const char* secretUtf8);
        void TickAutoUnlock();
        bool TriggerAutoUnlockNow();
        std::string GetLastUnlockStatus() const;

        void ResetOnDisconnect();

        static int RunSystemServiceMain();
        static int RunOneShotSasServiceMain();
        static int RunOneShotUnlockServiceMain(uint32_t parentPid);

    private:
        bool EnsureSoftwareSasPolicyEnabled();
        void RestoreSoftwareSasPolicy();
        bool InvokeSasViaLocalSystemHelper(const std::string* optionalUnlockSecret = nullptr);
        bool TypeUnlockSequenceOnCurrentDesktop(const std::string& secretUtf8);

        std::mutex m_Mutex;
        bool m_ModifiedSasReg = false;
        bool m_HadOrigSasVal = false;
        DWORD m_OrigSasVal = 0;

        bool m_AutoUnlockEnabled = false;
        std::string m_AutoUnlockSecret;
        bool m_UnlockAttemptedThisLock = false;
        bool m_WasLocked = false;
        std::string m_LastUnlockStatus;
    };
}
