// Заглушка для сборки вне CMake-экспорта miniz (репозиторий генерирует этот
// файл при сборке); экспорт символов нам не нужен — статическая линковка.
#pragma once

#ifndef MINIZ_EXPORT
#define MINIZ_EXPORT
#endif
