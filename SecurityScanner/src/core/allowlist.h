#pragma once

// ComponentAllowlist (документ п. 29): неудаляемый встроенный allowlist
// собственных компонентов SCU. Build-time часть — SHA-256 скриптов из
// Assets (генерируется cmake/component_allowlist.cmake), runtime-часть —
// хеши ScannerCore.exe и SCU.exe рядом со сканером.
// Список не редактируется пользователем и не сохраняется в файл.

#include <string>

#include <unordered_set>

namespace scan {

class ComponentAllowlist {
public:
    ComponentAllowlist();

    // Заполнить runtime-часть: собственный exe и SCU.exe из директории сканера.
    // Ошибки вычисления хеша не фатальны — просто не добавляются.
    void AddRuntimeComponents();

    bool Contains(const std::string& sha256Hex) const;

private:
    std::unordered_set<std::string> hashes_;
};

} // namespace scan
