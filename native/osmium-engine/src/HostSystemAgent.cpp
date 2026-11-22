#include "HostSystemAgent.h"
#include "HostInputInjector.h"
#include "DriverSetupHelper.h"

#include <wtsapi32.h>
#include <userenv.h>
#include <shellapi.h>
#include <sddl.h>
#include <shlwapi.h>
#include <algorithm>
#include <atomic>
#include <cstring>
#include <memory>
#include <mutex>
#include <thread>
#include <unordered_map>
#include <vector>

#pragma comment(lib, "wtsapi32.lib")
#pragma comment(lib, "userenv.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shlwapi.lib")

namespace osmium
{
    namespace
    {
        constexpr wchar_t kSasPolicySubkey[] = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System";
        constexpr wchar_t kSasPolicyValue[] = L"SoftwareSASGeneration";
        constexpr wchar_t kServiceName[] = L"OsmiumSvc";
        constexpr wchar_t kServiceDisplayName[] = L"Osmium System Agent";
        constexpr wchar_t kServiceDescription[] =
            L"Provides elevated input injection and secure-desktop support for Osmium Remote Desktop.";
        constexpr wchar_t kOneShotSvcName[] = L"OsmiumSasOnceSvc";
        constexpr wchar_t kSysPipeName[] = L"\\\\.\\pipe\\OsmiumSystemAgentPipe";

        using PfnSendSAS = VOID(WINAPI*)(BOOL AsUser);

        PfnSendSAS ResolveSendSas()
        {
            static HMODULE s_SasMod = LoadLibraryW(L"sas.dll");
            if (!s_SasMod) return nullptr;
            return reinterpret_cast<PfnSendSAS>(GetProcAddress(s_SasMod, "SendSAS"));
        }

        std::wstring NormalizeWin32Path(std::wstring path)
        {
            if (path.rfind(L"\\\\?\\", 0) == 0)
            {
                path.erase(0, 4);
            }
            return path;
        }

        std::wstring GetCurrentExePath()
        {
            std::vector<wchar_t> buf(MAX_PATH, L'\0');
            for (;;)
            {
                DWORD len = GetModuleFileNameW(nullptr, buf.data(), static_cast<DWORD>(buf.size()));
                if (len == 0) return {};
                if (len < buf.size() - 1)
                {
                    return NormalizeWin32Path(std::wstring(buf.data(), len));
                }
                if (buf.size() >= 32768) return {};
                buf.resize(buf.size() * 2, L'\0');
            }
        }

        std::wstring GetParentDir(const std::wstring& path)
        {
            size_t pos = path.find_last_of(L"\\/");
            return (pos == std::wstring::npos) ? std::wstring() : path.substr(0, pos);
        }

        bool AreSameFile(const std::wstring& pathA, const std::wstring& pathB)
        {
            if (pathA.empty() || pathB.empty()) return false;
            if (_wcsicmp(pathA.c_str(), pathB.c_str()) == 0) return true;

            auto getFileId = [](const std::wstring& p, BY_HANDLE_FILE_INFORMATION& outInfo) -> bool
            {
                HANDLE h = CreateFileW(
                    p.c_str(),
                    0,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    nullptr,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    nullptr);
                if (h == INVALID_HANDLE_VALUE) return false;
                BOOL ok = GetFileInformationByHandle(h, &outInfo);
                CloseHandle(h);
                return ok != FALSE;
            };

            BY_HANDLE_FILE_INFORMATION infoA{}, infoB{};
            if (!getFileId(pathA, infoA) || !getFileId(pathB, infoB)) return false;
            return infoA.dwVolumeSerialNumber == infoB.dwVolumeSerialNumber &&
                infoA.nFileIndexHigh == infoB.nFileIndexHigh &&
                infoA.nFileIndexLow == infoB.nFileIndexLow;
        }

        std::wstring ExtractExePathFromServiceBinPath(const wchar_t* binPath)
        {
            if (!binPath || !*binPath) return {};
            const wchar_t* p = binPath;
            while (*p == L' ' || *p == L'\t') ++p;
            if (*p == L'"')
            {
                ++p;
                const wchar_t* end = wcschr(p, L'"');
                if (end && end > p)
                {
                    return NormalizeWin32Path(std::wstring(p, end));
                }
                return NormalizeWin32Path(std::wstring(p));
            }
            const wchar_t* end = p;
            while (*end && *end != L' ' && *end != L'\t') ++end;
            return NormalizeWin32Path(std::wstring(p, end));
        }

        bool IsAuthorizedPipeClient(HANDLE pipe, DWORD* outClientSessionId = nullptr, DWORD* outClientPid = nullptr)
        {
            ULONG clientPid = 0;
            if (!GetNamedPipeClientProcessId(pipe, &clientPid) || clientPid == 0)
            {
                return false;
            }

            DWORD clientSid = 0;
            if (!ProcessIdToSessionId(clientPid, &clientSid))
            {
                return false;
            }
            if (outClientSessionId) *outClientSessionId = clientSid;
            if (outClientPid) *outClientPid = clientPid;

            HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, clientPid);
            if (!hProc)
            {
                return false;
            }

            std::vector<wchar_t> clientPath(1024, L'\0');
            DWORD len = static_cast<DWORD>(clientPath.size());
            BOOL ok = QueryFullProcessImageNameW(hProc, 0, clientPath.data(), &len);
            if (!ok && GetLastError() == ERROR_INSUFFICIENT_BUFFER)
            {
                clientPath.resize(32768, L'\0');
                len = static_cast<DWORD>(clientPath.size());
                ok = QueryFullProcessImageNameW(hProc, 0, clientPath.data(), &len);
            }
            CloseHandle(hProc);
            if (!ok || len == 0)
            {
                return false;
            }

            std::wstring normalizedClient = NormalizeWin32Path(std::wstring(clientPath.data(), len));
            std::wstring selfPath = GetCurrentExePath();
            return AreSameFile(normalizedClient, selfPath);
        }

        std::wstring GetInputAgentStopEventName(DWORD sessionId)
        {
            return L"Global\\OsmiumInputAgentStop_" + std::to_wstring(sessionId);
        }

        PSECURITY_DESCRIPTOR CreatePipeSecurityDescriptor()
        {
            PSECURITY_DESCRIPTOR pSd = nullptr;
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                L"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;AU)",
                SDDL_REVISION_1,
                &pSd,
                nullptr);
            return pSd;
        }

        PSECURITY_DESCRIPTOR CreateSystemOnlySecurityDescriptor()
        {
            PSECURITY_DESCRIPTOR pSd = nullptr;
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                L"D:(A;;GA;;;SY)(A;;GA;;;BA)",
                SDDL_REVISION_1,
                &pSd,
                nullptr);
            return pSd;
        }

        void EnableProcessPrivilege(const wchar_t* privName)
        {
            HANDLE token = nullptr;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token))
            {
                return;
            }
            LUID luid{};
            if (LookupPrivilegeValueW(nullptr, privName, &luid))
            {
                TOKEN_PRIVILEGES tp{};
                tp.PrivilegeCount = 1;
                tp.Privileges[0].Luid = luid;
                tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
                AdjustTokenPrivileges(token, FALSE, &tp, sizeof(tp), nullptr, nullptr);
            }
            CloseHandle(token);
        }

        void EnsureSoftwareSasPolicyForService()
        {
            HKEY hKey = nullptr;
            if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, kSasPolicySubkey, 0, nullptr, 0, KEY_READ | KEY_WRITE, nullptr,
                                &hKey, nullptr) != ERROR_SUCCESS)
            {
                return;
            }
            DWORD val = 0;
            DWORD sz = sizeof(val);
            DWORD type = 0;
            if (RegQueryValueExW(hKey, kSasPolicyValue, nullptr, &type, reinterpret_cast<LPBYTE>(&val), &sz) !=
                ERROR_SUCCESS || type != REG_DWORD)
            {
                val = 0;
            }
            DWORD desired = val | 3;
            RegSetValueExW(hKey, kSasPolicyValue, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&desired),
                           sizeof(desired));
            RegCloseKey(hKey);
        }

        bool WaitConnectNamedPipe(HANDLE pipe, HANDLE stopEvent)
        {
            HANDLE ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!ev) return false;

            OVERLAPPED ov{};
            ov.hEvent = ev;
            BOOL ok = ConnectNamedPipe(pipe, &ov);
            if (ok)
            {
                CloseHandle(ev);
                return true;
            }

            DWORD err = GetLastError();
            if (err == ERROR_PIPE_CONNECTED)
            {
                CloseHandle(ev);
                return true;
            }

            if (err != ERROR_IO_PENDING)
            {
                CloseHandle(ev);
                return false;
            }

            HANDLE handles[2] = {stopEvent, ev};
            DWORD waitRes = WaitForMultipleObjects(stopEvent ? 2 : 1, stopEvent ? handles : &ev, FALSE, INFINITE);
            bool connected = false;
            if ((stopEvent && waitRes == WAIT_OBJECT_0 + 1) || (!stopEvent && waitRes == WAIT_OBJECT_0))
            {
                DWORD dummy = 0;
                connected = GetOverlappedResult(pipe, &ov, &dummy, FALSE) != FALSE;
            }
            else
            {
                CancelIo(pipe);
                WaitForSingleObject(ev, 500);
            }

            CloseHandle(ev);
            return connected;
        }

        bool ReadPipeWithStop(HANDLE pipe, void* buf, DWORD bufSize, DWORD* outRead, HANDLE stopEvent,
                              DWORD timeoutMs = INFINITE)
        {
            if (outRead) *outRead = 0;
            HANDLE ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!ev) return false;

            OVERLAPPED ov{};
            ov.hEvent = ev;
            DWORD bytesRead = 0;
            BOOL ok = ReadFile(pipe, buf, bufSize, &bytesRead, &ov);
            if (ok)
            {
                if (outRead) *outRead = bytesRead;
                CloseHandle(ev);
                return true;
            }

            DWORD err = GetLastError();
            if (err != ERROR_IO_PENDING)
            {
                CloseHandle(ev);
                return false;
            }

            HANDLE handles[2] = {stopEvent, ev};
            DWORD waitRes = WaitForMultipleObjects(stopEvent ? 2 : 1, stopEvent ? handles : &ev, FALSE, timeoutMs);
            bool success = false;
            if ((stopEvent && waitRes == WAIT_OBJECT_0 + 1) || (!stopEvent && waitRes == WAIT_OBJECT_0))
            {
                if (GetOverlappedResult(pipe, &ov, &bytesRead, FALSE))
                {
                    if (outRead) *outRead = bytesRead;
                    success = true;
                }
            }
            else
            {
                CancelIo(pipe);
                WaitForSingleObject(ev, 500);
            }

            CloseHandle(ev);
            return success;
        }

        bool WritePipeWithTimeout(HANDLE pipe, const void* buf, DWORD len, DWORD timeoutMs = 2000)
        {
            HANDLE ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!ev) return false;

            OVERLAPPED ov{};
            ov.hEvent = ev;
            DWORD written = 0;
            BOOL ok = WriteFile(pipe, buf, len, &written, &ov);
            if (ok)
            {
                CloseHandle(ev);
                return written == len;
            }

            DWORD err = GetLastError();
            if (err != ERROR_IO_PENDING)
            {
                CloseHandle(ev);
                return false;
            }

            bool success = false;
            if (WaitForSingleObject(ev, timeoutMs) == WAIT_OBJECT_0)
            {
                success = GetOverlappedResult(pipe, &ov, &written, FALSE) != FALSE && written == len;
            }
            else
            {
                CancelIo(pipe);
                WaitForSingleObject(ev, 500);
            }

            CloseHandle(ev);
            return success;
        }

        void SetPerMonitorV2DpiAwareness()
        {
            using PfnSetDpiCtx = BOOL(WINAPI*)(DPI_AWARENESS_CONTEXT);
            HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
            if (!hUser32) return;
            auto fn = reinterpret_cast<PfnSetDpiCtx>(GetProcAddress(hUser32, "SetProcessDpiAwarenessContext"));
            if (fn)
            {
                fn(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            }
        }

        bool RunElevatedSelfArg(const wchar_t* arg, DWORD waitMs = 30000)
        {
            std::wstring exePath = GetCurrentExePath();
            if (exePath.empty()) return false;

            SHELLEXECUTEINFOW sei{};
            sei.cbSize = sizeof(sei);
            sei.fMask = SEE_MASK_NOCLOSEPROCESS;
            sei.lpVerb = L"runas";
            sei.lpFile = exePath.c_str();
            sei.lpParameters = arg;
            sei.nShow = SW_HIDE;

            if (!ShellExecuteExW(&sei) || !sei.hProcess)
            {
                return false;
            }

            DWORD waitRes = WaitForSingleObject(sei.hProcess, waitMs);
            DWORD exitCode = 1;
            if (waitRes == WAIT_OBJECT_0)
            {
                GetExitCodeProcess(sei.hProcess, &exitCode);
            }
            CloseHandle(sei.hProcess);
            return waitRes == WAIT_OBJECT_0 && exitCode == 0;
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
        HANDLE g_SvcSessionKickEvent = nullptr;

        void ReportSvcStatus(DWORD state, DWORD exitCode = NO_ERROR, DWORD waitHint = 0, bool acceptSessionChange = false)
        {
            if (!g_SvcStatusHandle) return;
            SERVICE_STATUS st{};
            st.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
            st.dwCurrentState = state;
            st.dwWin32ExitCode = exitCode;
            st.dwWaitHint = waitHint;
            st.dwControlsAccepted = (state == SERVICE_RUNNING)
                                        ? (SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN |
                                           (acceptSessionChange ? SERVICE_ACCEPT_SESSIONCHANGE : 0))
                                        : 0;
            SetServiceStatus(g_SvcStatusHandle, &st);
        }

        class SessionAgentSupervisor
        {
        public:
            SessionAgentSupervisor()
            {
                m_Job = CreateJobObjectW(nullptr, nullptr);
                if (m_Job)
                {
                    JOBOBJECT_EXTENDED_LIMIT_INFORMATION jeli{};
                    jeli.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    SetInformationJobObject(m_Job, JobObjectExtendedLimitInformation, &jeli, sizeof(jeli));
                }
            }

            ~SessionAgentSupervisor()
            {
                StopAll();
                if (m_Job)
                {
                    CloseHandle(m_Job);
                    m_Job = nullptr;
                }
            }

            bool EnsureAgentForSession(DWORD sessionId)
            {
                if (sessionId == 0 || sessionId == 0xFFFFFFFF)
                {
                    return false;
                }

                std::lock_guard<std::mutex> lock(m_Mutex);
                auto it = m_Agents.find(sessionId);
                if (it != m_Agents.end())
                {
                    if (it->second.hProcess && WaitForSingleObject(it->second.hProcess, 0) == WAIT_TIMEOUT)
                    {
                        return true;
                    }
                    if (it->second.hProcess) CloseHandle(it->second.hProcess);
                    if (it->second.hStopEvent) CloseHandle(it->second.hStopEvent);
                    m_Agents.erase(it);
                }

                std::wstring stopName = GetInputAgentStopEventName(sessionId);
                PSECURITY_DESCRIPTOR pSd = CreateSystemOnlySecurityDescriptor();
                SECURITY_ATTRIBUTES sa{};
                sa.nLength = sizeof(sa);
                sa.lpSecurityDescriptor = pSd;
                HANDLE hStop = CreateEventW(pSd ? &sa : nullptr, TRUE, FALSE, stopName.c_str());
                if (pSd) LocalFree(pSd);
                if (hStop)
                {
                    // Ensure any leftover instance from a previous service run isn't kept signaled.
                    ResetEvent(hStop);
                }

                EnableProcessPrivilege(SE_TCB_NAME);
                EnableProcessPrivilege(SE_ASSIGNPRIMARYTOKEN_NAME);
                EnableProcessPrivilege(SE_INCREASE_QUOTA_NAME);

                HANDLE selfToken = nullptr;
                if (!OpenProcessToken(
                    GetCurrentProcess(),
                    TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                    &selfToken))
                {
                    OSM_LOG_ERROR("OsmiumSvc OpenProcessToken failed (err=%lu)", GetLastError());
                    if (hStop) CloseHandle(hStop);
                    return false;
                }

                HANDLE dupToken = nullptr;
                BOOL dupOk = DuplicateTokenEx(
                    selfToken,
                    MAXIMUM_ALLOWED,
                    nullptr,
                    SecurityIdentification,
                    TokenPrimary,
                    &dupToken);
                CloseHandle(selfToken);
                if (!dupOk || !dupToken)
                {
                    OSM_LOG_ERROR("OsmiumSvc DuplicateTokenEx failed (err=%lu)", GetLastError());
                    if (hStop) CloseHandle(hStop);
                    return false;
                }

                if (!SetTokenInformation(dupToken, TokenSessionId, &sessionId, sizeof(sessionId)))
                {
                    OSM_LOG_ERROR("OsmiumSvc SetTokenInformation(Session=%lu) failed (err=%lu)",
                                  sessionId, GetLastError());
                    CloseHandle(dupToken);
                    if (hStop) CloseHandle(hStop);
                    return false;
                }

                std::wstring exePath = GetCurrentExePath();
                std::wstring exeDir = GetParentDir(exePath);
                std::wstring cmdLine = L"\"" + exePath + L"\" --input-agent";
                std::vector<wchar_t> cmdBuf(cmdLine.begin(), cmdLine.end());
                cmdBuf.push_back(L'\0');

                STARTUPINFOW si{};
                si.cb = sizeof(si);
                wchar_t desktop[] = L"winsta0\\default";
                si.lpDesktop = desktop;

                PROCESS_INFORMATION pi{};
                BOOL created = CreateProcessAsUserW(
                    dupToken,
                    exePath.c_str(),
                    cmdBuf.data(),
                    nullptr,
                    nullptr,
                    FALSE,
                    CREATE_NO_WINDOW | NORMAL_PRIORITY_CLASS,
                    nullptr,
                    exeDir.empty() ? nullptr : exeDir.c_str(),
                    &si,
                    &pi);
                DWORD createErr = created ? 0 : GetLastError();
                CloseHandle(dupToken);

                if (!created)
                {
                    OSM_LOG_WARN("OsmiumSvc CreateProcessAsUserW(--input-agent, session=%lu) failed (err=%lu)",
                                 sessionId, createErr);
                    if (hStop) CloseHandle(hStop);
                    return false;
                }

                if (m_Job)
                {
                    AssignProcessToJobObject(m_Job, pi.hProcess);
                }
                CloseHandle(pi.hThread);

                AgentEntry entry{};
                entry.hProcess = pi.hProcess;
                entry.hStopEvent = hStop;
                m_Agents[sessionId] = entry;

                OSM_LOG_INFO("OsmiumSvc launched SYSTEM input agent pid=%lu in session=%lu",
                             pi.dwProcessId, sessionId);
                return true;
            }

            bool WaitPipeReady(DWORD sessionId, DWORD timeoutMs)
            {
                std::wstring pipeName = HostSystemAgent::GetInputAgentPipeName(sessionId);
                const uint64_t deadline = NowMillis() + timeoutMs;
                while (NowMillis() <= deadline)
                {
                    if (WaitNamedPipeW(pipeName.c_str(), 100))
                    {
                        return true;
                    }
                    DWORD err = GetLastError();
                    if (err == ERROR_SEM_TIMEOUT)
                    {
                        return true;
                    }
                    Sleep(50);
                }
                return false;
            }

            void StopAll()
            {
                std::vector<HANDLE> procs;
                {
                    std::lock_guard<std::mutex> lock(m_Mutex);
                    for (auto& kv : m_Agents)
                    {
                        if (kv.second.hStopEvent) SetEvent(kv.second.hStopEvent);
                        if (kv.second.hProcess) procs.push_back(kv.second.hProcess);
                    }
                }

                if (!procs.empty())
                {
                    DWORD count = static_cast<DWORD>(std::min<size_t>(procs.size(), MAXIMUM_WAIT_OBJECTS));
                    WaitForMultipleObjects(count, procs.data(), TRUE, 2000);
                }

                std::lock_guard<std::mutex> lock(m_Mutex);
                for (auto& kv : m_Agents)
                {
                    if (kv.second.hProcess)
                    {
                        if (WaitForSingleObject(kv.second.hProcess, 0) == WAIT_TIMEOUT)
                        {
                            TerminateProcess(kv.second.hProcess, 0);
                        }
                        CloseHandle(kv.second.hProcess);
                    }
                    if (kv.second.hStopEvent)
                    {
                        CloseHandle(kv.second.hStopEvent);
                    }
                }
                m_Agents.clear();
            }

        private:
            struct AgentEntry
            {
                HANDLE hProcess = nullptr;
                HANDLE hStopEvent = nullptr;
            };

            HANDLE m_Job = nullptr;
            std::mutex m_Mutex;
            std::unordered_map<DWORD, AgentEntry> m_Agents;
        };

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

            EnsureSoftwareSasPolicyForService();
            if (auto fn = ResolveSendSas())
            {
                fn(FALSE);
            }

            ReportSvcStatus(SERVICE_STOPPED);
        }

        DWORD WINAPI SystemSvcCtrlHandlerEx(DWORD ctrl, DWORD eventType, LPVOID, LPVOID)
        {
            if (ctrl == SERVICE_CONTROL_STOP || ctrl == SERVICE_CONTROL_SHUTDOWN)
            {
                ReportSvcStatus(SERVICE_STOP_PENDING, NO_ERROR, 3000);
                if (g_SvcStopEvent) SetEvent(g_SvcStopEvent);
                return NO_ERROR;
            }
            if (ctrl == SERVICE_CONTROL_SESSIONCHANGE)
            {
                if (eventType == WTS_CONSOLE_CONNECT ||
                    eventType == WTS_REMOTE_CONNECT ||
                    eventType == WTS_SESSION_LOGON ||
                    eventType == WTS_SESSION_UNLOCK)
                {
                    if (g_SvcSessionKickEvent) SetEvent(g_SvcSessionKickEvent);
                }
                return NO_ERROR;
            }
            if (ctrl == SERVICE_CONTROL_INTERROGATE)
            {
                return NO_ERROR;
            }
            return ERROR_CALL_NOT_IMPLEMENTED;
        }

        VOID WINAPI SystemSvcMain(DWORD, LPWSTR*)
        {
            g_SvcStatusHandle = RegisterServiceCtrlHandlerExW(kServiceName, SystemSvcCtrlHandlerEx, nullptr);
            g_SvcStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            g_SvcSessionKickEvent = CreateEventW(nullptr, FALSE, TRUE, nullptr);
            ReportSvcStatus(SERVICE_RUNNING, NO_ERROR, 0, true);

            SessionAgentSupervisor supervisor;
            std::thread monitorThread([&]()
            {
                HANDLE waitHandles[2] = {g_SvcStopEvent, g_SvcSessionKickEvent};
                while (WaitForSingleObject(g_SvcStopEvent, 0) == WAIT_TIMEOUT)
                {
                    DWORD activeSid = WTSGetActiveConsoleSessionId();
                    if (activeSid != 0 && activeSid != 0xFFFFFFFF)
                    {
                        supervisor.EnsureAgentForSession(activeSid);
                    }
                    DWORD w = WaitForMultipleObjects(2, waitHandles, FALSE, 1500);
                    if (w == WAIT_OBJECT_0)
                    {
                        break;
                    }
                }
            });

            PSECURITY_DESCRIPTOR pSd = CreatePipeSecurityDescriptor();
            SECURITY_ATTRIBUTES sa{};
            sa.nLength = sizeof(sa);
            sa.lpSecurityDescriptor = pSd;

            while (WaitForSingleObject(g_SvcStopEvent, 0) == WAIT_TIMEOUT)
            {
                HANDLE pipe = CreateNamedPipeW(
                    kSysPipeName,
                    PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                    PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                    8, 1024, 1024, 0, pSd ? &sa : nullptr);
                if (pipe == INVALID_HANDLE_VALUE)
                {
                    if (WaitForSingleObject(g_SvcStopEvent, 250) == WAIT_OBJECT_0) break;
                    continue;
                }

                if (!WaitConnectNamedPipe(pipe, g_SvcStopEvent))
                {
                    CloseHandle(pipe);
                    if (WaitForSingleObject(g_SvcStopEvent, 0) == WAIT_OBJECT_0) break;
                    continue;
                }

                DWORD clientSid = 0;
                DWORD clientPid = 0;
                if (!IsAuthorizedPipeClient(pipe, &clientSid, &clientPid))
                {
                    OSM_LOG_WARN("OsmiumSvc rejected unauthorized client on control pipe (pid=%lu)", clientPid);
                    DisconnectNamedPipe(pipe);
                    CloseHandle(pipe);
                    continue;
                }

                char buf[512]{};
                DWORD rd = 0;
                if (ReadPipeWithStop(pipe, buf, sizeof(buf) - 1, &rd, g_SvcStopEvent, 2000) && rd > 0)
                {
                    std::string cmd(buf, rd);
                    bool ok = false;
                    if (cmd == "PING")
                    {
                        ok = true;
                    }
                    else if (cmd == "SAS")
                    {
                        EnsureSoftwareSasPolicyForService();
                        if (auto fn = ResolveSendSas())
                        {
                            fn(FALSE);
                            ok = true;
                        }
                    }
                    else if (cmd == "ENSURE_INPUT_AGENT")
                    {
                        DWORD targetSid = (clientSid != 0) ? clientSid : WTSGetActiveConsoleSessionId();
                        if (targetSid != 0 && targetSid != 0xFFFFFFFF && supervisor.EnsureAgentForSession(targetSid))
                        {
                            ok = supervisor.WaitPipeReady(targetSid, 2000);
                        }
                    }
                    SecureZeroMemory(cmd.data(), cmd.size());
                    const char* rep = ok ? "OK" : "ERR";
                    WritePipeWithTimeout(pipe, rep, static_cast<DWORD>(strlen(rep)), 2000);
                }

                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
            }

            if (monitorThread.joinable())
            {
                monitorThread.join();
            }
            supervisor.StopAll();

            if (pSd) LocalFree(pSd);
            if (g_SvcSessionKickEvent)
            {
                CloseHandle(g_SvcSessionKickEvent);
                g_SvcSessionKickEvent = nullptr;
            }
            if (g_SvcStopEvent)
            {
                CloseHandle(g_SvcStopEvent);
                g_SvcStopEvent = nullptr;
            }
            ReportSvcStatus(SERVICE_STOPPED);
        }

        struct InputAgentWorker
        {
            std::thread th;
            std::atomic<bool> done{false};
        };

        void ServeInputAgentClient(HANDLE pipe, HANDLE stopEvent, std::atomic<bool>* doneFlag)
        {
            // Dedicated non-COM thread with no windows/hooks so SetThreadDesktop can freely switch
            // between Default and Winlogon (UAC / Lock Screen) desktops.
            HostInputInjector injector(/*allowAgentForwarding=*/false);
            std::vector<uint8_t> msgBuf(sizeof(AgentInputMsgHeader) + 65536);

            while (WaitForSingleObject(stopEvent, 0) == WAIT_TIMEOUT)
            {
                DWORD bytesRead = 0;
                if (!ReadPipeWithStop(pipe, msgBuf.data(), static_cast<DWORD>(msgBuf.size()), &bytesRead, stopEvent))
                {
                    break;
                }
                if (bytesRead < sizeof(AgentInputMsgHeader))
                {
                    continue;
                }

                const auto* hdr = reinterpret_cast<const AgentInputMsgHeader*>(msgBuf.data());
                if (hdr->magic != kAgentInputMagic ||
                    hdr->payloadLen > 65536 ||
                    bytesRead != sizeof(AgentInputMsgHeader) + hdr->payloadLen)
                {
                    continue;
                }

                const uint8_t* payload = msgBuf.data() + sizeof(AgentInputMsgHeader);
                switch (static_cast<AgentInputOp>(hdr->op))
                {
                case AgentInputOp::Ping:
                    WritePipeWithTimeout(pipe, "OK", 2, 1000);
                    break;
                case AgentInputOp::SetDisplayRect:
                    if (hdr->payloadLen == sizeof(RECT))
                    {
                        RECT rect{};
                        memcpy(&rect, payload, sizeof(RECT));
                        injector.SetTargetDisplayRect(rect);
                    }
                    break;
                case AgentInputOp::InjectPacket:
                    if (hdr->payloadLen >= sizeof(proto::AppPacketHeader))
                    {
                        injector.InjectPacket(payload, hdr->payloadLen);
                    }
                    break;
                case AgentInputOp::ReleaseAllInputs:
                    injector.ReleaseAllInputs();
                    break;
                default:
                    break;
                }
            }

            injector.ReleaseAllInputs();
            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
            if (doneFlag) doneFlag->store(true);
        }
    }

    std::wstring HostSystemAgent::GetInputAgentPipeName(DWORD sessionId)
    {
        return L"\\\\.\\pipe\\OsmiumInputAgentPipe_" + std::to_wstring(sessionId);
    }

    bool HostSystemAgent::RequestServiceEnsureInputAgent(DWORD timeoutMs)
    {
        HANDLE hPipe = CreateFileW(kSysPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (hPipe == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY)
        {
            if (WaitNamedPipeW(kSysPipeName, timeoutMs))
            {
                hPipe = CreateFileW(kSysPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
            }
        }
        if (hPipe == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        DWORD mode = PIPE_READMODE_MESSAGE;
        SetNamedPipeHandleState(hPipe, &mode, nullptr, nullptr);

        static constexpr char kCmd[] = "ENSURE_INPUT_AGENT";
        DWORD written = 0;
        BOOL ok = WriteFile(hPipe, kCmd, static_cast<DWORD>(sizeof(kCmd) - 1), &written, nullptr);
        char reply[32]{};
        DWORD rd = 0;
        if (ok)
        {
            ok = ReadFile(hPipe, reply, sizeof(reply) - 1, &rd, nullptr);
        }
        CloseHandle(hPipe);
        return ok && rd >= 2 && strncmp(reply, "OK", 2) == 0;
    }

    int HostSystemAgent::RunInputAgentMain()
    {
        SetPerMonitorV2DpiAwareness();

        DWORD mySid = 0;
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &mySid))
        {
            return static_cast<int>(GetLastError());
        }

        PSECURITY_DESCRIPTOR pStopSd = CreateSystemOnlySecurityDescriptor();
        SECURITY_ATTRIBUTES stopSa{};
        stopSa.nLength = sizeof(stopSa);
        stopSa.lpSecurityDescriptor = pStopSd;
        std::wstring stopName = GetInputAgentStopEventName(mySid);
        HANDLE stopEvent = CreateEventW(pStopSd ? &stopSa : nullptr, TRUE, FALSE, stopName.c_str());
        if (pStopSd) LocalFree(pStopSd);
        if (!stopEvent)
        {
            return static_cast<int>(GetLastError());
        }

        PSECURITY_DESCRIPTOR pPipeSd = CreatePipeSecurityDescriptor();
        SECURITY_ATTRIBUTES pipeSa{};
        pipeSa.nLength = sizeof(pipeSa);
        pipeSa.lpSecurityDescriptor = pPipeSd;

        std::wstring pipeName = GetInputAgentPipeName(mySid);
        std::vector<std::unique_ptr<InputAgentWorker>> workers;

        auto pruneFinishedWorkers = [&]()
        {
            for (auto it = workers.begin(); it != workers.end();)
            {
                if ((*it)->done.load())
                {
                    if ((*it)->th.joinable()) (*it)->th.join();
                    it = workers.erase(it);
                }
                else
                {
                    ++it;
                }
            }
        };

        OSM_LOG_INFO("Osmium SYSTEM input agent listening on session=%lu", mySid);

        while (WaitForSingleObject(stopEvent, 0) == WAIT_TIMEOUT)
        {
            pruneFinishedWorkers();

            HANDLE pipe = CreateNamedPipeW(
                pipeName.c_str(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                65536,
                65536,
                0,
                pPipeSd ? &pipeSa : nullptr);
            if (pipe == INVALID_HANDLE_VALUE)
            {
                if (WaitForSingleObject(stopEvent, 250) == WAIT_OBJECT_0) break;
                continue;
            }

            if (!WaitConnectNamedPipe(pipe, stopEvent))
            {
                CloseHandle(pipe);
                if (WaitForSingleObject(stopEvent, 0) == WAIT_OBJECT_0) break;
                continue;
            }

            DWORD clientSid = 0;
            DWORD clientPid = 0;
            if (!IsAuthorizedPipeClient(pipe, &clientSid, &clientPid) || clientSid != mySid)
            {
                OSM_LOG_WARN("Input agent rejected unauthorized client (pid=%lu, clientSid=%lu, mySid=%lu)",
                             clientPid, clientSid, mySid);
                DisconnectNamedPipe(pipe);
                CloseHandle(pipe);
                continue;
            }

            auto worker = std::make_unique<InputAgentWorker>();
            auto* donePtr = &worker->done;
            worker->th = std::thread([pipe, stopEvent, donePtr]()
            {
                ServeInputAgentClient(pipe, stopEvent, donePtr);
            });
            workers.push_back(std::move(worker));
        }

        for (auto& w : workers)
        {
            if (w->th.joinable()) w->th.join();
        }
        workers.clear();

        if (pPipeSd) LocalFree(pPipeSd);
        CloseHandle(stopEvent);
        return 0;
    }

    bool HostSystemAgent::InstallSystemService()
    {
        if (!DriverSetupHelper::IsProcessElevated())
        {
            return RunElevatedSelfArg(L"--install-service", 30000);
        }

        std::wstring exePath = GetCurrentExePath();
        if (exePath.empty()) return false;
        std::wstring binCmd = L"\"" + exePath + L"\" --service";

        SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_ALL_ACCESS);
        if (!scm)
        {
            OSM_LOG_ERROR("InstallSystemService OpenSCManagerW failed (err=%lu)", GetLastError());
            return false;
        }

        SC_HANDLE svc = OpenServiceW(scm, kServiceName, SERVICE_ALL_ACCESS);
        if (svc)
        {
            SERVICE_STATUS st{};
            if (QueryServiceStatus(svc, &st) && st.dwCurrentState != SERVICE_STOPPED)
            {
                ControlService(svc, SERVICE_CONTROL_STOP, &st);
                const uint64_t deadline = NowMillis() + 8000;
                while (NowMillis() < deadline)
                {
                    if (!QueryServiceStatus(svc, &st) || st.dwCurrentState == SERVICE_STOPPED) break;
                    Sleep(150);
                }
            }

            if (!ChangeServiceConfigW(
                svc,
                SERVICE_WIN32_OWN_PROCESS,
                SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL,
                binCmd.c_str(),
                nullptr,
                nullptr,
                nullptr,
                L"LocalSystem",
                nullptr,
                kServiceDisplayName))
            {
                OSM_LOG_ERROR("InstallSystemService ChangeServiceConfigW failed (err=%lu)", GetLastError());
                CloseServiceHandle(svc);
                CloseServiceHandle(scm);
                return false;
            }
        }
        else
        {
            svc = CreateServiceW(
                scm,
                kServiceName,
                kServiceDisplayName,
                SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS,
                SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL,
                binCmd.c_str(),
                nullptr,
                nullptr,
                nullptr,
                nullptr,
                nullptr);
            if (!svc)
            {
                OSM_LOG_ERROR("InstallSystemService CreateServiceW failed (err=%lu)", GetLastError());
                CloseServiceHandle(scm);
                return false;
            }
        }

        SERVICE_DESCRIPTIONW desc{};
        desc.lpDescription = const_cast<LPWSTR>(kServiceDescription);
        ChangeServiceConfig2W(svc, SERVICE_CONFIG_DESCRIPTION, &desc);

        SC_ACTION actions[3] = {
            {SC_ACTION_RESTART, 2000},
            {SC_ACTION_RESTART, 2000},
            {SC_ACTION_RESTART, 5000}
        };
        SERVICE_FAILURE_ACTIONSW fa{};
        fa.dwResetPeriod = 60;
        fa.cActions = 3;
        fa.lpsaActions = actions;
        ChangeServiceConfig2W(svc, SERVICE_CONFIG_FAILURE_ACTIONS, &fa);

        bool started = StartServiceW(svc, 0, nullptr) != FALSE || GetLastError() == ERROR_SERVICE_ALREADY_RUNNING;
        bool running = false;
        if (started)
        {
            const uint64_t deadline = NowMillis() + 10000;
            while (NowMillis() < deadline)
            {
                SERVICE_STATUS st{};
                if (QueryServiceStatus(svc, &st))
                {
                    if (st.dwCurrentState == SERVICE_RUNNING)
                    {
                        running = true;
                        break;
                    }
                    if (st.dwCurrentState == SERVICE_STOPPED)
                    {
                        break;
                    }
                }
                Sleep(150);
            }
        }
        else
        {
            OSM_LOG_ERROR("InstallSystemService StartServiceW failed (err=%lu)", GetLastError());
        }

        CloseServiceHandle(svc);
        CloseServiceHandle(scm);
        return running;
    }

    bool HostSystemAgent::UninstallSystemService()
    {
        if (!DriverSetupHelper::IsProcessElevated())
        {
            return RunElevatedSelfArg(L"--uninstall-service", 30000);
        }

        SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
        if (!scm) return false;

        SC_HANDLE svc = OpenServiceW(scm, kServiceName, SERVICE_STOP | SERVICE_QUERY_STATUS | DELETE);
        if (!svc)
        {
            DWORD err = GetLastError();
            CloseServiceHandle(scm);
            return err == ERROR_SERVICE_DOES_NOT_EXIST;
        }

        SERVICE_STATUS st{};
        if (QueryServiceStatus(svc, &st) && st.dwCurrentState != SERVICE_STOPPED)
        {
            ControlService(svc, SERVICE_CONTROL_STOP, &st);
            const uint64_t deadline = NowMillis() + 8000;
            while (NowMillis() < deadline)
            {
                if (!QueryServiceStatus(svc, &st) || st.dwCurrentState == SERVICE_STOPPED) break;
                Sleep(150);
            }
        }

        BOOL delOk = DeleteService(svc);
        DWORD delErr = delOk ? 0 : GetLastError();
        CloseServiceHandle(svc);
        CloseServiceHandle(scm);
        return delOk != FALSE || delErr == ERROR_SERVICE_MARKED_FOR_DELETE;
    }

    bool HostSystemAgent::IsSystemServiceRunning()
    {
        SC_HANDLE scm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
        if (!scm) return false;

        bool canQueryConfig = true;
        SC_HANDLE svc = OpenServiceW(scm, kServiceName, SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG);
        if (!svc)
        {
            canQueryConfig = false;
            svc = OpenServiceW(scm, kServiceName, SERVICE_QUERY_STATUS);
        }
        if (!svc)
        {
            CloseServiceHandle(scm);
            return false;
        }

        SERVICE_STATUS st{};
        bool running = QueryServiceStatus(svc, &st) && st.dwCurrentState == SERVICE_RUNNING;
        if (running && canQueryConfig)
        {
            DWORD needed = 0;
            QueryServiceConfigW(svc, nullptr, 0, &needed);
            if (needed > 0)
            {
                std::vector<uint8_t> buf(needed);
                auto* cfg = reinterpret_cast<QUERY_SERVICE_CONFIGW*>(buf.data());
                if (QueryServiceConfigW(svc, cfg, needed, &needed) && cfg->lpBinaryPathName)
                {
                    std::wstring svcExe = ExtractExePathFromServiceBinPath(cfg->lpBinaryPathName);
                    std::wstring myExe = GetCurrentExePath();
                    if (!svcExe.empty() && !myExe.empty() && !AreSameFile(svcExe, myExe))
                    {
                        running = false;
                    }
                }
            }
        }

        CloseServiceHandle(svc);
        CloseServiceHandle(scm);
        return running;
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
