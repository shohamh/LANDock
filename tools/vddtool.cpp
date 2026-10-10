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
#include <initguid.h>
#include "third-party/sudovda/sudovda.h"
#include "src/utility.h"
#include "src/uuid.h"

using namespace SUDOVDA;

int main(int argc, char **argv) {
  if (argc < 2) {
    std::fprintf(stderr, "usage: vddtool watchdog | remove <uuid>...\n");
    return 2;
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
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    for (int i = 2; i < argc; ++i) {
      std::string a = argv[i];
      auto eq = a.find('='), cm = a.find(',');
      if (eq == std::string::npos || cm == std::string::npos) { std::fprintf(stderr, "bad arg %s\n", a.c_str()); return 2; }
      std::wstring name = L"\\\\.\\" + std::wstring(a.begin(), a.begin() + eq);
      LONG x = std::stol(a.substr(eq + 1, cm - eq - 1)), y = std::stol(a.substr(cm + 1));
      DEVMODEW dm {}; dm.dmSize = sizeof(dm);
      if (!EnumDisplaySettingsW(name.c_str(), ENUM_CURRENT_SETTINGS, &dm)) { std::printf("%ls: cannot read settings\n", name.c_str()); continue; }
      dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
      dm.dmPosition.x = x; dm.dmPosition.y = y;
      LONG r = ChangeDisplaySettingsExW(name.c_str(), &dm, nullptr, CDS_UPDATEREGISTRY | CDS_NORESET, nullptr);
      std::printf("%ls -> (%ld,%ld) = %ld\n", name.c_str(), x, y, r);
    }
    LONG r = ChangeDisplaySettingsExW(nullptr, nullptr, nullptr, 0, nullptr);
    std::printf("apply = %ld\n", r);
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
