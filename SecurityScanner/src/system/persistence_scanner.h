#pragma once

// PersistenceScanner (документ п. 17): единый обход автозапуска — Run/RunOnce
// (HKCU+HKLM), Winlogon Shell/Userinit, Startup-папки, службы (ImagePath),
// Scheduled Tasks (файлы XML в System32\Tasks). Каждый entry — точное
// происхождение (location, name, command, targetPath); решение по нему
// принимает scanner.cpp (корреляция с анализом цели, п. 18).

#include <functional>
#include <string>
#include <vector>

namespace scan {

struct PersistenceEntry {
    std::wstring location;   // например HKCU\...\Run или service/task источник
    std::wstring name;       // имя значения / службы / задачи / файла
    std::wstring command;    // полная команда (может быть пустой)
    std::wstring targetPath; // разрешённый путь exe (может быть пустым)
};

class PersistenceScanner {
public:
    void Enumerate(const std::function<void(const PersistenceEntry&)>& onEntry);
};

} // namespace scan
