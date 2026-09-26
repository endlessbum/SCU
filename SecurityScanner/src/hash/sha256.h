#pragma once

// SHA-256 через CNG (bcrypt.lib) — без внешних зависимостей (документ п. 3/7).
// Основной идентификатор файла — SHA-256 hex lowercase.

#include <string>

namespace scan {

class Sha256 {
public:
    // Потоковый хеш файла. Возвращает false при ошибке открытия/чтения.
    static bool HashFile(const std::wstring& path, std::string& hexOut);
};

} // namespace scan
