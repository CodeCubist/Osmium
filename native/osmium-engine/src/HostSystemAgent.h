#pragma once

#include "OsmiumCommon.h"
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

        void ResetOnDisconnect();

        static int RunSystemServiceMain();
        static int RunOneShotSasServiceMain();

    private:
        bool EnsureSoftwareSasPolicyEnabled();
        void RestoreSoftwareSasPolicy();
        bool InvokeSasViaLocalSystemHelper();

        bool m_ModifiedSasReg = false;
        bool m_HadOrigSasVal = false;
        DWORD m_OrigSasVal = 0;
    };
}
