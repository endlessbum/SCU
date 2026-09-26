#pragma once

// ProcessScanner (документ п. 18): снимок процессов через Toolhelp32, полный
// путь образа через QueryFullProcessImageNameW (PROCESS_QUERY_LIMITED_INFORMATION
// не требует elevation). Решение принимает scanner.cpp: в срезе детект — только
// по hash-DB (один признак «AppData/Temp» не равен malware, п. 18).

#include <functional>
#include <string>

namespace scan {

struct ProcessInfo {
    unsigned long pid = 0;
    unsigned long parentPid = 0;
    std::wstring name;     // имя образа из снимка
    std::wstring exePath;  // полный путь (может быть пустым у системных)
};

class ProcessScanner {
public:
    static void Enumerate(const std::function<void(const ProcessInfo&)>& onProcess);
};

} // namespace scan
