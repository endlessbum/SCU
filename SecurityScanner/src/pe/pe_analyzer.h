#pragma once

// Базовый PE-анализатор (документ п. 9): заголовки, секции (entropy, W+X),
// overlay. Все признаки — только сигналы для score; ни один из них сам по себе
// не ставит verdict Malware (документ п. 10/16).

#include "core/types.h"

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
    static PeAnalysis Analyze(const std::wstring& path);
};

} // namespace scan
