#pragma once

#include "OsmiumCommon.h"
#include <cstdint>
#include <string>

namespace osmium
{
    constexpr uint32_t kAgentInputMagic = 0x494D534Fu; // 'OSMI'

    enum class AgentInputOp : uint8_t
    {
        Ping = 1,
        SetDisplayRect = 2,
        InjectPacket = 3,
        ReleaseAllInputs = 4
    };

#pragma pack(push, 1)
    struct AgentInputMsgHeader
    {
        uint32_t magic;
        uint8_t op;
        uint8_t reserved[3];
        uint32_t payloadLen;
    };
#pragma pack(pop)

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
        static int RunInputAgentMain();

        static bool InstallSystemService();
        static bool UninstallSystemService();
        static bool IsSystemServiceRunning();

        static std::wstring GetInputAgentPipeName(DWORD sessionId);
        static bool RequestServiceEnsureInputAgent(DWORD timeoutMs = 2000);

    private:
        bool EnsureSoftwareSasPolicyEnabled();
        void RestoreSoftwareSasPolicy();
        bool InvokeSasViaLocalSystemHelper();

        bool m_ModifiedSasReg = false;
        bool m_HadOrigSasVal = false;
        DWORD m_OrigSasVal = 0;
    };
}
