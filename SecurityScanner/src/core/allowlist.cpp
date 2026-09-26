#include "core/allowlist.h"

#include "component_allowlist.h"
#include "hash/sha256.h"

#include <windows.h>

namespace scan {

namespace {

std::wstring DirectoryOf(const std::wstring& path)
{
    const size_t slash = path.find_last_of(L'\\');
    return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
}

} // namespace

ComponentAllowlist::ComponentAllowlist()
{
    for (size_t i = 0; i < kAllowlistEntryCount; ++i) {
        hashes_.insert(kAllowlistSha256[i]);
    }
}

void ComponentAllowlist::AddRuntimeComponents()
{
    wchar_t pathBuffer[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, pathBuffer, MAX_PATH) == 0) {
        return;
    }

    std::string hex;
    const std::wstring selfPath = pathBuffer;
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
