#pragma once

// SHA-256 через CNG (bcrypt.lib) — без внешних зависимостей (документ п. 3/7).
// Основной идентификатор файла — SHA-256 hex lowercase.

#include <windows.h>

#include <string>

namespace scan {

class Sha256 {
public:
    // Потоковый хеш файла. Возвращает false при ошибке открытия/чтения, а
    // также если размер файла изменился за время хэширования (короткое чтение
    // на усохшем файле не даёт «валидный» хэш снапшота — аудит).
    // predefinedFile — уже открытый хэндл (аудит TOCTOU): хеш считается из
    // него, хэндл не закрывается (владелец — вызывающий).
    static bool HashFile(const std::wstring& path, std::string& hexOut,
                         HANDLE predefinedFile = INVALID_HANDLE_VALUE);

private:
    // Общий потоковый хеш открытого файла: позиция сбрасывается на начало,
    // хэндл не закрывается. Возвращает false при ошибке чтения.
    static bool HashHandle(HANDLE file, std::string& hexOut);
};

} // namespace scan
