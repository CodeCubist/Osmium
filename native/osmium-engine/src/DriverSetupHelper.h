#pragma once

#include "OsmiumCommon.h"
#include <string>

namespace osmium
{
    class DriverSetupHelper
    {
    public:
        static bool IsProcessElevated();
        static bool IsRunningAsSystem();

        static std::wstring GetDriversBaseDir();

        static bool IsDeviceInstalled(const wchar_t* hardwareId, bool* outIsRunning = nullptr);

        static bool TrustCatalogSignerCert(const std::wstring& catPath);

        static bool InstallRootDriver(const std::wstring& infPath, const wchar_t* hardwareId,
                                      const std::wstring& catPath = L"");

        static bool SetDeviceEnabled(const wchar_t* hardwareId, bool enable);

        static bool RemoveDeviceNode(const wchar_t* hardwareId);

        static bool UninstallRootDriver(const wchar_t* hardwareId, const wchar_t* infOriginalNameHint);

        static bool RunElevatedSelfDriverOp(bool install, int waitMs = 60000);
    };
}
