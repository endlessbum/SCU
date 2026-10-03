#include "core/allowlist.h"

#include "component_allowlist.h"
#include "core/path_util.h"
#include "hash/sha256.h"

#include <windows.h>

namespace scan {

ComponentAllowlist::ComponentAllowlist()
{
    for (size_t i = 0; i < kAllowlistEntryCount; ++i) {
        hashes_.insert(kAllowlistSha256[i]);
    }
}

void ComponentAllowlist::AddRuntimeComponents()
{
    // Динамический путь (аудит п. 9): длинный путь установки не усекается.
    const std::wstring selfPath = GetModulePathDynamic();
    if (selfPath.empty()) {
        return;
    }

    std::string hex;
    if (Sha256::HashFile(selfPath, hex)) {
        hashes_.insert(hex);
    }

    // SCU.exe лежит рядом со ScannerCore.exe в установочной директории.
    const std::wstring appPath = DirectoryOf(selfPath) + L"\\SCU.exe";
    if (Sha256::HashFile(appPath, hex)) {
        hashes_.insert(hex);
    }
}

bool ComponentAllowlist::Contains(const std::string& sha256Hex) const
{
    return hashes_.count(sha256Hex) > 0;
}

} // namespace scan
