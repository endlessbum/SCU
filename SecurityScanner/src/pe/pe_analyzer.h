#pragma once

// Базовый PE-анализатор (документ п. 9): заголовки, секции (entropy, W+X),
// overlay. Все признаки — только сигналы для score; ни один из них сам по себе
// не ставит verdict Malware (документ п. 10/16).

#include "core/types.h"

#include <windows.h>

#include <string>
#include <vector>

namespace scan {

struct PeAnalysis {
    bool isValid = false;
    bool isPe = false;
    int score = 0;
    std::vector<std::wstring> signals;
};

class PeAnalyzer {
public:
    // Анализирует файл по пути (заголовки читает из памяти файла, entropy
    // секций считает потоково, без чтения всего файла в память).
    // predefinedFile — уже открытый хэндл (аудит TOCTOU): анализ идёт по нему,
    // хэндл не закрывается (владелец — вызывающий); INVALID_HANDLE_VALUE —
    // открыть по пути и закрыть как раньше.
    static PeAnalysis Analyze(const std::wstring& path, HANDLE predefinedFile = INVALID_HANDLE_VALUE);
};

} // namespace scan
