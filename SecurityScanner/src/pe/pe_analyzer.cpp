#include "pe/pe_analyzer.h"

#include <windows.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

namespace scan {

namespace {

// Порог высокой entropy секции (документ п. 10: high entropy — только фича;
// упаковщики и компрессоры легитимно дают значения выше порога).
constexpr double kHighEntropyThreshold = 7.2;
constexpr unsigned long long kLargeOverlayBytes = 10ull * 1024 * 1024;

struct SectionInfo {
    DWORD virtualSize = 0;
    DWORD rawPointer = 0;
    DWORD rawSize = 0;
    DWORD characteristics = 0;
    char name[9]{};
};

// Shannon entropy потока байтов (0..8).
double Entropy(const unsigned char* data, size_t size)
{
    if (size == 0) {
        return 0.0;
    }
    unsigned counts[256]{};
    for (size_t i = 0; i < size; ++i) {
        counts[data[i]]++;
    }
    double entropy = 0.0;
    for (const unsigned count : counts) {
        if (count == 0) {
            continue;
        }
        const double p = static_cast<double>(count) / static_cast<double>(size);
        entropy -= p * std::log2(p);
    }
    return entropy;
}

double SectionEntropy(HANDLE file, const SectionInfo& section)
{
    if (section.rawSize == 0) {
        return 0.0;
    }

    LARGE_INTEGER position{};
    position.QuadPart = static_cast<long long>(section.rawPointer);
    if (!SetFilePointerEx(file, position, nullptr, FILE_BEGIN)) {
        return 0.0;
    }

    constexpr DWORD kChunk = 256 * 1024;
    unsigned counts[256]{};
    std::vector<unsigned char> buffer(kChunk);
    unsigned long long total = 0;
    unsigned long long remaining = section.rawSize;

    while (remaining > 0) {
        const DWORD toRead = static_cast<DWORD>(std::min<unsigned long long>(kChunk, remaining));
        DWORD read = 0;
        if (!ReadFile(file, buffer.data(), toRead, &read, nullptr) || read == 0) {
            break;
        }
        for (DWORD i = 0; i < read; ++i) {
            counts[buffer[i]]++;
        }
        total += read;
        remaining -= read;
    }

    if (total == 0) {
        return 0.0;
    }

    double entropy = 0.0;
    for (const unsigned count : counts) {
        if (count == 0) {
            continue;
        }
        const double p = static_cast<double>(count) / static_cast<double>(total);
        entropy -= p * std::log2(p);
    }
    return entropy;
}

} // namespace

PeAnalysis PeAnalyzer::Analyze(const std::wstring& path)
{
    PeAnalysis result;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return result;
    }

    LARGE_INTEGER fileSize{};
    if (!GetFileSizeEx(file, &fileSize) || fileSize.QuadPart < 64) {
        CloseHandle(file);
        return result;
    }
    const unsigned long long totalSize = static_cast<unsigned long long>(fileSize.QuadPart);

    // Заголовки: DOS header + NT headers + section table помещаются в 64 КБ.
    constexpr DWORD kHeaderSize = 64 * 1024;
    std::vector<unsigned char> header(kHeaderSize);
    DWORD read = 0;
    LARGE_INTEGER zero{};
    if (!SetFilePointerEx(file, zero, nullptr, FILE_BEGIN)
        || !ReadFile(file, header.data(), kHeaderSize, &read, nullptr) || read < 64) {
        CloseHandle(file);
        return result;
    }

    const auto* dosHeader = reinterpret_cast<const IMAGE_DOS_HEADER*>(header.data());
    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE) {
        CloseHandle(file);
        return result;
    }

    const DWORD ntOffset = static_cast<DWORD>(dosHeader->e_lfanew);
    if (ntOffset == 0 || ntOffset + sizeof(IMAGE_NT_HEADERS) > read) {
        CloseHandle(file);
        return result;
    }

    const auto* ntHeaders = reinterpret_cast<const IMAGE_NT_HEADERS*>(header.data() + ntOffset);
    if (ntHeaders->Signature != IMAGE_NT_SIGNATURE) {
        CloseHandle(file);
        return result;
    }

    result.isPe = true;

    const WORD sectionCount = ntHeaders->FileHeader.NumberOfSections;
    const DWORD sectionTableOffset = ntOffset + FIELD_OFFSET(IMAGE_NT_HEADERS, OptionalHeader)
                                     + ntHeaders->FileHeader.SizeOfOptionalHeader;
    if (sectionTableOffset + static_cast<DWORD>(sectionCount) * sizeof(IMAGE_SECTION_HEADER) > read) {
        // Некорректная таблица секций: malformed PE — только счётчик, не crash
        // (документ п. 22: безопасная обработка повреждённых PE).
        result.score += 1;
        result.signals.push_back(L"malformed-section-table");
        CloseHandle(file);
        result.isValid = true;
        return result;
    }

    unsigned long long endOfRaw = 0;
    for (WORD i = 0; i < sectionCount; ++i) {
        const auto* raw = reinterpret_cast<const IMAGE_SECTION_HEADER*>(
            header.data() + sectionTableOffset + static_cast<size_t>(i) * sizeof(IMAGE_SECTION_HEADER));

        SectionInfo section{};
        section.virtualSize = raw->Misc.VirtualSize;
        section.rawPointer = raw->PointerToRawData;
        section.rawSize = raw->SizeOfRawData;
        section.characteristics = raw->Characteristics;
        memcpy(section.name, raw->Name, sizeof(section.name) - 1);
        section.name[sizeof(section.name) - 1] = '\0';

        const bool writable = (section.characteristics & IMAGE_SCN_MEM_WRITE) != 0;
        const bool executable = (section.characteristics & IMAGE_SCN_MEM_EXECUTE) != 0;
        const bool isCode = (section.characteristics & IMAGE_SCN_CNT_CODE) != 0;

        // W+X секция с ненулевым размером: самозапаковка/самомодификация.
        // Частый признак packer'ов, но бывает и у легитимных — только сигнал.
        if (writable && executable && section.rawSize > 0) {
            result.score += 2;
            result.signals.push_back(L"writable-executable-section:" + std::wstring(section.name, section.name + strlen(section.name)));
        }

        if (isCode || executable) {
            const double entropy = SectionEntropy(file, section);
            if (entropy >= kHighEntropyThreshold) {
                result.score += 2;
                char entropyText[16];
                snprintf(entropyText, sizeof(entropyText), "%.2f", entropy);
                result.signals.push_back(L"high-entropy-executable-section:" + std::wstring(section.name, section.name + strlen(section.name))
                                         + L" (" + std::wstring(entropyText, entropyText + strlen(entropyText)) + L")");
            }
        }

        endOfRaw = std::max<unsigned long long>(
            endOfRaw, static_cast<unsigned long long>(section.rawPointer) + section.rawSize);
    }

    // Overlay: данные после последней секции. Большой overlay у исполнимых
    // файлов — типичное место внедрения payload'ов.
    if (endOfRaw < totalSize) {
        const unsigned long long overlaySize = totalSize - endOfRaw;
        if (overlaySize >= kLargeOverlayBytes && overlaySize * 2 >= totalSize) {
            result.score += 1;
            char sizeText[32];
            snprintf(sizeText, sizeof(sizeText), "%llu", overlaySize / (1024 * 1024));
            result.signals.push_back(L"large-overlay:" + std::wstring(sizeText, sizeText + strlen(sizeText)) + L"MB");
        }
    }

    result.isValid = true;
    CloseHandle(file);
    return result;
}

} // namespace scan
