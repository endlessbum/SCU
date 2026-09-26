#include "system/process_scanner.h"

#include <windows.h>
#include <tlhelp32.h>

namespace scan {

void ProcessScanner::Enumerate(const std::function<void(const ProcessInfo&)>& onProcess)
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return;
    }

    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) {
        do {
            ProcessInfo info;
            info.pid = entry.th32ProcessID;
            info.parentPid = entry.th32ParentProcessID;
            info.name = entry.szExeFile;

            if (HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,
                                             FALSE, entry.th32ProcessID)) {
                wchar_t path[1024]{};
                DWORD pathLength = 1024;
                if (QueryFullProcessImageNameW(process, 0, path, &pathLength) && pathLength > 0) {
                    info.exePath = path;
                }
                CloseHandle(process);
            }

            onProcess(info);
        } while (Process32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
}

} // namespace scan
