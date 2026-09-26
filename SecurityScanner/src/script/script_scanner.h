#pragma once

// ScriptScanner, срез v2 (документ п. 19): собственный статический анализ
// PowerShell/BAT/VBS/JS/HTA без исполнения. AMSI не используется.
// Правила с весами; detection только при нескольких независимых сигналах
// (порог задаёт scanner.cpp). Собственные скрипты SCU проходят мимо этого
// модуля через ComponentAllowlist (проверяется раньше).

#include <string>
#include <vector>

namespace scan {

struct ScriptAnalysis {
    int score = 0;
    std::vector<std::wstring> signals;
};

class ScriptScanner {
public:
    // Анализ скрипта по пути (файл читается с лимитом размера).
    static ScriptAnalysis Analyze(const std::wstring& path);

    // Анализ текста (уже lowercase, как даёт ReadText/AppendDecodedBase64):
    // используется для команд persistence-записей (п. 17: command line).
    static ScriptAnalysis AnalyzeText(const std::string& lowerText);
};

} // namespace scan
