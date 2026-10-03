#include "system/process_scanner.h"

#include "core/path_util.h"

#include <windows.h>
#include <tlhelp32.h>

namespace scan {

bool ProcessScanner::Enumerate(const std::function<void(const ProcessInfo&)>& onProcess)
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return false;
    }

    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (!Process32FirstW(snapshot, &entry)) {
        CloseHandle(snapshot);
        return false;
    }

    do {
        ProcessInfo info;
        info.pid = entry.th32ProcessID;
        info.parentPid = entry.th32ParentProcessID;
        info.name = entry.szExeFile;

        if (HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,
                                         FALSE, entry.th32ProcessID)) {
            // Динамический путь (аудит п. 9): путь длиннее 1024 не усекается
            // молча — усечённый путь не отличим от полного и ломает проверки.
            info.exePath = GetProcessImagePathDynamic(process);
            CloseHandle(process);
        }

        onProcess(info);
    } while (Process32NextW(snapshot, &entry));

    CloseHandle(snapshot);
    return true;
}

} // namespace scan
