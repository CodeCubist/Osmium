#include "DriverSetupHelper.h"

#include <setupapi.h>
#include <newdev.h>
#include <cfgmgr32.h>
#include <wincrypt.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <cstring>
#include <vector>

#pragma comment(lib, "setupapi.lib")
#pragma comment(lib, "newdev.lib")
#pragma comment(lib, "cfgmgr32.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")

namespace osmium
{
    namespace
    {
        bool MatchesHardwareId(HDEVINFO devInfo, SP_DEVINFO_DATA& devData, const wchar_t* targetHwId)
        {
            DWORD reqSize = 0;
            DWORD regType = 0;
            SetupDiGetDeviceRegistryPropertyW(devInfo, &devData, SPDRP_HARDWAREID, nullptr, nullptr, 0, &reqSize);
            if (reqSize == 0) return false;

            std::vector<wchar_t> buf(reqSize / sizeof(wchar_t) + 4, L'\0');
            if (!SetupDiGetDeviceRegistryPropertyW(
                devInfo, &devData, SPDRP_HARDWAREID, &regType,
                reinterpret_cast<PBYTE>(buf.data()),
                static_cast<DWORD>(buf.size() * sizeof(wchar_t)),
                &reqSize))
            {
                return false;
            }

            for (const wchar_t* p = buf.data(); p && *p; p += wcslen(p) + 1)
            {
                if (_wcsicmp(p, targetHwId) == 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    bool DriverSetupHelper::IsProcessElevated()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
        TOKEN_ELEVATION elev{};
        DWORD cb = 0;
        BOOL ok = GetTokenInformation(token, TokenElevation, &elev, sizeof(elev), &cb);
        CloseHandle(token);
        return ok && elev.TokenIsElevated != 0;
    }

    bool DriverSetupHelper::IsRunningAsSystem()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
        BYTE buf[256]{};
        DWORD cb = 0;
        bool isSys = false;
        if (GetTokenInformation(token, TokenUser, buf, sizeof(buf), &cb))
        {
            auto* tu = reinterpret_cast<TOKEN_USER*>(buf);
            SID_IDENTIFIER_AUTHORITY ntAuth = SECURITY_NT_AUTHORITY;
            PSID sysSid = nullptr;
            if (AllocateAndInitializeSid(&ntAuth, 1, SECURITY_LOCAL_SYSTEM_RID, 0, 0, 0, 0, 0, 0, 0, &sysSid))
            {
                isSys = EqualSid(tu->User.Sid, sysSid) != FALSE;
                FreeSid(sysSid);
            }
        }
        CloseHandle(token);
        return isSys;
    }

    std::wstring DriverSetupHelper::GetDriversBaseDir()
    {
        HMODULE hMod = nullptr;
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&DriverSetupHelper::GetDriversBaseDir),
            &hMod);

        wchar_t modPath[MAX_PATH]{};
        GetModuleFileNameW(hMod, modPath, MAX_PATH);
        PathRemoveFileSpecW(modPath);

        std::wstring candidate = std::wstring(modPath) + L"\\drivers";
        if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            return candidate;
        }

        std::wstring probe1 = std::wstring(modPath) + L"\\..\\..\\..\\drivers";
        if (GetFileAttributesW(probe1.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            return probe1;
        }
        return candidate;
    }

    bool DriverSetupHelper::IsDeviceInstalled(const wchar_t* hardwareId, bool* outIsRunning)
    {
        if (outIsRunning) *outIsRunning = false;
        if (!hardwareId) return false;

        HDEVINFO devInfo = SetupDiGetClassDevsW(nullptr, nullptr, nullptr, DIGCF_ALLCLASSES);
        if (devInfo == INVALID_HANDLE_VALUE) return false;

        bool found = false;
        SP_DEVINFO_DATA devData{};
        devData.cbSize = sizeof(devData);

        for (DWORD i = 0; SetupDiEnumDeviceInfo(devInfo, i, &devData); ++i)
        {
            if (!MatchesHardwareId(devInfo, devData, hardwareId)) continue;
            found = true;

            ULONG status = 0, problem = 0;
            if (CM_Get_DevNode_Status(&status, &problem, devData.DevInst, 0) == CR_SUCCESS)
            {
                if (outIsRunning && (status & DN_STARTED) != 0 && problem == 0)
                {
                    *outIsRunning = true;
                }
            }
            break;
        }

        SetupDiDestroyDeviceInfoList(devInfo);
        return found;
    }

    bool DriverSetupHelper::TrustCatalogSignerCert(const std::wstring& catPath)
    {
        if (catPath.empty() || GetFileAttributesW(catPath.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            return false;
        }

        HCERTSTORE hEmbeddedStore = nullptr;
        HCRYPTMSG hMsg = nullptr;
        DWORD encoding = 0, contentType = 0, formatType = 0;
        if (!CryptQueryObject(
            CERT_QUERY_OBJECT_FILE,
            catPath.c_str(),
            CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED | CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
            CERT_QUERY_FORMAT_FLAG_BINARY,
            0,
            &encoding,
            &contentType,
            &formatType,
            &hEmbeddedStore,
            &hMsg,
            nullptr))
        {
            return false;
        }

        bool anyAdded = false;
        if (hEmbeddedStore)
        {
            HCERTSTORE hPubStore = CertOpenStore(
                CERT_STORE_PROV_SYSTEM_W,
                0,
                0,
                CERT_SYSTEM_STORE_LOCAL_MACHINE | CERT_STORE_OPEN_EXISTING_FLAG,
                L"TrustedPublisher");
            if (!hPubStore)
            {
                hPubStore = CertOpenStore(
                    CERT_STORE_PROV_SYSTEM_W,
                    0,
                    0,
                    CERT_SYSTEM_STORE_LOCAL_MACHINE,
                    L"TrustedPublisher");
            }
            if (hPubStore)
            {
                PCCERT_CONTEXT ctx = nullptr;
                while ((ctx = CertEnumCertificatesInStore(hEmbeddedStore, ctx)) != nullptr)
                {
                    if (CertAddCertificateContextToStore(hPubStore, ctx, CERT_STORE_ADD_USE_EXISTING, nullptr))
                    {
                        anyAdded = true;
                    }
                }
                CertCloseStore(hPubStore, 0);
            }
            CertCloseStore(hEmbeddedStore, 0);
        }
        if (hMsg) CryptMsgClose(hMsg);
        return anyAdded;
    }

    bool DriverSetupHelper::InstallRootDriver(
        const std::wstring& infPath,
        const wchar_t* hardwareId,
        const std::wstring& catPath)
    {
        if (!IsProcessElevated())
        {
            OSM_LOG_WARN("InstallRootDriver skipped: administrator privileges required for %ls", hardwareId);
            return false;
        }

        if (GetFileAttributesW(infPath.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            OSM_LOG_ERROR("INF file not found: %ls", infPath.c_str());
            return false;
        }

        if (!catPath.empty())
        {
            TrustCatalogSignerCert(catPath);
        }

        bool running = false;
        if (IsDeviceInstalled(hardwareId, &running))
        {
            if (!running)
            {
                SetDeviceEnabled(hardwareId, true);
            }
            BOOL rebootReq = FALSE;
            if (UpdateDriverForPlugAndPlayDevicesW(nullptr, hardwareId, infPath.c_str(),
                                                   INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, &rebootReq))
            {
                return true;
            }
            IsDeviceInstalled(hardwareId, &running);
            return running;
        }

        GUID classGuid{};
        wchar_t className[MAX_CLASS_NAME_LEN]{};
        if (!SetupDiGetINFClassW(infPath.c_str(), &classGuid, className, ARRAYSIZE(className), nullptr))
        {
            OSM_LOG_ERROR("SetupDiGetINFClassW failed for %ls (err=%lu)", infPath.c_str(), GetLastError());
            return false;
        }

        HDEVINFO devInfoList = SetupDiCreateDeviceInfoList(&classGuid, nullptr);
        if (devInfoList == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        SP_DEVINFO_DATA devInfoData{};
        devInfoData.cbSize = sizeof(devInfoData);
        if (!SetupDiCreateDeviceInfoW(devInfoList, className, &classGuid, nullptr, nullptr, DICD_GENERATE_ID,
                                      &devInfoData))
        {
            SetupDiDestroyDeviceInfoList(devInfoList);
            return false;
        }

        std::wstring hwIdMulti(hardwareId);
        hwIdMulti.push_back(L'\0');
        hwIdMulti.push_back(L'\0');

        if (!SetupDiSetDeviceRegistryPropertyW(
            devInfoList,
            &devInfoData,
            SPDRP_HARDWAREID,
            reinterpret_cast<const BYTE*>(hwIdMulti.data()),
            static_cast<DWORD>(hwIdMulti.size() * sizeof(wchar_t))))
        {
            SetupDiDestroyDeviceInfoList(devInfoList);
            return false;
        }

        if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, devInfoList, &devInfoData))
        {
            SetupDiDestroyDeviceInfoList(devInfoList);
            return false;
        }
        SetupDiDestroyDeviceInfoList(devInfoList);

        BOOL rebootReq = FALSE;
        if (!UpdateDriverForPlugAndPlayDevicesW(
            nullptr,
            hardwareId,
            infPath.c_str(),
            INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE,
            &rebootReq))
        {
            DWORD err = GetLastError();
            OSM_LOG_ERROR("UpdateDriverForPlugAndPlayDevicesW failed for %ls (err=%lu)", hardwareId, err);
            return false;
        }

        OSM_LOG_INFO("Installed driver %ls cleanly (rebootRequired=%d)", hardwareId, rebootReq ? 1 : 0);
        return true;
    }

    bool DriverSetupHelper::SetDeviceEnabled(const wchar_t* hardwareId, bool enable)
    {
        if (!IsProcessElevated()) return false;

        HDEVINFO devInfo = SetupDiGetClassDevsW(nullptr, nullptr, nullptr, DIGCF_ALLCLASSES);
        if (devInfo == INVALID_HANDLE_VALUE) return false;

        bool touched = false;
        SP_DEVINFO_DATA devData{};
        devData.cbSize = sizeof(devData);

        for (DWORD i = 0; SetupDiEnumDeviceInfo(devInfo, i, &devData); ++i)
        {
            if (!MatchesHardwareId(devInfo, devData, hardwareId)) continue;

            SP_PROPCHANGE_PARAMS params{};
            params.ClassInstallHeader.cbSize = sizeof(SP_CLASSINSTALL_HEADER);
            params.ClassInstallHeader.InstallFunction = DIF_PROPERTYCHANGE;
            params.StateChange = enable ? DICS_ENABLE : DICS_DISABLE;
            params.Scope = DICS_FLAG_GLOBAL;
            params.HwProfile = 0;

            if (SetupDiSetClassInstallParamsW(devInfo, &devData, &params.ClassInstallHeader, sizeof(params)) &&
                SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devInfo, &devData))
            {
                touched = true;
            }
        }

        SetupDiDestroyDeviceInfoList(devInfo);
        return touched;
    }

    bool DriverSetupHelper::RemoveDeviceNode(const wchar_t* hardwareId)
    {
        if (!IsProcessElevated()) return false;

        HDEVINFO devInfo = SetupDiGetClassDevsW(nullptr, nullptr, nullptr, DIGCF_ALLCLASSES);
        if (devInfo == INVALID_HANDLE_VALUE) return false;

        bool removed = false;
        SP_DEVINFO_DATA devData{};
        devData.cbSize = sizeof(devData);

        for (DWORD i = 0; SetupDiEnumDeviceInfo(devInfo, i, &devData); ++i)
        {
            if (!MatchesHardwareId(devInfo, devData, hardwareId)) continue;
            if (SetupDiCallClassInstaller(DIF_REMOVE, devInfo, &devData))
            {
                removed = true;
            }
        }

        SetupDiDestroyDeviceInfoList(devInfo);
        return removed;
    }

    bool DriverSetupHelper::UninstallRootDriver(const wchar_t* hardwareId, const wchar_t* infOriginalNameHint)
    {
        if (!IsProcessElevated())
        {
            OSM_LOG_WARN("UninstallRootDriver skipped: administrator privileges required");
            return false;
        }

        bool anyTouched = false;

        if (hardwareId && *hardwareId)
        {
            HDEVINFO devInfo = SetupDiGetClassDevsW(nullptr, nullptr, nullptr, DIGCF_ALLCLASSES);
            if (devInfo != INVALID_HANDLE_VALUE)
            {
                SP_DEVINFO_DATA devData{};
                devData.cbSize = sizeof(devData);
                for (DWORD i = 0; SetupDiEnumDeviceInfo(devInfo, i, &devData); ++i)
                {
                    if (!MatchesHardwareId(devInfo, devData, hardwareId)) continue;
                    BOOL rebootReq = FALSE;
                    if (DiUninstallDevice(nullptr, devInfo, &devData, 0, &rebootReq) ||
                        SetupDiCallClassInstaller(DIF_REMOVE, devInfo, &devData))
                    {
                        anyTouched = true;
                        OSM_LOG_INFO("Uninstalled PnP device %ls", hardwareId);
                    }
                }
                SetupDiDestroyDeviceInfoList(devInfo);
            }
        }

        if (infOriginalNameHint && *infOriginalNameHint)
        {
            wchar_t winDir[MAX_PATH]{};
            if (GetWindowsDirectoryW(winDir, MAX_PATH) > 0)
            {
                std::wstring infDir = std::wstring(winDir) + L"\\INF";
                std::wstring pattern = infDir + L"\\oem*.inf";

                WIN32_FIND_DATAW fd{};
                HANDLE hFind = FindFirstFileW(pattern.c_str(), &fd);
                if (hFind != INVALID_HANDLE_VALUE)
                {
                    do
                    {
                        std::wstring oemFullPath = infDir + L"\\" + fd.cFileName;
                        DWORD reqSize = 0;
                        SetupGetInfInformationW(oemFullPath.c_str(), INFINFO_INF_NAME_IS_ABSOLUTE, nullptr, 0,
                                                &reqSize);
                        if (reqSize == 0) continue;

                        std::vector<BYTE> infoBuf(reqSize);
                        auto* pInfo = reinterpret_cast<PSP_INF_INFORMATION>(infoBuf.data());
                        if (!SetupGetInfInformationW(oemFullPath.c_str(), INFINFO_INF_NAME_IS_ABSOLUTE, pInfo, reqSize,
                                                     nullptr))
                        {
                            continue;
                        }

                        SP_ORIGINAL_FILE_INFO_W origInfo{};
                        origInfo.cbSize = sizeof(origInfo);
                        if (SetupQueryInfOriginalFileInformationW(pInfo, 0, nullptr, &origInfo))
                        {
                            const wchar_t* origBase = PathFindFileNameW(origInfo.OriginalInfName);
                            if (origBase && _wcsicmp(origBase, infOriginalNameHint) == 0)
                            {
                                if (SetupUninstallOEMInfW(fd.cFileName, SUOI_FORCEDELETE, nullptr))
                                {
                                    anyTouched = true;
                                    OSM_LOG_INFO("Purged OEM driver package %ls (%ls)", fd.cFileName,
                                                 origInfo.OriginalInfName);
                                }
                            }
                        }
                    }
                    while (FindNextFileW(hFind, &fd));
                    FindClose(hFind);
                }
            }
        }

        return true;
    }

    bool DriverSetupHelper::RunElevatedSelfDriverOp(bool install, int waitMs)
    {
        wchar_t exePath[MAX_PATH]{};
        if (GetModuleFileNameW(nullptr, exePath, MAX_PATH) == 0)
        {
            return false;
        }

        SHELLEXECUTEINFOW sei{};
        sei.cbSize = sizeof(sei);
        sei.fMask = SEE_MASK_NOCLOSEPROCESS;
        sei.lpVerb = L"runas";
        sei.lpFile = exePath;
        sei.lpParameters = install ? L"--install-drivers" : L"--uninstall-drivers";
        sei.nShow = SW_HIDE;

        if (!ShellExecuteExW(&sei) || !sei.hProcess)
        {
            return false;
        }

        DWORD waitRes = WaitForSingleObject(sei.hProcess, static_cast<DWORD>(waitMs > 0 ? waitMs : 60000));
        DWORD exitCode = 1;
        if (waitRes == WAIT_OBJECT_0)
        {
            GetExitCodeProcess(sei.hProcess, &exitCode);
        }
        CloseHandle(sei.hProcess);
        return waitRes == WAIT_OBJECT_0 && exitCode == 0;
    }
}
