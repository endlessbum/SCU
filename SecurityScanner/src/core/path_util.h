#pragma once

// Динамические path-хелперы (аудит п. 9): заменяют фиксированные MAX_PATH/1024
// буферы — при недостаточном размере буфер удваивается и вызов повторяется,
// вместо молчаливого усечения длинных путей (потеря coverage скана).

#include <string>
#include <windows.h>

namespace scan {

// Полный путь собственного исполняемого файла; "" при ошибке.
std::wstring GetModulePathDynamic();

// Путь образа процесса через QueryFullProcessImageNameW; "" при ошибке.
std::wstring GetProcessImagePathDynamic(HANDLE process);

// ExpandEnvironmentStringsW с ростом буфера по документированному
// возвращаемому размеру; "" при ошибке.
std::wstring ExpandEnvironmentDynamic(const std::wstring& value);

// Значение переменной окружения; "" при отсутствии/ошибке.
std::wstring GetEnvironmentValueDynamic(const wchar_t* name);

// Временный каталог через GetTempPathW с ростом буфера (с завершающим
// разделителем); "" при ошибке.
std::wstring GetTempPathDynamic();

// Родительский каталог пути (без завершающего разделителя); путь без
// разделителя даёт пустой результат (fail-closed у вызывающих).
std::wstring DirectoryOf(const std::wstring& path);

// Создание каталога без следования по reparse-точкам (junction/symlink):
// ERROR_SUCCESS — создан или уже существует как обычный каталог; существующий
// путь с FILE_ATTRIBUTE_REPARSE_POINT (или не-каталог) — ERROR_ACCESS_DENIED,
// иначе — код ошибки CreateDirectoryW. Общий хелпер (аудит): archive_scanner
// (временные каталоги) и database_package (generations, junction-гонка в
// user-writable portable-установке).
DWORD CreateDirectoryRecursiveNoReparse(const std::wstring& path);

} // namespace scan
