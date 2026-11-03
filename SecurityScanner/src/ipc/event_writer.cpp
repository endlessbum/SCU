#include "ipc/event_writer.h"

#include <windows.h>

#include <cstdio>
#include <sstream>

namespace scan {

std::string Utf8(const std::wstring& text)
{
    if (text.empty()) {
        return {};
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(),
                                         static_cast<int>(text.size()),
                                         nullptr, 0, nullptr, nullptr);
    // Аудит: при size<=0 (ошибка конвертации) аллоцировалась строка из NUL
    // (при size<0 — size_t(size) переполнялся), второй вызов не проверялся —
    // NUL-строка рвёт JSON-протокол. Проверяются оба вызова; при расхождении
    // возвращается placeholder без NUL. Замечание по арифметике: вход задан
    // явной длиной (не -1), поэтому возвращаемое значение — число байт БЕЗ
    // терминатора, и его же передаётся как размер буфера второго вызова.
    if (size <= 0) {
        return "?";
    }
    std::string result(static_cast<size_t>(size), '\0');
    if (WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                            result.data(), size, nullptr, nullptr) != size) {
        return "?";
    }
    return result;
}

std::string JsonEscape(const std::string& utf8)
{
    std::string out;
    out.reserve(utf8.size() + 8);
    char buffer[8];
    for (const unsigned char c : utf8) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (c < 0x20) {
                snprintf(buffer, sizeof(buffer), "\\u%04x", c);
                out += buffer;
            } else {
                out += static_cast<char>(c);
            }
        }
    }
    return out;
}

namespace {

std::string Quote(const std::string& value)
{
    return "\"" + JsonEscape(value) + "\"";
}

std::string QuoteWide(const std::wstring& value)
{
    return Quote(Utf8(value));
}

std::string DetectionToJson(const Detection& d)
{
    std::ostringstream json;
    json << "{\"path\":" << QuoteWide(d.path)
         << ",\"targetPath\":" << QuoteWide(d.targetPath)
         << ",\"sha256\":" << Quote(d.sha256)
         << ",\"verdict\":" << Quote(VerdictToString(d.verdict))
         << ",\"ruleId\":" << QuoteWide(d.ruleId)
         << ",\"description\":" << QuoteWide(d.description)
         << ",\"signedFile\":" << (d.signedFile ? "true" : "false")
         << ",\"publisher\":" << QuoteWide(d.publisher)
         << ",\"score\":" << d.score
         << ",\"containerPath\":" << QuoteWide(d.containerPath)
         << ",\"isVirtual\":" << (d.isVirtual ? "true" : "false")
         << ",\"source\":" << QuoteWide(d.source)
         << ",\"signals\":[";
    for (size_t i = 0; i < d.signals.size(); ++i) {
        if (i > 0) {
            json << ",";
        }
        json << QuoteWide(d.signals[i]);
    }
    json << "]}";
    return json.str();
}

} // namespace

EventWriter::EventWriter(std::atomic<bool>* cancelFlag)
    : cancelFlag_(cancelFlag)
{
    // stdout кэшируется один раз (аудит): GetStdHandle на каждую запись —
    // лишняя работа в горячем пути прогресса.
    // stdout при запуске из GUI — пайп: кодировка консоли не применяется.
    // Пишем UTF-8 напрямую через WriteFile, минуя WideChar-слои CRT.
    stdoutHandle_ = GetStdHandle(STD_OUTPUT_HANDLE);
}

void EventWriter::ReportWriteFailure()
{
    // Оба кода означают, что читающий конец пайпа закрыт (GUI умер/отвалился):
    // события дальше идут в никуда — взводим флаг отмены, сканер остановится
    // сам (аудит). Иные ошибки записи отменой не считаются.
    const DWORD error = GetLastError();
    if ((error == ERROR_NO_DATA || error == ERROR_BROKEN_PIPE) && cancelFlag_ != nullptr) {
        cancelFlag_->store(true, std::memory_order_relaxed);
    }
}

void EventWriter::Write(const std::string& line)
{
    std::lock_guard lock(mutex_);
    if (stdoutHandle_ == INVALID_HANDLE_VALUE || stdoutHandle_ == nullptr) {
        return;
    }
    // Событие и '\n' — одним WriteFile (аудит: меньше сисколлов); полной
    // буферизации нет, каждая строка по-прежнему уходит целиком и атомарно.
    std::string packet;
    packet.reserve(line.size() + 1);
    packet += line;
    packet.push_back('\n');
    DWORD written = 0;
    if (!WriteFile(stdoutHandle_, packet.data(), static_cast<DWORD>(packet.size()), &written, nullptr)
        || written != packet.size()) {
        ReportWriteFailure();
    }
    // Flush не нужен: WriteFile на пайпе уходит в ядро сразу.
}

void EventWriter::Started(const std::string& engineVersion, const char* mode,
                          const std::wstring& dbVersion, const std::wstring& dbDate)
{
    std::ostringstream json;
    json << "{\"event\":\"started"
         << "\",\"engineVersion\":" << Quote(engineVersion)
         << ",\"mode\":" << Quote(mode)
         << ",\"dbVersion\":" << QuoteWide(dbVersion)
         << ",\"dbDate\":" << QuoteWide(dbDate) << "}";
    Write(json.str());
}

void EventWriter::Progress(const ScanStats& stats, const std::wstring& current)
{
    std::ostringstream json;
    json << "{\"event\":\"progress"
         << "\",\"scanned\":" << stats.filesScanned
         << ",\"skipped\":" << stats.filesSkipped
         << ",\"detections\":" << stats.detections
         << ",\"current\":" << QuoteWide(current) << "}";
    Write(json.str());
}

void EventWriter::DetectionEvent(const Detection& detection)
{
    Write("{\"event\":\"detection\"," + DetectionToJson(detection).substr(1));
}

void EventWriter::Warning(const std::wstring& message)
{
    std::ostringstream json;
    json << "{\"event\":\"warning\",\"message\":" << QuoteWide(message) << "}";
    Write(json.str());
}

void EventWriter::Error(const std::wstring& message)
{
    std::ostringstream json;
    json << "{\"event\":\"error\",\"message\":" << QuoteWide(message) << "}";
    Write(json.str());
}

void EventWriter::UpdateResult(bool ok, const std::wstring& error,
                               const std::wstring& dbVersion, int entries)
{
    std::ostringstream json;
    json << "{\"event\":\"update"
         << "\",\"status\":" << (ok ? "\"ok\"" : "\"failed\"")
         << ",\"error\":" << QuoteWide(error)
         << ",\"dbVersion\":" << QuoteWide(dbVersion)
         << ",\"entries\":" << entries << "}";
    Write(json.str());
}

void EventWriter::Finished(const ScanStats& stats,
                           const std::vector<Detection>& detections,
                           bool cancelled)
{
    std::ostringstream json;
    json << "{\"event\":\"finished"
         << "\",\"cancelled\":" << (cancelled ? "true" : "false")
         << ",\"summary\":{\"filesScanned\":" << stats.filesScanned
         << ",\"filesSkipped\":" << stats.filesSkipped
         << ",\"errors\":" << stats.errors
         << ",\"detections\":" << stats.detections << "}"
         << ",\"detections\":[";
    for (size_t i = 0; i < detections.size(); ++i) {
        if (i > 0) {
            json << ",";
        }
        json << DetectionToJson(detections[i]);
    }
    json << "]}";
    Write(json.str());
}

} // namespace scan
