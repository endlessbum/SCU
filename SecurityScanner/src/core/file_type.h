#pragma once

// Классификация файла по содержимому (magic bytes), а не по расширению
// (документ п. 6). Расширение учитывается только как подсказка для скриптов.

#include <string>

namespace scan {

enum class FileType { Unknown, PE, ArchiveZip, Script, Text, Other };

FileType ClassifyFile(const std::wstring& path);

} // namespace scan
