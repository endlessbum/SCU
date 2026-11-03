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

PeAnalysis PeAnalyzer::Analyze(const std::wstring& path, HANDLE predefinedFile)
{
    PeAnalysis result;
    // Предопределённый хэндл (аудит TOCTOU) принадлежит вызывающему: анализ
    // идёт по нему, закрывается только собственный хэндл.
    const bool ownsFile = predefinedFile == INVALID_HANDLE_VALUE;
    HANDLE file = ownsFile
        ? CreateFileW(path.c_str(), GENERIC_READ,
                      FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                      nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr)
        : predefinedFile;
    if (file == INVALID_HANDLE_VALUE) {
        return result;
    }

    LARGE_INTEGER fileSize{};
    if (!GetFileSizeEx(file, &fileSize) || fileSize.QuadPart < 64) {
        if (ownsFile) {
            CloseHandle(file);
        }
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
        if (ownsFile) {
            CloseHandle(file);
        }
        return result;
    }

    const auto* dosHeader = reinterpret_cast<const IMAGE_DOS_HEADER*>(header.data());
    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE) {
        if (ownsFile) {
            CloseHandle(file);
        }
        return result;
    }

    // Проверки границ — от длины буфера, без ad-hoc сложений (аудит PE):
    // «offset + size > read» выглядит безопасно, но ломается при переносе на
    // 32-битный size_t или смене типов; вычитание не переполняется никогда.
    const DWORD ntOffset = static_cast<DWORD>(dosHeader->e_lfanew);
    // Аудит: прежний bounds-check по sizeof(IMAGE_NT_HEADERS) (64-битный
    // вариант, 264 байта) отбрасывал валидные PE32 с e_lfanew в хвосте
    // 64-КиБ буфера. До чтения OptionalHeader.Magic гарантируем только
    // Signature + FileHeader + Magic, затем дискретизация по Magic:
    // PE32 (0x10B) → IMAGE_NT_HEADERS32, PE32+ (0x20B) → IMAGE_NT_HEADERS64.
    constexpr DWORD kNtHeaderMinimum = FIELD_OFFSET(IMAGE_NT_HEADERS, FileHeader)
        + sizeof(IMAGE_FILE_HEADER) + 2; // 2 байта — OptionalHeader.Magic
    if (ntOffset == 0 || ntOffset > read || kNtHeaderMinimum > read - ntOffset) {
        if (ownsFile) {
            CloseHandle(file);
        }
        return result;
    }

    const auto* ntBase = header.data() + ntOffset;
    if (*reinterpret_cast<const DWORD*>(ntBase) != IMAGE_NT_SIGNATURE) {
        if (ownsFile) {
            CloseHandle(file);
        }
        return result;
    }

    const WORD optionalMagic = *reinterpret_cast<const WORD*>(
        ntBase + FIELD_OFFSET(IMAGE_NT_HEADERS, FileHeader) + sizeof(IMAGE_FILE_HEADER));
    if (optionalMagic != IMAGE_NT_OPTIONAL_HDR32_MAGIC
        && optionalMagic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
        // ROM-image (0x107) и неизвестные форматы не анализируем.
        if (ownsFile) {
            CloseHandle(file);
        }
        return result;
    }
    const bool isPe32Plus = optionalMagic == IMAGE_NT_OPTIONAL_HDR64_MAGIC;

    // Полный вариант-специфичный заголовок обязан помещаться в буфер: для PE32+
    // поведение прежнее (sizeof(IMAGE_NT_HEADERS) == sizeof(IMAGE_NT_HEADERS64)),
    // PE32 требует 248 байт вместо 264 и теперь принимается в хвосте. Поля
    // OptionalHeader (AddressOfEntryPoint, ImageBase и т.п. — у PE32 они
    // 32-битные) ниже не читаются; любой будущий доступ — только через
    // вариантный тип, иначе значения PE32 исказятся.
    const size_t ntHeadersSize = isPe32Plus ? sizeof(IMAGE_NT_HEADERS64) : sizeof(IMAGE_NT_HEADERS32);
    if (ntHeadersSize > read - ntOffset) {
        if (ownsFile) {
            CloseHandle(file);
        }
        return result;
    }
    // FileHeader раскладывается одинаково у обоих вариантов — единая ссылка
    // вместо дубля разбора.
    const IMAGE_FILE_HEADER& fileHeader = isPe32Plus
        ? reinterpret_cast<const IMAGE_NT_HEADERS64*>(ntBase)->FileHeader
        : reinterpret_cast<const IMAGE_NT_HEADERS32*>(ntBase)->FileHeader;

    result.isPe = true;

    const WORD sectionCount = fileHeader.NumberOfSections;
    // Раскладка секционной таблицы одинакова для PE32 и PE32+: смещение
    // OptionalHeader в обоих вариантах 24 (Signature + FileHeader).
    const unsigned long long sectionTableOffset =
        static_cast<unsigned long long>(ntOffset)
        + FIELD_OFFSET(IMAGE_NT_HEADERS32, OptionalHeader)
        + fileHeader.SizeOfOptionalHeader;
    if (sectionTableOffset > read
        || static_cast<unsigned long long>(sectionCount) * sizeof(IMAGE_SECTION_HEADER)
               > read - sectionTableOffset) {
        // Некорректная таблица секций: malformed PE — только счётчик, не crash
        // (документ п. 22: безопасная обработка повреждённых PE).
        result.score += 1;
        result.signals.push_back(L"malformed-section-table");
        if (ownsFile) {
            CloseHandle(file);
        }
        result.isValid = true;
        return result;
    }

    unsigned long long endOfRaw = 0;
    // Вклад одного класса сигналов ограничен (контракт kSuspiciousPeScore:
    // «score >= 4 = два независимых сигнала») — две W+X-секции или две
    // высокоэнтропийные секции не должны набирать порог в одиночку.
    int writableExecutableScore = 0;
    int highEntropyScore = 0;
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
        if (writable && executable && section.rawSize > 0 && writableExecutableScore < 2) {
            writableExecutableScore += 2;
            result.score += 2;
            result.signals.push_back(L"writable-executable-section:" + std::wstring(section.name, section.name + strlen(section.name)));
        }

        if ((isCode || executable) && highEntropyScore < 2) {
            const double entropy = SectionEntropy(file, section);
            if (entropy >= kHighEntropyThreshold) {
                highEntropyScore += 2;
                result.score += 2;
                char entropyText[16];
                snprintf(entropyText, sizeof(entropyText), "%.2f", entropy);
                result.signals.push_back(L"high-entropy-executable-section:" + std::wstring(section.name, section.name + strlen(section.name))
                                         + L" (" + std::wstring(entropyText, entropyText + strlen(entropyText)) + L")");
            }
        }

        // Аудит: endOfRaw по всем секциям позволял фиктивному заголовку с
        // большим PointerToRawData «съесть» overlay и погасить сигнал
        // large-overlay. Учитываем только реальные секции: ненулевой размер и
        // целиком внутри файла.
        const unsigned long long sectionEnd =
            static_cast<unsigned long long>(section.rawPointer) + section.rawSize;
        if (section.rawSize > 0 && sectionEnd <= totalSize) {
            endOfRaw = std::max(endOfRaw, sectionEnd);
        }
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
    if (ownsFile) {
        CloseHandle(file);
    }
    return result;
}

} // namespace scan
