#pragma once

// Писатель JSON-событий в stdout построчно (документ п. 39: ScannerCore выводит
// JSON-события в stdout, GUI парсит через ConsoleOutputDecoder). Строки UTF-8,
// без буферизации — каждое событие пишется и сбрасывается атомарно.

#include "core/types.h"

#include <windows.h>

#include <atomic>
#include <mutex>
#include <string>
#include <vector>

namespace scan {

class EventWriter {
public:
    // cancelFlag — необязательный флаг отмены владельца (аудит): при сломанном
    // пайпе stdout (родитель умер) писатель взводит его — скан останавливается
    // сам, вместо того чтобы дописывать события в никуда до конца обхода.
    explicit EventWriter(std::atomic<bool>* cancelFlag = nullptr);

    // Регистрация флага отмены Scanner: Scanner получает EventWriter по ссылке
    // раньше собственного конструирования, поэтому передаёт флаг методом.
    // Вызывать до старта скана (однопоточная фаза), не под мьютексами.
    void SetCancelFlag(std::atomic<bool>* cancelFlag) { cancelFlag_ = cancelFlag; }

    void Started(const std::string& engineVersion, const char* mode,
                 const std::wstring& dbVersion, const std::wstring& dbDate);
    void Progress(const ScanStats& stats, const std::wstring& current);
    // Имя DetectionEvent: метод с именем Detection затенил бы тип scan::Detection
    // внутри класса и сломал бы std::vector<Detection> в сигнатуре ниже.
    void DetectionEvent(const Detection& detection);
    void Warning(const std::wstring& message);
    void Error(const std::wstring& message);
    void UpdateResult(bool ok, const std::wstring& error,
                      const std::wstring& dbVersion, int entries);
    void Finished(const ScanStats& stats,
                  const std::vector<Detection>& detections,
                  bool cancelled);

private:
    void Write(const std::string& line);
    // Ошибка записи в stdout: ERROR_NO_DATA/ERROR_BROKEN_PIPE означает, что
    // родитель закрыл пайп — сигнализируем отмену (аудит).
    void ReportWriteFailure();

    std::mutex mutex_;
    HANDLE stdoutHandle_ = nullptr; // кэшируется один раз, в конструкторе
    std::atomic<bool>* cancelFlag_ = nullptr;
};

// Экранирование строки для JSON-строки (внутренние кавычки/управляющие символы).
std::string JsonEscape(const std::string& utf8);

// UTF-16 → UTF-8 для путей и сообщений.
std::string Utf8(const std::wstring& text);

} // namespace scan
