#include "HostSystemAgent.h"
#include "DriverSetupHelper.h"

#include <wtsapi32.h>
#include <userenv.h>
#include <shellapi.h>
#include <sddl.h>
#include <algorithm>
#include <vector>

#pragma comment(lib, "wtsapi32.lib")
#pragma comment(lib, "userenv.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")

namespace osmium
{
    namespace
    {
        constexpr wchar_t kSasPolicySubkey[] = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System";
        constexpr wchar_t kSasPolicyValue[] = L"SoftwareSASGeneration";
        constexpr wchar_t kServiceName[] = L"OsmiumSvc";
        constexpr wchar_t kOneShotSvcName[] = L"OsmiumSasOnceSvc";
        constexpr wchar_t kSysPipeName[] = L"\\\\.\\pipe\\OsmiumSystemAgentPipe";

        using PfnSendSAS = VOID(WINAPI*)(BOOL AsUser);

        PfnSendSAS ResolveSendSas()
        {
            static HMODULE s_SasMod = LoadLibraryW(L"sas.dll");
            if (!s_SasMod) return nullptr;
            return reinterpret_cast<PfnSendSAS>(GetProcAddress(s_SasMod, "SendSAS"));
        }

        bool GetCurrentInputDesktopName(std::wstring& outName)
        {
            outName.clear();
            HDESK hDesk = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
            if (!hDesk) return false;
            wchar_t name[128]{};
            DWORD needed = 0;
            BOOL ok = GetUserObjectInformationW(hDesk, UOI_NAME, name, sizeof(name), &needed);
            CloseDesktop(hDesk);
            if (ok)
            {
                outName = name;
                return true;
            }
            return false;
        }

        void SendKeyVk(WORD vk, bool down, bool extended = false)
        {
            INPUT in{};
            in.type = INPUT_KEYBOARD;
            in.ki.wVk = vk;
            in.ki.wScan = static_cast<WORD>(MapVirtualKeyW(vk, MAPVK_VK_TO_VSC));
            in.ki.dwFlags = (down ? 0 : KEYEVENTF_KEYUP) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
            SendInput(1, &in, sizeof(INPUT));
        }
    }

    HostSystemAgent::~HostSystemAgent()
    {
        ResetOnDisconnect();
    }

    bool HostSystemAgent::SyncThreadDesktopToInput(HDESK* inOutLastDesk)
    {
        HDESK curThreadDesk = GetThreadDesktop(GetCurrentThreadId());
        wchar_t curName[128]{};
        DWORD curNeeded = 0;
        BOOL haveCur = curThreadDesk &&
            GetUserObjectInformationW(curThreadDesk, UOI_NAME, curName, sizeof(curName), &curNeeded);

        HDESK hRead = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
        if (!hRead) return false;
        wchar_t inName[128]{};
        DWORD inNeeded = 0;
        BOOL haveIn = GetUserObjectInformationW(hRead, UOI_NAME, inName, sizeof(inName), &inNeeded);
        CloseDesktop(hRead);

        if (haveCur && haveIn && _wcsicmp(curName, inName) == 0)
        {
            return true;
        }

        HDESK hDesk = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS);
        if (!hDesk)
        {
            hDesk = OpenInputDesktop(0, FALSE,
                                     DESKTOP_CREATEMENU | DESKTOP_CREATEWINDOW | DESKTOP_ENUMERATE |
                                     DESKTOP_HOOKCONTROL | DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS);
        }
        if (!hDesk) return false;
        BOOL ok = SetThreadDesktop(hDesk);
        if (inOutLastDesk)
        {
            if (*inOutLastDesk && *inOutLastDesk != hDesk)
            {
                CloseDesktop(*inOutLastDesk);
            }
            *inOutLastDesk = ok ? hDesk : *inOutLastDesk;
            if (!ok) CloseDesktop(hDesk);
        }
        else
        {
            CloseDesktop(hDesk);
        }
        return ok != FALSE;
    }

    bool HostSystemAgent::IsSessionLocked()
    {
        DWORD sessionId = WTSGetActiveConsoleSessionId();
        LPWSTR buf = nullptr;
        DWORD bytes = 0;
        if (WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId, WTSSessionInfoEx, &buf, &bytes) && buf)
        {
            bool locked = false;
            if (bytes >= sizeof(WTSINFOEXW))
            {
                const auto* ex = reinterpret_cast<const WTSINFOEXW*>(buf);
                if (ex->Level == 1)
                {
                    locked = (ex->Data.WTSInfoExLevel1.SessionFlags == WTS_SESSIONSTATE_LOCK);
                }
            }
            WTSFreeMemory(buf);
            if (locked) return true;
        }

        std::wstring deskName;
        if (GetCurrentInputDesktopName(deskName))
        {
            if (_wcsicmp(deskName.c_str(), L"Winlogon") == 0)
            {
                return true;
            }
        }
        return false;
    }

    bool HostSystemAgent::IsSmartFullscreenOrCursorLocked(RECT targetDisplayRect)
    {
        QUERY_USER_NOTIFICATION_STATE quns{};
        if (SUCCEEDED(SHQueryUserNotificationState(&quns)))
        {
            if (quns == QUNS_RUNNING_D3D_FULL_SCREEN)
            {
                return true;
            }
        }

        CURSORINFO ci{};
        ci.cbSize = sizeof(ci);
        if (GetCursorInfo(&ci) && !(ci.flags & CURSOR_SHOWING))
        {
            return true;
        }

        RECT clip{};
        if (GetClipCursor(&clip))
        {
            int dispW = std::max(1L, targetDisplayRect.right - targetDisplayRect.left);
            int dispH = std::max(1L, targetDisplayRect.bottom - targetDisplayRect.top);
            int clipW = clip.right - clip.left;
            int clipH = clip.bottom - clip.top;
            if (clipW > 0 && clipH > 0 && (clipW < (dispW * 85) / 100 || clipH < (dispH * 85) / 100))
            {
                return true;
            }
        }

        HWND fg = GetForegroundWindow();
        if (fg)
        {
            wchar_t cls[64]{};
            GetClassNameW(fg, cls, ARRAYSIZE(cls));
            if (_wcsicmp(cls, L"Progman") != 0 &&
                _wcsicmp(cls, L"WorkerW") != 0 &&
                _wcsicmp(cls, L"Shell_TrayWnd") != 0 &&
                _wcsicmp(cls, L"Windows.UI.Core.CoreWindow") != 0)
            {
                RECT wr{};
                if (GetWindowRect(fg, &wr))
                {
                    LONG style = GetWindowLongW(fg, GWL_STYLE);
                    bool noCaption = (style & WS_CAPTION) == 0;
                    if (noCaption &&
                        wr.left <= targetDisplayRect.left &&
                        wr.top <= targetDisplayRect.top &&
                        wr.right >= targetDisplayRect.right &&
                        wr.bottom >= targetDisplayRect.bottom)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    bool HostSystemAgent::EnsureSoftwareSasPolicyEnabled()
    {
        HKEY hKey = nullptr;
        if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, kSasPolicySubkey, 0, nullptr, 0, KEY_READ | KEY_WRITE, nullptr, &hKey,
                            nullptr) != ERROR_SUCCESS)
        {
            return false;
        }

        DWORD val = 0;
        DWORD sz = sizeof(val);
        DWORD type = 0;
        if (RegQueryValueExW(hKey, kSasPolicyValue, nullptr, &type, reinterpret_cast<LPBYTE>(&val), &sz) ==
            ERROR_SUCCESS &&
            type == REG_DWORD)
        {
            if (!m_ModifiedSasReg)
            {
                m_HadOrigSasVal = true;
                m_OrigSasVal = val;
            }
        }
        else
        {
            if (!m_ModifiedSasReg)
            {
                m_HadOrigSasVal = false;
                m_OrigSasVal = 0;
            }
        }

        DWORD desired = val | 3;
        LSTATUS st = RegSetValueExW(hKey, kSasPolicyValue, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&desired),
                                    sizeof(desired));
        RegCloseKey(hKey);
        if (st == ERROR_SUCCESS)
        {
            m_ModifiedSasReg = true;
            return true;
        }
        return false;
    }

    void HostSystemAgent::RestoreSoftwareSasPolicy()
    {
        if (!m_ModifiedSasReg) return;
        HKEY hKey = nullptr;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, kSasPolicySubkey, 0, KEY_WRITE, &hKey) == ERROR_SUCCESS)
        {
            if (m_HadOrigSasVal)
            {
                RegSetValueExW(hKey, kSasPolicyValue, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&m_OrigSasVal),
                               sizeof(m_OrigSasVal));
            }
            else
            {
                RegDeleteValueW(hKey, kSasPolicyValue);
            }
            RegCloseKey(hKey);
        }
        m_ModifiedSasReg = false;
    }

    bool HostSystemAgent::InvokeSasViaLocalSystemHelper()
    {
        HANDLE hPipe = CreateFileW(kSysPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (hPipe != INVALID_HANDLE_VALUE)
        {
            const char cmd[] = "SAS";
            DWORD written = 0;
            BOOL ok = WriteFile(hPipe, cmd, 3, &written, nullptr);
            char reply[32]{};
            DWORD rd = 0;
            if (ok) ReadFile(hPipe, reply, sizeof(reply) - 1, &rd, nullptr);
            CloseHandle(hPipe);
            return ok && strncmp(reply, "OK", 2) == 0;
        }

        if (!DriverSetupHelper::IsProcessElevated()) return false;

        wchar_t exePath[MAX_PATH]{};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        std::wstring binCmd = std::wstring(L"\"") + exePath + L"\" --sas-once";

        SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CREATE_SERVICE);
        if (!scm) return false;

        SC_HANDLE svc = OpenServiceW(scm, kOneShotSvcName, SERVICE_ALL_ACCESS);
        if (!svc)
        {
            svc = CreateServiceW(
                scm, kOneShotSvcName, L"Osmium One-Shot SAS Helper",
                SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_DEMAND_START,
                SERVICE_ERROR_IGNORE, binCmd.c_str(), nullptr, nullptr, nullptr, nullptr, nullptr);
        }
        bool started = false;
        if (svc)
        {
            if (StartServiceW(svc, 0, nullptr) || GetLastError() == ERROR_SERVICE_ALREADY_RUNNING)
            {
                started = true;
                Sleep(450);
            }
            DeleteService(svc);
            CloseServiceHandle(svc);
        }
        CloseServiceHandle(scm);
        return started;
    }

    bool HostSystemAgent::SendCtrlAltDel()
    {
        EnsureSoftwareSasPolicyEnabled();

        if (DriverSetupHelper::IsRunningAsSystem())
        {
            auto fn = ResolveSendSas();
            if (fn)
            {
                fn(FALSE);
                OSM_LOG_INFO("Sent SAS (Ctrl+Alt+Del) natively via LocalSystem");
                return true;
            }
        }

        if (InvokeSasViaLocalSystemHelper())
        {
            OSM_LOG_INFO("Sent SAS (Ctrl+Alt+Del) via LocalSystem helper");
            return true;
        }

        if (auto fn = ResolveSendSas())
        {
            fn(TRUE);
            OSM_LOG_INFO("Triggered SendSAS(TRUE) fallback");
            return true;
        }
        return false;
    }

    bool HostSystemAgent::OpenTaskManager()
    {
        wchar_t sysDir[MAX_PATH]{};
        GetSystemDirectoryW(sysDir, MAX_PATH);
        std::wstring taskMgrPath = std::wstring(sysDir) + L"\\taskmgr.exe";

        if (DriverSetupHelper::IsRunningAsSystem())
        {
            DWORD sid = WTSGetActiveConsoleSessionId();
            HANDLE userToken = nullptr;
            if (WTSQueryUserToken(sid, &userToken))
            {
                LPVOID env = nullptr;
                CreateEnvironmentBlock(&env, userToken, FALSE);

                STARTUPINFOW si{};
                si.cb = sizeof(si);
                wchar_t desk[] = L"winsta0\\default";
                si.lpDesktop = desk;
                PROCESS_INFORMATION pi{};
                BOOL ok = CreateProcessAsUserW(
                    userToken, taskMgrPath.c_str(), nullptr, nullptr, nullptr, FALSE,
                    CREATE_UNICODE_ENVIRONMENT, env, sysDir, &si, &pi);
                if (ok)
                {
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);
                }
                if (env) DestroyEnvironmentBlock(env);
                CloseHandle(userToken);
                if (ok) return true;
            }
        }

        SHELLEXECUTEINFOW sei{};
        sei.cbSize = sizeof(sei);
        sei.lpVerb = L"open";
        sei.lpFile = taskMgrPath.c_str();
        sei.nShow = SW_SHOWNORMAL;
        if (ShellExecuteExW(&sei))
        {
            return true;
        }

        SyncThreadDesktopToInput();
        SendKeyVk(VK_LCONTROL, true);
        SendKeyVk(VK_LSHIFT, true);
        SendKeyVk(VK_ESCAPE, true);
        SendKeyVk(VK_ESCAPE, false);
        SendKeyVk(VK_LSHIFT, false);
        SendKeyVk(VK_LCONTROL, false);
        return true;
    }

    void HostSystemAgent::ResetOnDisconnect()
    {
        RestoreSoftwareSasPolicy();
    }

    namespace
    {
        SERVICE_STATUS_HANDLE g_SvcStatusHandle = nullptr;
        HANDLE g_SvcStopEvent = nullptr;

        void ReportSvcStatus(DWORD state, DWORD exitCode = NO_ERROR, DWORD waitHint = 0)
        {
            if (!g_SvcStatusHandle) return;
            SERVICE_STATUS st{};
            st.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
            st.dwCurrentState = state;
            st.dwWin32ExitCode = exitCode;
            st.dwWaitHint = waitHint;
            st.dwControlsAccepted = (state == SERVICE_RUNNING) ? (SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN) : 0;
            SetServiceStatus(g_SvcStatusHandle, &st);
        }

        VOID WINAPI OneShotSasCtrlHandler(DWORD ctrl)
        {
            if (ctrl == SERVICE_CONTROL_STOP || ctrl == SERVICE_CONTROL_SHUTDOWN)
            {
                ReportSvcStatus(SERVICE_STOPPED);
            }
        }

        VOID WINAPI OneShotSasServiceMain(DWORD, LPWSTR*)
        {
            g_SvcStatusHandle = RegisterServiceCtrlHandlerW(kOneShotSvcName, OneShotSasCtrlHandler);
            ReportSvcStatus(SERVICE_RUNNING);

            if (auto fn = ResolveSendSas())
            {
                fn(FALSE);
            }

            ReportSvcStatus(SERVICE_STOPPED);
        }

        VOID WINAPI SystemSvcCtrlHandler(DWORD ctrl)
        {
            if (ctrl == SERVICE_CONTROL_STOP || ctrl == SERVICE_CONTROL_SHUTDOWN)
            {
                ReportSvcStatus(SERVICE_STOP_PENDING, NO_ERROR, 1000);
                if (g_SvcStopEvent) SetEvent(g_SvcStopEvent);
            }
        }

        VOID WINAPI SystemSvcMain(DWORD, LPWSTR*)
        {
            g_SvcStatusHandle = RegisterServiceCtrlHandlerW(kServiceName, SystemSvcCtrlHandler);
            g_SvcStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            ReportSvcStatus(SERVICE_RUNNING);

            SECURITY_ATTRIBUTES sa{};
            sa.nLength = sizeof(sa);
            PSECURITY_DESCRIPTOR pSd = nullptr;
            ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;SY)(A;;GA;;;BA)", SDDL_REVISION_1, &pSd,
                                                                 nullptr);
            sa.lpSecurityDescriptor = pSd;

            while (WaitForSingleObject(g_SvcStopEvent, 0) == WAIT_TIMEOUT)
            {
                HANDLE pipe = CreateNamedPipeW(
                    kSysPipeName,
                    PIPE_ACCESS_DUPLEX,
                    PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                    4, 512, 512, 0, pSd ? &sa : nullptr);
                if (pipe == INVALID_HANDLE_VALUE)
                {
                    Sleep(250);
                    continue;
                }
                if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED)
                {
                    char buf[512]{};
                    DWORD rd = 0;
                    if (ReadFile(pipe, buf, sizeof(buf) - 1, &rd, nullptr) && rd > 0)
                    {
                        std::string cmd(buf, rd);
                        bool ok = false;
                        if (cmd == "SAS")
                        {
                            if (auto fn = ResolveSendSas())
                            {
                                fn(FALSE);
                                ok = true;
                            }
                        }
                        SecureZeroMemory(cmd.data(), cmd.size());
                        const char* rep = ok ? "OK" : "ERR";
                        DWORD wr = 0;
                        WriteFile(pipe, rep, static_cast<DWORD>(strlen(rep)), &wr, nullptr);
                    }
                }
                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
            }

            if (pSd) LocalFree(pSd);
            if (g_SvcStopEvent)
            {
                CloseHandle(g_SvcStopEvent);
                g_SvcStopEvent = nullptr;
            }
            ReportSvcStatus(SERVICE_STOPPED);
        }
    }

    int HostSystemAgent::RunOneShotSasServiceMain()
    {
        SERVICE_TABLE_ENTRYW tbl[] = {
            {const_cast<LPWSTR>(kOneShotSvcName), OneShotSasServiceMain},
            {nullptr, nullptr}
        };
        return StartServiceCtrlDispatcherW(tbl) ? 0 : static_cast<int>(GetLastError());
    }

    int HostSystemAgent::RunSystemServiceMain()
    {
        SERVICE_TABLE_ENTRYW tbl[] = {
            {const_cast<LPWSTR>(kServiceName), SystemSvcMain},
            {nullptr, nullptr}
        };
        return StartServiceCtrlDispatcherW(tbl) ? 0 : static_cast<int>(GetLastError());
    }
}
