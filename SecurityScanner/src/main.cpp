// ScannerCore.exe — offline on-demand сканер SCU (документ п. 1/3/61):
// никаких resident-служб, никакой сети, deterministic-отчёт, чистый выход.
// Протокол: аргументы командной строки, события — построчный JSON в stdout.

#include "core/allowlist.h"
#include "core/database.h"
#include "core/database_package.h"
#include "core/path_util.h"
#include "core/scanner.h"
#include "core/types.h"
#include "ipc/event_writer.h"
#include "pe/signature.h"
#include "signing_pin.h"

#include <windows.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <string>

namespace {

constexpr const char* kEngineVersion = scan::kEngineVersion;

// Коды выхода (документ п. 5/36): 0 — успех; 1 — завершено с ошибками
// (часть файлов не просканирована — отчёт не «Clean»); 2 — неверные аргументы;
// 3 — прервано отменой; 99 — фатальная ошибка.
constexpr int kExitOk = 0;
constexpr int kExitPartialErrors = 1;
constexpr int kExitBadArgs = 2;
constexpr int kExitCancelled = 3;
constexpr int kExitFatal = 99;

std::atomic<bool>* g_cancelFlag = nullptr;

BOOL WINAPI OnConsoleCtrl(DWORD /*ctrlType*/)
{
    if (g_cancelFlag != nullptr) {
        g_cancelFlag->store(true, std::memory_order_relaxed);
        return TRUE; // завершение после текущего файла — частичный отчёт (п. 26)
    }
    return FALSE;
}

scan::ScanOptions ParseArgs(int argc, wchar_t** argv, int& exitCode)
{
    scan::ScanOptions options;
    bool haveMode = false;
    bool havePath = false;

    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--mode" && i + 1 < argc) {
            const std::wstring mode = argv[++i];
            haveMode = mode == L"file" || mode == L"custom";
            if (mode == L"custom") {
                options.mode = scan::ScanMode::Custom;
            }
        } else if (arg == L"--path" && i + 1 < argc) {
            options.path = argv[++i];
            havePath = !options.path.empty();
        } else if (arg == L"--max-size" && i + 1 < argc) {
            options.maxFileSize = wcstoull(argv[++i], nullptr, 10);
        } else if (arg == L"--threads" && i + 1 < argc) {
            options.workerThreads = static_cast<uint32_t>(wcstoul(argv[++i], nullptr, 10));
        } else if (arg == L"--no-disk-cache") {
            options.useDiskCache = false;
        } else if (arg == L"--dev-unsigned-ok") {
            options.devUnsignedOk = true;
        }
    }

    if (!haveMode || !havePath) {
        exitCode = kExitBadArgs;
    }
    return options;
}

// Self-integrity check (документ п. 5.3/48) до начала сканирования.
// Режим пиннинга (kSigningCertSha1 непустой): сравнение отпечатка сертификата
// подписи с вшитым — не требует доверенного корня, работает на любой машине.
// Несовпадение/отсутствие подписи = подмена сканера, abort.
// Dev-режим (пин пустой): Authenticode warn-only по --dev-unsigned-ok.
bool SelfIntegrityCheck(const scan::ScanOptions& options, scan::EventWriter& events)
{
    // Динамический путь (аудит п. 9): длинный путь установки не усекается.
    const std::wstring selfPathStr = scan::GetModulePathDynamic();
    if (selfPathStr.empty()) {
        events.Error(L"integrity: cannot resolve own executable path");
        return false;
    }
    if (kSigningCertSha1[0] != '\0') {
        std::string actualThumbprint;
        if (!scan::SignatureVerifier::GetSigningCertHash(selfPathStr, actualThumbprint)) {
            events.Error(L"integrity: ScannerCore.exe is not signed, but a signing pin is configured");
            return false;
        }
        if (_stricmp(actualThumbprint.c_str(), kSigningCertSha1) != 0) {
            events.Error(L"integrity: ScannerCore.exe signing certificate does not match the pinned thumbprint");
            return false;
        }
        return true;
    }

    const scan::SignatureCheck check = scan::SignatureVerifier::Verify(selfPathStr);
    if (check.trusted) {
        return true;
    }

    if (options.devUnsignedOk) {
        events.Warning(L"integrity: ScannerCore.exe is unsigned (dev build) — continuing by flag");
        return true;
    }

    events.Error(L"integrity: ScannerCore.exe signature is not valid — scan aborted");
    return false;
}

} // namespace

namespace {

// Команда update (п. 30/31): offline-установка подписанного пакета базы.
// Схема: verify signature → schema → temp → atomic replace (в DatabasePackage).
int HandleUpdate(int argc, wchar_t** argv, scan::EventWriter& events, bool devUnsignedOk)
{
    std::wstring packagePath;
    for (int i = 2; i < argc - 1; ++i) {
        if (wcscmp(argv[i], L"--package") == 0) {
            packagePath = argv[i + 1];
        }
    }
    if (packagePath.empty()) {
        events.Error(L"usage: ScannerCore.exe update --package <package.zip>");
        return kExitBadArgs;
    }

    scan::ScanOptions options;
    options.devUnsignedOk = devUnsignedOk;
    if (!SelfIntegrityCheck(options, events)) {
        return kExitFatal;
    }

    // Динамический путь (аудит п. 9) + единая логика выделения каталога.
    const std::wstring databaseDir = scan::DirectoryOf(scan::GetModulePathDynamic())
                                     + L"\\security\\database";

    scan::DatabaseUpdateResult result;
    scan::DatabasePackage::Apply(packagePath, databaseDir, result);
    events.UpdateResult(result.ok, result.error, result.dbVersion, result.entries);
    return result.ok ? kExitOk : kExitPartialErrors;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    scan::EventWriter events;

    // Подкоманда update (п. 30): offline-установка подписанного пакета базы.
    if (argc >= 2 && wcscmp(argv[1], L"update") == 0) {
        bool devUnsignedOk = false;
        for (int i = 2; i < argc; ++i) {
            if (wcscmp(argv[i], L"--dev-unsigned-ok") == 0) {
                devUnsignedOk = true;
            }
        }
        return HandleUpdate(argc, argv, events, devUnsignedOk);
    }

    int exitCode = kExitOk;
    const scan::ScanOptions options = ParseArgs(argc, argv, exitCode);
    if (exitCode != kExitOk) {
        events.Error(L"usage: ScannerCore.exe scan --mode file|custom --path <path> [--max-size N] [--dev-unsigned-ok]");
        return kExitBadArgs;
    }

    if (!SelfIntegrityCheck(options, events)) {
        return kExitFatal;
    }

    // Пути и существование цели проверяем до события started.
    DWORD attributes = GetFileAttributesW(options.path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        events.Error(L"target path not found: " + options.path);
        return kExitBadArgs;
    }

    std::atomic<bool> cancelFlag{false};
    g_cancelFlag = &cancelFlag;
    SetConsoleCtrlHandler(OnConsoleCtrl, TRUE);

    scan::ComponentAllowlist allowlist;
    allowlist.AddRuntimeComponents();
    scan::HashDatabase database;
    scan::Scanner scanner(events, allowlist, database);

    events.Started(kEngineVersion, options.mode == scan::ScanMode::File ? "file" : "custom",
                   database.Version(), database.Date());

    scan::ScanStats stats;
    std::vector<scan::Detection> detections;
    const bool completed = scanner.Run(options, stats, detections);

    const bool cancelled = !completed || cancelFlag.load(std::memory_order_relaxed);
    events.Finished(stats, detections, cancelled);

    if (cancelled) {
        return kExitCancelled;
    }
    return stats.errors > 0 ? kExitPartialErrors : kExitOk;
}
