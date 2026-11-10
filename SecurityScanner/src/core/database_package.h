#pragma once

// DatabasePackage (документ п. 12/30/33): установка offline-пакета базы.
// Пакет — ZIP с hashes.txt + hashes.txt.sig + db-version.json (все три члена
// обязательны). Подпись: ECDSA P-256 (CNG) над составным дайджестом
// SHA-256(hashes.txt || db-version.json) — метаданные версии входят в
// подписанный payload наравне с базой (аудит п. 11), r||s 64 байта.
// Публичный ключ вшит в бинарь; приватный существует только в
// tools/database-builder (п. 33: private key никогда не в приложении).
// Установка — единым состоянием (аудит 2, п. 16): полная генерация в
// databaseDir\generations\<id>, атомарный commit-поинтер current.json;
// hashes.txt/db-version.json в корне — best-effort легаси-зеркало.
// Легаси-раскладка (без current.json) продолжает читаться напрямую.

#include <cstddef>

#include <string>

namespace scan {

// Верификация подписи базы (та же схема, что в пакете): ECDSA P-256 над
// SHA-256(hashes || version). Используется и DatabasePackage::Apply, и
// HashDatabase при загрузке активной базы с диска (OBS-05 п. 3,
// аудит 2026-11-04). Возвращает false на любой ошибке — fail-closed.
bool VerifyDatabaseSignature(const unsigned char* hashes, size_t hashesSize,
                             const unsigned char* version, size_t versionSize,
                             const unsigned char* signature, size_t signatureSize);

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
