#pragma once

// Классификация файла по содержимому (magic bytes), а не по расширению
// (документ п. 6). Расширение учитывается только как подсказка для скриптов.

#include <windows.h>

#include <string>

namespace scan {

enum class FileType { Unknown, PE, ArchiveZip, Script, Text, Other };

// predefinedFile — уже открытый хэндл (аудит TOCTOU): magic читается из него,
// хэндл не закрывается (владелец — вызывающий); INVALID_HANDLE_VALUE —
// открыть по пути и закрыть как раньше.
FileType ClassifyFile(const std::wstring& path, HANDLE predefinedFile = INVALID_HANDLE_VALUE);

} // namespace scan
