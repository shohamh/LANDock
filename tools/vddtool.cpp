// vddtool: talk to the SudoVDA driver. Built with MSYS2 UCRT64 g++ (see tools/build-vddtool.sh).
//   vddtool watchdog            print the driver watchdog timeout/countdown
//   vddtool remove <uuid>...    remove the virtual display Apollo would have created for that client uuid
#include <algorithm>
#include <cstdio>
#include <vector>
#include <cstring>
#include <iostream>
#include <string>
#include <windows.h>
#include <setupapi.h>
#include <newdev.h>
#include <initguid.h>
#include "third-party/sudovda/sudovda.h"
#include "src/utility.h"
#include "src/uuid.h"

using namespace SUDOVDA;

// ---- the driver: installs SudoVDA's package and creates its device. The Apollo installer does the same with nefconc.exe; here it is
// Windows' own setup API, so no other program is needed.
static const wchar_t *SUDOVDA_HWID = L"Root\\SudoMaker\\SudoVDA";   // the hardware id in SudoVDA.inf
static const GUID SUDOVDA_CLASS = {0x4D36E968, 0xE325, 0x11CE, {0xBF, 0xC1, 0x08, 0x00, 0x2B, 0xE1, 0x03, 0x18}};   // Display

// the SudoVDA device node if Windows has one (its set stays open for the caller to close); INVALID_HANDLE_VALUE when there is none
static HDEVINFO findSudoVdaDevice(SP_DEVINFO_DATA &dev) {
	HDEVINFO set = SetupDiGetClassDevsW(&SUDOVDA_CLASS, nullptr, nullptr, 0);
	if (set == INVALID_HANDLE_VALUE) return set;
	for (DWORD i = 0;; ++i) {
		SP_DEVINFO_DATA d{};
		d.cbSize = sizeof(d);
		if (!SetupDiEnumDeviceInfo(set, i, &d)) break;
		wchar_t ids[1024]{};
		DWORD type = 0, need = 0;
		if (!SetupDiGetDeviceRegistryPropertyW(set, &d, SPDRP_HARDWAREID, &type, reinterpret_cast<PBYTE>(ids), sizeof(ids), &need)) continue;
		for (const wchar_t *p = ids; *p; p += wcslen(p) + 1) {
			if (_wcsicmp(p, SUDOVDA_HWID) == 0) {
				dev = d;
				return set;
			}
		}
	}
	SetupDiDestroyDeviceInfoList(set);
	return INVALID_HANDLE_VALUE;
}

static std::wstring toWide(const char *s) {
	int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
	std::wstring w(n > 0 ? n - 1 : 0, L'\0');
	if (n > 1) MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
	return w;
}

static bool sameFile(const std::wstring &a, const std::wstring &b) {
	HANDLE ha = CreateFileW(a.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
	HANDLE hb = CreateFileW(b.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
	bool same = false;
	if (ha != INVALID_HANDLE_VALUE && hb != INVALID_HANDLE_VALUE) {
		LARGE_INTEGER sa{}, sb{};
		GetFileSizeEx(ha, &sa);
		GetFileSizeEx(hb, &sb);
		if (sa.QuadPart == sb.QuadPart && sa.QuadPart > 0) {
			std::vector<char> ba(static_cast<size_t>(sa.QuadPart)), bb(ba.size());
			DWORD ra = 0, rb = 0;
			same = ReadFile(ha, ba.data(), static_cast<DWORD>(ba.size()), &ra, nullptr) && ReadFile(hb, bb.data(), static_cast<DWORD>(bb.size()), &rb, nullptr) && ra == rb && ba == bb;
		}
	}
	if (ha != INVALID_HANDLE_VALUE) CloseHandle(ha);
	if (hb != INVALID_HANDLE_VALUE) CloseHandle(hb);
	return same;
}

// the driver store copies of the package (oemNN.inf in the INF folder) that are this INF: Windows names them, and the names
// differ on every PC, so they are found by content
static std::vector<std::wstring> driverStoreCopies(const std::wstring &inf) {
	std::vector<std::wstring> found;
	wchar_t windir[MAX_PATH]{};
	GetWindowsDirectoryW(windir, MAX_PATH);
	std::wstring pattern = std::wstring(windir) + L"\\INF\\oem*.inf";
	WIN32_FIND_DATAW f{};
	HANDLE h = FindFirstFileW(pattern.c_str(), &f);
	if (h == INVALID_HANDLE_VALUE) return found;
	do {
		std::wstring path = std::wstring(windir) + L"\\INF\\" + f.cFileName;
		if (sameFile(path, inf)) found.push_back(f.cFileName);
	} while (FindNextFileW(h, &f));
	FindClose(h);
	return found;
}

// driver-install <folder with SudoVDA.inf>: the package into the driver store, the device node (once), and the driver bound to it
static int driverInstall(const std::wstring &dir) {
	std::wstring inf = dir + L"\\SudoVDA.inf";
	if (GetFileAttributesW(inf.c_str()) == INVALID_FILE_ATTRIBUTES) {
		wprintf(L"no SudoVDA.inf in %ls\n", dir.c_str());
		return 2;
	}
	if (!SetupCopyOEMInfW(inf.c_str(), nullptr, SPOST_PATH, 0, nullptr, 0, nullptr, nullptr)) {
		wprintf(L"the driver package could not be added (error %lu): run as administrator\n", GetLastError());
		return 1;
	}
	SP_DEVINFO_DATA dev{};
	HDEVINFO set = findSudoVdaDevice(dev);
	if (set == INVALID_HANDLE_VALUE) {
		set = SetupDiCreateDeviceInfoList(&SUDOVDA_CLASS, nullptr);
		if (set == INVALID_HANDLE_VALUE) return 1;
		SP_DEVINFO_DATA d{};
		d.cbSize = sizeof(d);
		if (!SetupDiCreateDeviceInfoW(set, L"Display", &SUDOVDA_CLASS, nullptr, nullptr, DICD_GENERATE_ID, &d)) {
			wprintf(L"the device could not be created (error %lu)\n", GetLastError());
			SetupDiDestroyDeviceInfoList(set);
			return 1;
		}
		std::wstring hwid = SUDOVDA_HWID;
		hwid.push_back(L'\0');   // a multi-string: ends with an empty string
		hwid.push_back(L'\0');
		if (!SetupDiSetDeviceRegistryPropertyW(set, &d, SPDRP_HARDWAREID, reinterpret_cast<const BYTE *>(hwid.data()), static_cast<DWORD>(hwid.size() * sizeof(wchar_t))) ||
		    !SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, &d)) {
			wprintf(L"the device could not be registered (error %lu)\n", GetLastError());
			SetupDiDestroyDeviceInfoList(set);
			return 1;
		}
		wprintf(L"device created\n");
	}
	SetupDiDestroyDeviceInfoList(set);
	BOOL reboot = FALSE;
	if (!UpdateDriverForPlugAndPlayDevicesW(nullptr, SUDOVDA_HWID, inf.c_str(), INSTALLFLAG_FORCE, &reboot)) {
		wprintf(L"the driver could not be bound to the device (error %lu)\n", GetLastError());
		return 1;
	}
	wprintf(L"driver installed%ls\n", reboot ? L"; a restart is needed" : L"");
	return 0;
}

// driver-remove <folder with SudoVDA.inf>: the device node, then the package from the driver store
static int driverRemove(const std::wstring &dir) {
	std::wstring inf = dir + L"\\SudoVDA.inf";
	SP_DEVINFO_DATA dev{};
	int failed = 0;
	HDEVINFO set = findSudoVdaDevice(dev);
	if (set != INVALID_HANDLE_VALUE) {
		if (SetupDiCallClassInstaller(DIF_REMOVE, set, &dev)) wprintf(L"device removed\n");
		else {
			wprintf(L"the device could not be removed (error %lu): run as administrator\n", GetLastError());
			failed++;
		}
		SetupDiDestroyDeviceInfoList(set);
	}
	std::vector<std::wstring> copies = driverStoreCopies(inf);
	if (copies.empty()) wprintf(L"no driver package of SudoVDA is in the driver store\n");
	for (const std::wstring &copy : copies) {
		if (SetupUninstallOEMInfW(copy.c_str(), SUOI_FORCEDELETE, nullptr)) wprintf(L"driver package %ls removed\n", copy.c_str());
		else {
			wprintf(L"driver package %ls could not be removed (error %lu): run as administrator\n", copy.c_str(), GetLastError());
			failed++;
		}
	}
	return failed == 0 ? 0 : 1;
}

int main(int argc, char **argv) {
  if (argc < 2) {
    std::fprintf(stderr, "usage: vddtool watchdog | remove <uuid>...\n");
    return 2;
  }
  // the driver commands run before the device is opened: it does not exist until they have run
  if (std::string(argv[1]) == "driver-install" || std::string(argv[1]) == "driver-remove") {
    if (argc < 3) {
      std::fprintf(stderr, "usage: vddtool %s <folder with SudoVDA.inf>\n", argv[1]);
      return 2;
    }
    std::wstring dir = toWide(argv[2]);
    return std::string(argv[1]) == "driver-install" ? driverInstall(dir) : driverRemove(dir);
  }
  HANDLE h = OpenDevice(&SUVDA_INTERFACE_GUID);
  if (h == INVALID_HANDLE_VALUE) {
    std::fprintf(stderr, "cannot open SudoVDA device\n");
    return 1;
  }
  std::string cmd = argv[1];
  if (cmd == "watchdog") {
    VIRTUAL_DISPLAY_GET_WATCHDOG_OUT out {};
    if (!GetWatchdogTimeout(h, out)) return 1;
    std::printf("watchdog timeout=%u countdown=%u\n", out.Timeout, out.Countdown);
    return 0;
  }
  if (cmd == "screens") {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    DISPLAY_DEVICEW dd {}; dd.cb = sizeof(dd);
    for (DWORD i = 0; EnumDisplayDevicesW(nullptr, i, &dd, 0); ++i) {
      if (!(dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP)) continue;
      DEVMODEW dm {}; dm.dmSize = sizeof(dm);
      if (!EnumDisplaySettingsW(dd.DeviceName, ENUM_CURRENT_SETTINGS, &dm)) continue;
      std::printf("%ls pos=%ld,%ld size=%lux%lu hz=%lu orient=%lu%s", dd.DeviceName, dm.dmPosition.x, dm.dmPosition.y,
        dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmDisplayOrientation, (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) ? " primary" : "");
      std::printf("%s", "\n");
    }
    return 0;
  }
  if (cmd == "layout") {
    // vddtool layout <DISPLAYn>=<x>,<y> ...   (e.g. DISPLAY6=1920,0)
    // the same lock as the program's display changes (see DisplayConfigLock in the Apollo source): a configuration write of another
    // instance in between would put back the old positions
    HANDLE configLock = CreateMutexW(nullptr, FALSE, L"Local\\LANDockDisplayConfig");
    if (configLock) WaitForSingleObject(configLock, 30000);
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    for (int i = 2; i < argc; ++i) {
      std::string a = argv[i];
      auto eq = a.find('='), cm = a.find(',');
      if (eq == std::string::npos || cm == std::string::npos) { std::fprintf(stderr, "bad arg %s\n", a.c_str()); return 2; }
      std::wstring name = L"\\\\.\\" + std::wstring(a.begin(), a.begin() + eq);
      LONG x = std::stol(a.substr(eq + 1, cm - eq - 1)), y = std::stol(a.substr(cm + 1));
      DEVMODEW dm {}; dm.dmSize = sizeof(dm);
      if (!EnumDisplaySettingsW(name.c_str(), ENUM_CURRENT_SETTINGS, &dm)) { std::printf("%ls: cannot read settings\n", name.c_str()); continue; }
      // the orientation is part of the mode: without DM_DISPLAYORIENTATION Windows resets a portrait display to landscape
      // (dm already holds the current orientation, so it is kept)
      dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYORIENTATION;
      dm.dmPosition.x = x; dm.dmPosition.y = y;
      LONG r = ChangeDisplaySettingsExW(name.c_str(), &dm, nullptr, CDS_UPDATEREGISTRY | CDS_NORESET, nullptr);
      std::printf("%ls -> (%ld,%ld) = %ld\n", name.c_str(), x, y, r);
    }
    LONG r = ChangeDisplaySettingsExW(nullptr, nullptr, nullptr, 0, nullptr);
    std::printf("apply = %ld\n", r);
    if (configLock) {
      ReleaseMutex(configLock);
      CloseHandle(configLock);
    }
    return r == DISP_CHANGE_SUCCESSFUL ? 0 : 1;
  }
  if (cmd == "clone") {
    LONG r = SetDisplayConfig(0, nullptr, 0, nullptr, SDC_APPLY | SDC_TOPOLOGY_CLONE);
    std::printf("SetDisplayConfig(clone) = %ld\n", r);
    return r == ERROR_SUCCESS ? 0 : 1;
  }
  if (cmd == "extend") {
    LONG r = SetDisplayConfig(0, nullptr, 0, nullptr, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
    std::printf("SetDisplayConfig(extend) = %ld\n", r);
    return r == ERROR_SUCCESS ? 0 : 1;
  }
  if (cmd == "paths") {
    UINT32 np = 0, nm = 0;
    if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, &np, &nm) != ERROR_SUCCESS) return 1;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths(np);
    std::vector<DISPLAYCONFIG_MODE_INFO> modes(nm);
    if (QueryDisplayConfig(QDC_ALL_PATHS, &np, paths.data(), &nm, modes.data(), nullptr) != ERROR_SUCCESS) return 1;
    for (UINT32 i = 0; i < np; ++i) {
      auto &p = paths[i];
      bool active = p.flags & DISPLAYCONFIG_PATH_ACTIVE;
      if (!active && !p.targetInfo.targetAvailable) continue;
      std::printf("path%u adapter=%08lx:%08lx src=%u tgt=%u active=%d available=%d tech=%d",
        i, (unsigned long) p.targetInfo.adapterId.HighPart, (unsigned long) p.targetInfo.adapterId.LowPart,
        p.sourceInfo.id, p.targetInfo.id, (int) active, (int) p.targetInfo.targetAvailable, (int) p.targetInfo.outputTechnology);
      DISPLAYCONFIG_TARGET_DEVICE_NAME tn {};
      tn.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
      tn.header.size = sizeof(tn);
      tn.header.adapterId = p.targetInfo.adapterId;
      tn.header.id = p.targetInfo.id;
      if (DisplayConfigGetDeviceInfo(&tn.header) == ERROR_SUCCESS) std::printf(" name=%ls", tn.monitorFriendlyDeviceName);
      std::printf("%s", "\n");
    }
    return 0;
  }
  if (cmd == "remove") {
    int removed = 0;
    for (int i = 2; i < argc; ++i) {
      std::string s = argv[i];
      auto u = uuid_util::uuid_t::parse(s);
      GUID g;
      std::memcpy(&g, &u, sizeof(GUID));
      bool ok = RemoveVirtualDisplay(h, g);
      std::printf("%s %s\n", s.c_str(), ok ? "removed" : "not-removed");
      removed += ok;
    }
    std::printf("removed=%d\n", removed);
    return 0;
  }
  std::fprintf(stderr, "unknown command\n");
  return 2;
}
