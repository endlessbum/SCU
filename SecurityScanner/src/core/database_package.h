#pragma once

// DatabasePackage (документ п. 12/30/33): установка offline-пакета базы.
// Пакет — ZIP с hashes.txt + hashes.txt.sig + db-version.json.
// Подпись: ECDSA P-256 (CNG) над SHA-256(hashes.txt), r||s 64 байта.
// Публичный ключ вшит в бинарь; приватный существует только в
// tools/database-builder (п. 33: private key никогда не в приложении).
// Применение — atomic replace: запись в .tmp в целевом каталоге,
// затем MoveFileEx(REPLACE_EXISTING) (п. 12: atomic replace).

#include <string>

namespace scan {

struct DatabaseUpdateResult {
    bool ok = false;
    std::wstring error;
    std::wstring dbVersion;
    int entries = 0;
};

class DatabasePackage {
public:
    // Проверяет и устанавливает пакет в databaseDir. Целевые файлы
    // hashes.txt / db-version.json замещаются только при полной валидности.
    static void Apply(const std::wstring& packageZip,
                      const std::wstring& databaseDir,
                      DatabaseUpdateResult& result);
};

} // namespace scan
