#include "core/scanner.h"

#include "core/path_util.h"

#include <windows.h>
#include <bcrypt.h>
#include <sddl.h>
#include <aclapi.h>

// ntstatus.h тянет за собой ntdef.h и конфликтует с windows.h — STATUS_SUCCESS
// определяем локально (аудит-фикс незакоммиченного блока HMAC-кэша).
#ifndef STATUS_SUCCESS
#define STATUS_SUCCESS ((NTSTATUS)0x00000000L)
#endif

#pragma comment(lib, "bcrypt.lib")

#include <algorithm>
#include <cctype>
#include <fstream>
#include <sstream>
#include <thread>
#include <unordered_map>

#include "archive/archive_scanner.h"
#include "core/file_type.h"
#include "hash/sha256.h"
#include "pe/pe_analyzer.h"
#include "pe/signature.h"
#include "script/script_scanner.h"
#include "system/persistence_scanner.h"
#include "system/process_scanner.h"

namespace scan {

namespace {

// Порог Suspicious для PE по эвристическим сигналам (документ п. 15:
// стартовые пороги — архитектурный черновик, калибруются на corpus).
// score >= 4 означает минимум два независимых сигнала.
constexpr int kSuspiciousPeScore = 4;
constexpr int kSuspiciousScriptScore = 4;

// Верхняя граница дискового кэша (п. 25): переполнение отключает пополнение.
constexpr size_t kMaxCacheEntries = 100000;

bool FileExistsNow(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

unsigned long long GetFileSizeSafe(const std::wstring& path)
{
    WIN32_FIND_DATAW findData{};
    HANDLE find = FindFirstFileExW(path.c_str(), FindExInfoBasic, &findData,
                                   FindExSearchNameMatch, nullptr, 0);
    if (find == INVALID_HANDLE_VALUE) {
        return 0;
    }
    FindClose(find);
    return (static_cast<unsigned long long>(findData.nFileSizeHigh) << 32) | findData.nFileSizeLow;
}

std::string ToLowerAscii(const std::wstring& text)
{
    // Аудит: прежняя версия молча выбрасывала не-ASCII символы — Unicode-путь
    // в persistence-команде искажался до анализа (часть признаков могла
    // пропасть, диагностика искажалась). Конвертация в UTF-8 без потерь;
    // lowercase — только ASCII: паттерны эвристик ASCII, не-ASCII символы
    // сохраняются как есть.
    if (text.empty()) {
        return {};
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(),
                                         static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) {
        return {};
    }
    std::string result(static_cast<size_t>(size), '\0');
    if (WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                            result.data(), size, nullptr, nullptr) <= 0) {
        return {};
    }
    for (char& c : result) {
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    return result;
}

// Валидный ключ кэша — строго 64 hex-символа. Повреждённая строка кэша
// игнорируется (fail-closed: вердикт пересчитывается, не подменяется).
bool IsSha256Hex(const std::string& text)
{
    if (text.size() != 64) {
        return false;
    }
    for (const char c : text) {
        const bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
        if (!hex) {
            return false;
        }
    }
    return true;
}

std::string HashTargetForReport(const std::wstring& path)
{
    std::string hex;
    return Sha256::HashFile(path, hex) ? hex : std::string();
}

std::wstring VerdictToWide(Verdict verdict)
{
    switch (verdict) {
    case Verdict::Malware: return L"malware";
    case Verdict::Suspicious: return L"suspicious";
    default: return L"clean";
    }
}

// UTF-8 ↔ wide для дискового кэша (аудит): раньше ruleId/description писались
// побайтовым усечением wchar_t — не-ASCII текст (имена/описания из базы) портился.
std::string WideToUtf8(const std::wstring& wide)
{
    if (wide.empty()) {
        return {};
    }
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(),
                                         static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) {
        return {};
    }
    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()),
                        result.data(), size, nullptr, nullptr);
    return result;
}

std::wstring Utf8ToWide(const std::string& utf8)
{
    if (utf8.empty()) {
        return {};
    }
    const int size = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(),
                                         static_cast<int>(utf8.size()), nullptr, 0);
    if (size <= 0) {
        return {};
    }
    std::wstring result(static_cast<size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), result.data(), size);
    return result;
}

// ===================== Доверие к дисковому кэшу (аудит trust model) =====================
// Кэш лежит в user-writable LocalAppData, а ScannerCore исполняется elevated:
// не-админ-процесс мог подменить сохранённый вердикт, переходя границу
// привилегий. Защита — HMAC-SHA256 каждой строки ключом из %LOCALAPPDATA%\SCU
// \cache.key, созданного elevated-запуском с DACL только SYSTEM+Administrators.
// Не-админ процесс ключ не читает → подделать MAC не может. Нет ключа →
// дисковый кэш отключается (fail-closed, остаётся in-memory).

std::vector<unsigned char> HexToBytes(const std::string& hex)
{
    std::vector<unsigned char> bytes;
    if (hex.size() % 2 != 0) {
        return bytes;
    }
    for (size_t i = 0; i < hex.size(); i += 2) {
        auto nibble = [](wchar_t c) -> int {
            if (c >= L'0' && c <= L'9') return c - L'0';
            if (c >= L'a' && c <= L'f') return c - L'a' + 10;
            if (c >= L'A' && c <= L'F') return c - L'A' + 10;
            return -1;
        };
        const int high = nibble(hex[i]);
        const int low = nibble(hex[i + 1]);
        if (high < 0 || low < 0) {
            return {};
        }
        bytes.push_back(static_cast<unsigned char>((high << 4) | low));
    }
    return bytes;
}

std::string BytesToHex(const unsigned char* data, size_t size)
{
    static const char* digits = "0123456789abcdef";
    std::string hex;
    hex.reserve(size * 2);
    for (size_t i = 0; i < size; ++i) {
        hex.push_back(digits[data[i] >> 4]);
        hex.push_back(digits[data[i] & 0x0F]);
    }
    return hex;
}

// true, если процесс elevated: доверенный каталог кэша и ключ создаёт только
// elevated-запуск. НЕ-elevated прогон не читает и не создаёт их вовсе: его
// артефакты были бы доступны не-админ процессам, и «приём» такого ключа вернул
// бы подмену через границу привилегий.
bool ProcessIsElevated()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        return false;
    }
    DWORD elevation = 0;
    DWORD returned = 0;
    const bool ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &returned)
        && elevation != 0;
    CloseHandle(token);
    return ok;
}

// SID — SYSTEM (S-1-5-18) или встроенные Администраторы (S-1-5-32-544).
bool SidIsTrustedPrincipal(const PSID sid)
{
    if (sid == nullptr || !IsValidSid(sid)) {
        return false;
    }

    PSID system = nullptr;
    PSID administrators = nullptr;
    bool ok = false;
    if (ConvertStringSidToSidW(L"S-1-5-18", &system) && system != nullptr) {
        if (ConvertStringSidToSidW(L"S-1-5-32-544", &administrators) && administrators != nullptr) {
            ok = EqualSid(sid, system) || EqualSid(sid, administrators);
        }
    }
    if (system != nullptr) {
        LocalFree(system);
    }
    if (administrators != nullptr) {
        LocalFree(administrators);
    }
    return ok;
}

// DACL каталога не даёт доступа никому, кроме SYSTEM/Администраторов: каждая
// ACE — ACCESS_ALLOWED для доверенного принципала. Иначе каталог мог создать
// или переподчинить не-админ процесс.
bool DirectoryDaclIsStrict(HANDLE dir)
{
    PSECURITY_DESCRIPTOR sd = nullptr;
    PACL dacl = nullptr;
    if (GetSecurityInfo(dir, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                        nullptr, nullptr, &dacl, nullptr, &sd) != ERROR_SUCCESS
        || dacl == nullptr) {
        if (sd != nullptr) {
            LocalFree(sd);
        }
        return false;
    }

    ACL_SIZE_INFORMATION size{};
    bool ok = GetAclInformation(dacl, &size, sizeof(size), AclSizeInformation);
    for (DWORD i = 0; ok && i < size.AceCount; ++i) {
        LPVOID ace = nullptr;
        if (!GetAce(dacl, i, &ace) || ace == nullptr) {
            ok = false;
            break;
        }
        const auto* header = static_cast<const ACE_HEADER*>(ace);
        if (header->AceType != ACCESS_ALLOWED_ACE_TYPE) {
            ok = false;
            break;
        }
        auto* allowed = static_cast<ACCESS_ALLOWED_ACE*>(ace);
        ok = SidIsTrustedPrincipal(reinterpret_cast<PSID>(&allowed->SidStart));
    }

    if (sd != nullptr) {
        LocalFree(sd);
    }
    return ok;
}

// Доверенный каталог кэша: %ProgramData%\SCU\scanner-cache со строгим DACL.
// Ключ HMAC и кэш-файл лежат там, куда не-админ не может писать: подмена
// ключа удалением/пересозданием (user-writable %LOCALAPPDATA% допускал её)
// стала невозможна физически. Каталог создаётся один раз elevated-запуском;
// существующий каталог доверяется только при строгом DACL (fail-closed).
std::wstring TrustedCacheDirectory()
{
    const std::wstring programData = GetEnvironmentValueDynamic(L"ProgramData");
    if (programData.empty()) {
        return {};
    }

    SECURITY_ATTRIBUTES sa{};
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:P(A;OICI;FA;;;S-1-5-18)(A;OICI;FA;;;BA)", SDDL_REVISION_1, &sd, nullptr)) {
        sa.nLength = sizeof(sa);
        sa.lpSecurityDescriptor = sd;
        sa.bInheritHandle = FALSE;
    }

    const std::wstring root = programData + L"\\SCU";
    const std::wstring dir = root + L"\\scanner-cache";
    if (!CreateDirectoryW(root.c_str(), sa.nLength ? &sa : nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) {
        if (sd != nullptr) {
            LocalFree(sd);
        }
        return {};
    }
    if (!CreateDirectoryW(dir.c_str(), sa.nLength ? &sa : nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) {
        if (sd != nullptr) {
            LocalFree(sd);
        }
        return {};
    }
    if (sd != nullptr) {
        LocalFree(sd);
    }

    // Уже существующий каталог: доверяем только при строгом DACL — иначе
    // кто-то успел подложить свой каталог до нас.
    HANDLE probe = CreateFileW(dir.c_str(), READ_CONTROL, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (probe == INVALID_HANDLE_VALUE) {
        return {};
    }
    const bool trusted = DirectoryDaclIsStrict(probe);
    CloseHandle(probe);
    return trusted ? dir : std::wstring{};
}

// Ключ кэша: 32 байта из cache.key в доверенном каталоге кэша. Файл создаётся
// elevated-запуском с DACL «только SYSTEM и Администраторы»; нет прав/нет
// каталога → пустой вектор → дисковый кэш отключается (fail-closed).
// Граница доверия — строгий DACL каталога (TrustedCacheDirectory): подмена
// ключа не-elevated процессом физически невозможна, отдельная проверка
// владельца файла не нужна (на системах, где даже elevated-токен создаёт
// файлы user-owned, она давала бы ложное срабатывание и churn ключа).
std::vector<unsigned char> LoadOrCreateCacheKey(const std::wstring& cacheDir)
{
    const std::wstring keyPath = cacheDir + L"\\cache.key";
    const DWORD desiredAccess = GENERIC_READ | GENERIC_WRITE;
    SECURITY_ATTRIBUTES sa{};
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:P(A;;FA;;;S-1-5-18)(A;;FA;;;BA)", SDDL_REVISION_1, &sd, nullptr)) {
        sa.nLength = sizeof(sa);
        sa.lpSecurityDescriptor = sd;
        sa.bInheritHandle = FALSE;
    }

    // Единственный выход из функции — общий эпилог: sd освобождается на всех
    // путях (раньше ветка CREATE_NEW возвращала ключ до LocalFree — утечка).
    std::vector<unsigned char> key;
    HANDLE keyFile = CreateFileW(keyPath.c_str(), desiredAccess,
                                 0, sa.nLength ? &sa : nullptr, OPEN_EXISTING,
                                 FILE_ATTRIBUTE_NORMAL, nullptr);
    const bool missing = keyFile == INVALID_HANDLE_VALUE && GetLastError() == ERROR_FILE_NOT_FOUND;

    if (keyFile == INVALID_HANDLE_VALUE && missing) {
        // Создание — только elevated-запуском в доверенном каталоге: если
        // raising не сработал, ключ не придумываем, а отключаем дисковый кэш.
        keyFile = CreateFileW(keyPath.c_str(), desiredAccess,
                              0, sa.nLength ? &sa : nullptr, CREATE_NEW,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
        if (keyFile != INVALID_HANDLE_VALUE) {
            unsigned char fresh[32] = {};
            const bool generated = BCryptGenRandom(nullptr, fresh, sizeof(fresh),
                                                   BCRYPT_USE_SYSTEM_PREFERRED_RNG) == STATUS_SUCCESS;
            DWORD written = 0;
            if (generated
                && WriteFile(keyFile, fresh, sizeof(fresh), &written, nullptr)
                && written == sizeof(fresh)) {
                key.assign(fresh, fresh + sizeof(fresh));
            }
            CloseHandle(keyFile);
            if (sd != nullptr) {
                LocalFree(sd);
            }
            return key;
        }
    } else if (keyFile != INVALID_HANDLE_VALUE) {
        unsigned char keyBuffer[32] = {};
        DWORD read = 0;
        const BOOL readOk = ReadFile(keyFile, keyBuffer, sizeof(keyBuffer), &read, nullptr);
        CloseHandle(keyFile);
        if (readOk && read == sizeof(keyBuffer)) {
            key.assign(keyBuffer, keyBuffer + sizeof(keyBuffer));
        }
    }

    if (sd != nullptr) {
        LocalFree(sd);
    }
    return key;
}

// SHA-256 от конкатенации двух байтовых блоков поверх обычного (безключевого)
// BCrypt-провайдера. На ряде систем BCryptGenerateSymmetricKey с HMAC-флагом
// отвечает STATUS_NOT_SUPPORTED (0xC00000BB) — keyed-провайдер не работает,
// и прежняя реализация молча отдавала пустой HMAC, отключая весь дисковый кэш.
bool Sha256Of(const unsigned char* first, size_t firstLength,
              const unsigned char* second, size_t secondLength, unsigned char out[32])
{
    BCRYPT_ALG_HANDLE alg = nullptr;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != STATUS_SUCCESS) {
        return false;
    }

    BCRYPT_HASH_HANDLE hash = nullptr;
    bool ok = BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) == STATUS_SUCCESS;
    if (ok) {
        ok = BCryptHashData(hash, const_cast<PUCHAR>(first),
                            static_cast<ULONG>(firstLength), 0) == STATUS_SUCCESS;
    }
    if (ok && secondLength > 0) {
        ok = BCryptHashData(hash, const_cast<PUCHAR>(second),
                            static_cast<ULONG>(secondLength), 0) == STATUS_SUCCESS;
    }
    if (ok) {
        ok = BCryptFinishHash(hash, out, 32, 0) == STATUS_SUCCESS;
    }
    if (hash != nullptr) {
        BCryptDestroyHash(hash);
    }
    BCryptCloseAlgorithmProvider(alg, 0);
    return ok;
}

// HMAC-SHA256 вручную: HMAC = H(K⊕opad || H(K⊕ipad || message)). Ключ 32 байта
// меньше блока SHA-256 (64) и добивается нулями (RFC 2104).
std::string HmacHex(const std::vector<unsigned char>& key, const std::string& data)
{
    if (key.empty()) {
        return {};
    }

    constexpr size_t kShaBlock = 64;
    std::vector<unsigned char> ipad(kShaBlock, 0x36);
    std::vector<unsigned char> opad(kShaBlock, 0x5c);
    for (size_t i = 0; i < kShaBlock; ++i) {
        const unsigned char keyByte = i < key.size() ? key[i] : 0;
        ipad[i] ^= keyByte;
        opad[i] ^= keyByte;
    }

    unsigned char inner[32] = {};
    if (!Sha256Of(ipad.data(), ipad.size(),
                  reinterpret_cast<const unsigned char*>(data.data()), data.size(), inner)) {
        return {};
    }

    unsigned char mac[32] = {};
    if (!Sha256Of(opad.data(), opad.size(), inner, sizeof(inner), mac)) {
        return {};
    }

    return BytesToHex(mac, sizeof(mac));
}

} // namespace

Scanner::Scanner(EventWriter& events, ComponentAllowlist& allowlist, HashDatabase& database)
    : events_(events)
    , allowlist_(allowlist)
    , database_(database)
{
}

void Scanner::RememberVerdict(const std::string& sha256, Verdict verdict, Detection detection)
{
    // Лимит кэша (п. 25): при переполнении новые записи не добавляются —
    // деградация до отсутствия кэша, не до неверных результатов.
    // Кэш mutated воркерами параллельно — под собственным cacheMutex_
    // (порядок захвата всегда cacheMutex_ → stateMutex_).
    std::lock_guard lock(cacheMutex_);
    if (verdictCache_.size() < kMaxCacheEntries) {
        verdictCache_[sha256] = {verdict, std::move(detection)};
    }
}

std::wstring Scanner::DiskCachePath() const
{
    // Кэш живёт в доверенном каталоге (ProgramData, строгий DACL): путь недоступен
    // не-elevated прогону или при подменённом каталоге — кэш просто отключён.
    const std::wstring dir = TrustedCacheDirectory();
    if (dir.empty()) {
        return {};
    }
    return dir + L"\\scan-cache.txt";
}

// Профиль скана в заголовке кэша (п. 2 аудита): кэш валиден только для той же
// комбинации параметров, влияющих на вердикт. Изменение любого параметра
// (scripts/archives/persistence/processes/maxFileSize/эвристики) полностью
// инвалидирует сохранённый кэш.
std::string Scanner::CacheProfileTag() const
{
    std::ostringstream out;
    out << "scripts=" << (scanScripts_ ? 1 : 0)
        << ";archives=" << (scanArchives_ ? 1 : 0)
        << ";persistence=" << (scanPersistence_ ? 1 : 0)
        << ";processes=" << (scanProcesses_ ? 1 : 0)
        << ";maxbytes=" << maxFileSize_
        << ";heur=" << kHeuristicVersion;
    return out.str();
}

// Формат: заголовок
// "#scucache v3 engine=<ver> db=<ver> profile=<scripts=N;archives=N;...>",
// далее строки "<hex-hmac>\t<payload>", payload =
// "sha256<TAB>verdict<TAB>ruleId<TAB>score<TAB>signed<TAB>description" в UTF-8.
// v3: каждая строка подписана HMAC-SHA256 ключом из cache.key (DACL только
// SYSTEM/Администраторы) — user-writable кэш больше не является доверенным
// источником вердиктов (аудит trust model). v2: UTF-8 вместо усечённого
// wchar_t. Любое несовпадение версии/ключа/движка/базы/профиля отбрасывает кэш.
void Scanner::LoadDiskCache()
{
    const std::wstring path = DiskCachePath();
    if (path.empty()) {
        return;
    }

    // Ключ в том же доверенном каталоге, что и кэш; нет каталога/ключа/прав →
    // дисковый кэш отключён. НЕ-elevated прогон: каталог нечитаем для него —
    // кэш отключён сам по себе (гейт оставлен как короткое замыкание).
    if (!ProcessIsElevated()) {
        return;
    }
    const auto cacheKey = LoadOrCreateCacheKey(TrustedCacheDirectory());
    if (cacheKey.empty()) {
        return;
    }

    std::ifstream file(path);
    if (!file.is_open()) {
        return;
    }

    std::string header;
    if (!std::getline(file, header)) {
        return;
    }
    const std::string expected = std::string("#scucache v3 engine=") + kEngineVersion
                                 + " db=" + WideToUtf8(database_.Version())
                                 + " profile=" + CacheProfileTag();
    if (header != expected) {
        return; // другая версия движка/базы/профиля — кэш недействителен
    }

    std::string line;
    while (std::getline(file, line) && verdictCache_.size() < kMaxCacheEntries) {
        // Строка = "<hex-hmac>\t<payload>": неверный/отсутствующий MAC —
        // строка отбрасывается (подмена вердикта не принимается).
        const size_t separator = line.find('\t');
        if (separator == std::string::npos) {
            continue;
        }
        const std::string macHex = line.substr(0, separator);
        const std::string payload = line.substr(separator + 1);
        const std::string expectedMac = HmacHex(cacheKey, payload);
        if (expectedMac.empty() || macHex != expectedMac) {
            continue;
        }

        std::istringstream stream(payload);
        std::string sha256;
        std::string verdictText;
        std::string ruleId;
        std::string scoreText;
        std::string signedText;
        std::string description;
        if (!std::getline(stream, sha256, '\t') || !std::getline(stream, verdictText, '\t')) {
            continue; // повреждённая строка — пропускается, не превращается в Clean
        }
        if (!IsSha256Hex(sha256)) {
            continue;
        }
        if (verdictText != "malware" && verdictText != "suspicious" && verdictText != "clean") {
            continue; // неизвестный вердикт — fail-closed: пересчёт по-настоящему
        }

        const Verdict verdict = verdictText == "malware" ? Verdict::Malware
                                : verdictText == "suspicious" ? Verdict::Suspicious
                                                              : Verdict::Clean;
        if (verdict == Verdict::Clean) {
            verdictCache_[sha256] = {Verdict::Clean, Detection{}};
            continue;
        }

        std::getline(stream, ruleId, '\t');
        std::getline(stream, scoreText, '\t');
        std::getline(stream, signedText, '\t');
        std::getline(stream, description);

        Detection detection;
        detection.sha256 = sha256;
        detection.verdict = verdict;
        detection.ruleId = Utf8ToWide(ruleId);
        detection.description = Utf8ToWide(description);
        detection.score = atoi(scoreText.c_str());
        detection.signedFile = signedText == "1";
        verdictCache_[sha256] = {verdict, std::move(detection)};
    }
}

void Scanner::SaveDiskCache()
{
    const std::wstring path = DiskCachePath();
    if (path.empty()) {
        return;
    }

    // Аудит trust model: без ключа (нет прав на его создание/чтение) дисковый
    // кэш не пишется вовсе — неподписанные вердикты на диск не попадают.
    // НЕ-elevated прогон: не трогаем ключ вообще (см. LoadDiskCache).
    if (!ProcessIsElevated()) {
        return;
    }
    const auto cacheKey = LoadOrCreateCacheKey(TrustedCacheDirectory());
    if (cacheKey.empty()) {
        return;
    }

    std::ostringstream out;
    out << "#scucache v3 engine=" << kEngineVersion
        << " db=" << WideToUtf8(database_.Version())
        << " profile=" << CacheProfileTag() << "\n";
    for (const auto& [sha256, cached] : verdictCache_) {
        std::ostringstream payload;
        payload << sha256 << '\t' << VerdictToString(cached.verdict);
        if (cached.verdict == Verdict::Clean) {
            out << HmacHex(cacheKey, payload.str()) << '\t' << payload.str() << '\n';
            continue;
        }
        payload << '\t' << WideToUtf8(cached.detection.ruleId)
                << '\t' << cached.detection.score
                << '\t' << (cached.detection.signedFile ? '1' : '0')
                << '\t' << WideToUtf8(cached.detection.description);
        out << HmacHex(cacheKey, payload.str()) << '\t' << payload.str() << '\n';
    }

    const std::string content = out.str();
    // Tmp-файл уникален для процесса: общий фиксированный .tmp при двух
    // параллельных ScannerCore приводил к взаимному затиранию ещё до
    // MoveFileExW. Кэш fail-closed, поэтому гонка «кто последний переименовал»
    // безопасна — проигравший просто не попадёт в кэш этого прогона.
    const std::wstring tempPath = path + L"." + std::to_wstring(GetCurrentProcessId()) + L".tmp";
    HANDLE file = CreateFileW(tempPath.c_str(), GENERIC_WRITE, 0,
                              nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return;
    }
    DWORD written = 0;
    if (WriteFile(file, content.data(), static_cast<DWORD>(content.size()), &written, nullptr)
        && written == content.size()) {
        CloseHandle(file);
        MoveFileExW(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING);
    } else {
        CloseHandle(file);
        DeleteFileW(tempPath.c_str());
    }
}

bool Scanner::Run(const ScanOptions& options, ScanStats& stats, std::vector<Detection>& detections)
{
    maxFileSize_ = options.maxFileSize;
    scanArchives_ = options.scanArchives;
    scanScripts_ = options.scanScripts;
    scanPersistence_ = options.scanPersistence;
    scanProcesses_ = options.scanProcesses;
    useDiskCache_ = options.useDiskCache;

    // Test-hook (только для интеграционных тестов): SCU_TEST_CANCEL_AFTER=<N>
    // прерывает скан после N обработанных файлов — тем же флагом, что и отмена
    // пользователя, поэтому поведение идентично прод-отмене (частичный отчёт).
    // Компилируется только с -DSCU_ENABLE_TEST_HOOKS (CMake-опция, включается
    // CI-ногой тестов): в production elevated-сканер наследует окружение
    // пользовательской сессии, и hook через переменную окружения стал бы
    // рычагом отказа в обслуживании без прав администратора.
#ifdef SCU_ENABLE_TEST_HOOKS
    {
        wchar_t cancelAfter[16]{};
        if (GetEnvironmentVariableW(L"SCU_TEST_CANCEL_AFTER", cancelAfter, 16) > 0) {
            const unsigned long long parsed = wcstoull(cancelAfter, nullptr, 10);
            if (parsed > 0) {
                testCancelAfter_ = parsed;
            }
        }
    }
#endif

    // 1. Обход дерева — быстрый однопоточный этап, результат в вектор.
    std::vector<FileEntry> entries;
    DirectoryEnumerator enumerator;
    enumerator.Enumerate(
        options.path,
        [&entries](const FileEntry& entry) {
            entries.push_back(entry);
        },
        [this, &stats](const std::wstring& path, unsigned long code) {
            std::lock_guard lock(stateMutex_);
            stats.errors++;
            wchar_t codeText[16];
            swprintf_s(codeText, L"%lu", code);
            events_.Warning(L"directory error (" + std::wstring(codeText) + L"): " + path);
        },
        // Намеренный пропуск обхода (reparse/max-depth/offline/device) — потеря
        // покрытия, а не пустой результат (аудит 2, п. 3–5): фиксируется как
        // filesSkipped, чтобы итог стал Partial.
        [this, &stats](const std::wstring& path, const wchar_t* reason) {
            std::lock_guard lock(stateMutex_);
            stats.filesSkipped++;
            events_.Warning(L"skipped (" + std::wstring(reason) + L"): " + path);
        },
        isCancelled_);

    // 2. Дисковый кэш загружается до воркеров (п. 25). Этапы однопоточные:
    //    блокировка не нужна, join воркеров даёт happens-before для записи.
    if (useDiskCache_) {
        LoadDiskCache();
    }

    // 3. Фиксированный thread pool (п. 26): каждому воркеру — следующий файл
    // по atomic-индексу. ScanFile сам держит stateMutex_ и сам пишет progress.
    const unsigned cores = std::thread::hardware_concurrency() > 1
        ? static_cast<unsigned>(std::thread::hardware_concurrency()) - 1
        : 1;
    const unsigned threadCount = options.workerThreads != 0
        ? options.workerThreads
        : std::clamp(cores, 1u, 8u);
    if (threadCount > 1 && entries.size() > 32) {
        std::atomic<size_t> nextIndex{0};
        std::atomic<unsigned long long> processedCount{0};
        std::vector<std::thread> workers;
        workers.reserve(threadCount);
        for (unsigned t = 0; t < threadCount; ++t) {
            workers.emplace_back([this, &entries, &nextIndex, &stats, &detections, &processedCount]() {
                while (!IsCancelled()) {
                    const size_t index = nextIndex.fetch_add(1, std::memory_order_relaxed);
                    if (index >= entries.size()) {
                        return;
                    }
                    ScanFile(entries[index], stats, detections);
                    if (testCancelAfter_ > 0
                        && processedCount.fetch_add(1, std::memory_order_relaxed) + 1 >= testCancelAfter_) {
                        isCancelled_.store(true, std::memory_order_relaxed);
                    }
                }
            });
        }
        for (auto& worker : workers) {
            worker.join();
        }
    } else {
        unsigned long long processed = 0;
        for (const FileEntry& entry : entries) {
            if (IsCancelled()) {
                break;
            }
            ScanFile(entry, stats, detections);
            if (testCancelAfter_ > 0 && ++processed >= testCancelAfter_) {
                isCancelled_.store(true, std::memory_order_relaxed);
            }
        }
    }

    // 4. Persistence и процессы — последовательные этапы после воркеров (п. 24).
    if (!IsCancelled() && options.mode == ScanMode::Custom) {
        if (options.scanPersistence) {
            ScanPersistence(stats, detections);
        }
        if (!IsCancelled() && options.scanProcesses) {
            ScanProcesses(stats, detections);
        }
    }

    // 5. Кэш сохраняется один раз в конце скана (воркеры уже join — потокобопасно).
    if (useDiskCache_) {
        SaveDiskCache();
    }

    return !IsCancelled();
}

void Scanner::ScanFile(const FileEntry& entry, ScanStats& stats, std::vector<Detection>& detections)
{
    // Анализ выполняется ВНЕ общих блокировок (аудит 2, п. 15): SHA-256,
    // WinVerifyTrust и PE-разбор не должны сериализовать воркеры.
    // AnalyzeFile сам синхронизирует короткие критические секции
    // (stats/detections/verdictCache), events_ имеет собственный мьютекс.
    bool needsArchiveScan = false;
    AnalyzeFile(entry.path, entry.path, entry.size, L"", false, stats, detections, needsArchiveScan);

    {
        std::lock_guard lock(stateMutex_);
        events_.Progress(stats, entry.path);
    }

    // Полный pipeline для архива (ScanMode::File и Full Scan, документ п. 20/24):
    // извлечение членов с лимитами и скан каждого. Само извлечение — вне
    // блокировки (тяжёлая операция), синхронизация — внутри ProcessMember.
    if (needsArchiveScan && scanArchives_ && !IsCancelled()) {
        RunArchiveScan(entry.path, entry.path, stats, detections);
    }
}

Verdict Scanner::RunArchiveScan(const std::wstring& path,
                                const std::wstring& containerPath,
                                ScanStats& stats,
                                std::vector<Detection>& detections)
{
    Verdict worst = Verdict::Clean;
    ArchiveLimits limits;
    ArchiveScanner scanner(
        limits,
        isCancelled_,
        [this, &stats, &detections, &worst, containerPath](const std::wstring& virtualPath,
                                                            const std::wstring& realPath,
                                                            unsigned long long size) {
            const Verdict verdict = ProcessMember(virtualPath, realPath, size, containerPath, stats, detections);
            if (verdict == Verdict::Malware) {
                worst = Verdict::Malware;
            } else if (verdict == Verdict::Suspicious && worst != Verdict::Malware) {
                worst = Verdict::Suspicious;
            }
            return !IsCancelled();
        },
        [this, &stats](const ArchiveIssue& issue) {
            std::lock_guard lock(stateMutex_);
            // Ошибки vs пропуски формализованы (аудит п. 3): технический сбой
            // идёт в errors (exit code 1 → Partial), ограничение покрытия —
            // в filesSkipped (Partial без exit code 1).
            if (issue.technical) {
                stats.errors++;
                events_.Error(L"archive " + issue.code + L": " + issue.virtualPath
                              + (issue.member.empty() ? L"" : L" :: " + issue.member));
            } else {
                stats.filesSkipped++;
                events_.Warning(L"archive " + issue.code + L": " + issue.virtualPath
                                + (issue.member.empty() ? L"" : L" :: " + issue.member));
            }
        });
    scanner.ScanZip(path, path, 0);
    return worst;
}

Verdict Scanner::ProcessMember(const std::wstring& virtualPath,
                            const std::wstring& realPath,
                            unsigned long long size,
                            const std::wstring& containerPath,
                            ScanStats& stats,
                            std::vector<Detection>& detections)
{
    if (IsCancelled()) {
        return Verdict::Clean;
    }

    // Анализ члена — вне общих блокировок (см. ScanFile, аудит 2, п. 15).
    bool needsArchiveScan = false;
    const Verdict verdict = AnalyzeFile(virtualPath, realPath, size, containerPath, true,
                                        stats, detections, needsArchiveScan);

    {
        std::lock_guard lock(stateMutex_);
        events_.Progress(stats, virtualPath);
    }
    return verdict;
}

Verdict Scanner::AnalyzeFile(const std::wstring& displayPath,
                             const std::wstring& realPath,
                             unsigned long long size,
                             const std::wstring& containerPath,
                             bool isVirtual,
                             ScanStats& stats,
                             std::vector<Detection>& detections,
                             bool& needsArchiveScan,
                             bool countInFileStats)
{
    needsArchiveScan = false;

    // Фильтр по размеру (документ п. 2.A): слишком большие файлы не анализируем.
    // Это потеря покрытия для ЛЮБОГО источника (аудит 2, п. 7): persistence-цель
    // с size > maxFileSize больше не исчезает молча — итог становится Partial.
    if (size > maxFileSize_) {
        {
            std::lock_guard lock(stateMutex_);
            stats.filesSkipped++;
        }
        events_.Warning(L"skipped oversized: " + displayPath);
        return Verdict::Clean;
    }

    std::string sha256;
    if (!Sha256::HashFile(realPath, sha256)) {
        {
            std::lock_guard lock(stateMutex_);
            stats.errors++;
        }
        events_.Warning(L"hash failed: " + displayPath);
        return Verdict::Clean;
    }

    // 1. Hash lookup по базе IOC — ВСЕГДА до кэша (п. 1 аудита): подписанная
    //    база обновляется, кэш — нет, поэтому кэш не имеет права перекрывать
    //    её вердикт (hash в malware-базе + чистый кэш = malware). Выполняется
    //    даже для allowlist-компонентов (документ п. 19). database_/allowlist_
    //    иммутабельны во время скана — читаются без блокировки.
    Verdict dbVerdict;
    std::wstring dbName;
    if (database_.Lookup(sha256, dbVerdict, dbName)) {
        Detection detection;
        detection.path = displayPath;
        detection.sha256 = sha256;
        detection.verdict = dbVerdict;
        detection.ruleId = L"HASH-DB";
        detection.description = dbName.empty() ? L"Known malicious hash" : dbName;
        detection.containerPath = containerPath;
        detection.isVirtual = isVirtual;
        {
            std::lock_guard lock(stateMutex_);
            if (countInFileStats) {
                stats.filesScanned++;
            }
            stats.detections++;
            detections.push_back(detection);
        }
        events_.DetectionEvent(detection);
        RememberVerdict(sha256, dbVerdict, detection);
        return dbVerdict;
    }

    if (countInFileStats) {
        std::lock_guard lock(stateMutex_);
        stats.filesScanned++;
    }

    // 2. Собственные компоненты SCU: clean без эвристик (документ п. 29).
    if (allowlist_.Contains(sha256)) {
        RememberVerdict(sha256, Verdict::Clean, Detection{});
        return Verdict::Clean;
    }

    // 3. Классификация по содержимому — ДО кэша (п. 3 аудита): «чистый»
    //    вердикт контейнера из кэша не доказывает безопасность содержимого,
    //    поэтому архив не может пройти мимо archive scan через cache hit.
    //    Классификация дешёвая (magic bytes) в сравнении с эвристиками.
    const FileType fileType = ClassifyFile(realPath);
    if (fileType == FileType::ArchiveZip) {
        needsArchiveScan = true;
        // Контейнер НЕ кэшируется вовсе: обновление базы может найти malware
        // внутри неизменённого архива — члены сканируются при каждой встрече.
        return Verdict::Clean;
    }

    // 4. Кэш вердиктов по SHA-256 (п. 25): только для вердиктов, посчитанных
    //    эвристиками (WinVerifyTrust/PE/скрипты); повторные копии того же файла
    //    не проходят их повторно. DB выше уже сказала «не в базе», а архив
    //    выше уже ушёл в archive scan — кэш не может замаскировать ни детект
    //    базы, ни содержимое контейнера. Кэш — под собственным cacheMutex_
    //    (порядок захвата всегда cacheMutex_ → stateMutex_).
    {
        std::lock_guard cacheLock(cacheMutex_);
        const auto cached = verdictCache_.find(sha256);
        if (cached != verdictCache_.end()) {
            if (cached->second.verdict != Verdict::Clean) {
                Detection clone = cached->second.detection;
                clone.path = displayPath;
                clone.containerPath = containerPath;
                clone.isVirtual = isVirtual;
                std::lock_guard stateLock(stateMutex_);
                stats.detections++;
                detections.push_back(clone);
                events_.DetectionEvent(clone);
            }
            return cached->second.verdict;
        }
    }

    // === Тяжёлый анализ — без общих блокировок ===

    if (fileType == FileType::Script && scanScripts_) {
        // Аудит: неудача чтения скрипта — не Clean. Вердикт не пишется в кэш
        // (отсутствие данных — не отрицательный ответ), потеря покрытия честно
        // попадает в статистику: TooLarge → filesSkipped (итог Partial),
        // IoError → errors (сбой чтения).
        ScriptAnalysis script;
        const ScriptReadStatus scriptStatus = ScriptScanner::Analyze(realPath, script);
        if (scriptStatus == ScriptReadStatus::TooLarge) {
            {
                std::lock_guard lock(stateMutex_);
                stats.filesSkipped++;
            }
            events_.Warning(L"skipped script (too large): " + displayPath);
            return Verdict::Clean;
        }
        if (scriptStatus == ScriptReadStatus::IoError) {
            {
                std::lock_guard lock(stateMutex_);
                stats.errors++;
            }
            events_.Warning(L"script read failed: " + displayPath);
            return Verdict::Clean;
        }

        if (script.score >= kSuspiciousScriptScore) {
            Detection detection;
            detection.path = displayPath;
            detection.sha256 = sha256;
            detection.verdict = Verdict::Suspicious;
            detection.ruleId = L"SCRIPT-HEURISTIC";
            detection.description = L"Multiple independent script signals";
            detection.score = script.score;
            detection.signals = script.signals;
            detection.containerPath = containerPath;
            detection.isVirtual = isVirtual;
            {
                std::lock_guard lock(stateMutex_);
                detections.push_back(detection);
                stats.detections++;
            }
            events_.DetectionEvent(detection);
            RememberVerdict(sha256, Verdict::Suspicious, detection);
            return Verdict::Suspicious;
        }
        RememberVerdict(sha256, Verdict::Clean, Detection{});
        return Verdict::Clean;
    }

    if (fileType != FileType::PE) {
        RememberVerdict(sha256, Verdict::Clean, Detection{});
        return Verdict::Clean;
    }

    const SignatureCheck signature = SignatureVerifier::Verify(realPath);
    const PeAnalysis pe = PeAnalyzer::Analyze(realPath);

    int score = pe.score;
    std::vector<std::wstring> signals = pe.signals;
    if (!signature.trusted) {
        score += 1;
        signals.push_back(L"unsigned-pe");
    }

    if (score >= kSuspiciousPeScore) {
        Detection detection;
        detection.path = displayPath;
        detection.sha256 = sha256;
        detection.verdict = Verdict::Suspicious;
        detection.ruleId = L"PE-HEURISTIC";
        detection.description = L"Multiple independent PE signals";
        detection.signedFile = signature.trusted;
        detection.publisher = signature.publisher;
        detection.score = score;
        detection.signals = signals;
        detection.containerPath = containerPath;
        detection.isVirtual = isVirtual;
        {
            std::lock_guard lock(stateMutex_);
            detections.push_back(detection);
            stats.detections++;
        }
        events_.DetectionEvent(detection);
        RememberVerdict(sha256, Verdict::Suspicious, detection);
        return Verdict::Suspicious;
    }

    RememberVerdict(sha256, Verdict::Clean, Detection{});
    return Verdict::Clean;
}

void Scanner::ScanPersistence(ScanStats& stats, std::vector<Detection>& detections)
{
    // Кэш вердиктов целей: одна цель анализируется один раз, сколько бы
    // записей (служб, задач) на неё ни указывало.
    std::unordered_map<std::wstring, Verdict> verdictCache;

    PersistenceScanner persistence;
    persistence.Enumerate(
        [&](const PersistenceEntry& entry) {
            if (IsCancelled()) {
                return;
            }

            const std::wstring displayPath = entry.location + L"\\" + entry.name;
            events_.Progress(stats, displayPath);

        Verdict verdict = Verdict::Clean;
        std::vector<std::wstring> signals;
        int score = 0;

        const bool hasTarget = !entry.targetPath.empty() && FileExistsNow(entry.targetPath);
        Verdict targetVerdict = Verdict::Clean;
        if (hasTarget) {
            const auto cached = verdictCache.find(entry.targetPath);
            if (cached != verdictCache.end()) {
                targetVerdict = cached->second;
            } else {
                bool needsArchiveScan = false;
                targetVerdict = AnalyzeFile(displayPath, entry.targetPath, GetFileSizeSafe(entry.targetPath),
                                            L"", false, stats, detections, needsArchiveScan,
                                            /*countInFileStats=*/false);

                // Persistence-цель — архив: AnalyzeFile анализирует только сам
                // контейнер (п. 4 аудита). Полный archive-скан обязателен, иначе
                // вредонос внутри цели остаётся скрытым; худший вердикт члена
                // прокидывается наружу как вердикт цели.
                if (targetVerdict == Verdict::Clean && needsArchiveScan && scanArchives_ && !IsCancelled()) {
                    targetVerdict = RunArchiveScan(entry.targetPath, entry.targetPath, stats, detections);
                }

                verdictCache[entry.targetPath] = targetVerdict;
            }

            if (targetVerdict != Verdict::Clean) {
                verdict = targetVerdict;
                signals.push_back(L"persistence-target:" + VerdictToWide(targetVerdict));
                score += 2;
            }
        }

        // П. SCAN-03: командная строка анализируется ВСЕГДА, а не только когда
        // цель отсутствует. Чистый host-exe (powershell.exe, wscript.exe) с
        // вредоносными аргументами (-enc <payload>, /c script.vbs) раньше
        // проходил как clean, пока существовал и был чист сам exe.
        const std::string commandLower = ToLowerAscii(entry.command);
        const ScriptAnalysis script = ScriptScanner::AnalyzeText(commandLower);
        if (script.score >= kSuspiciousScriptScore) {
            verdict = verdict == Verdict::Clean ? Verdict::Suspicious : verdict;
            score = std::max(score, script.score);
            signals.insert(signals.end(), script.signals.begin(), script.signals.end());
        }

        if (verdict == Verdict::Clean) {
            return; // и цель, и командная строка чистые
        }

        Detection detection;
        detection.path = displayPath;
        // GUI показывает и изолирует реальный файл цели, а не псевдопуть записи.
        detection.targetPath = entry.targetPath;
        detection.sha256 = entry.targetPath.empty() ? std::string() : HashTargetForReport(entry.targetPath);
        detection.verdict = verdict;
        detection.ruleId = L"PERSISTENCE";
        detection.description = targetVerdict != Verdict::Clean
            ? std::wstring(L"Autostart entry correlated with ") + VerdictToWide(targetVerdict) + L" target"
            : std::wstring(L"Suspicious autostart command");
        detection.score = score;
        detection.signals = signals;
        detection.source = L"persistence";
        events_.DetectionEvent(detection);
        detections.push_back(detection);
        stats.detections++;
    },
        // Намеренный пропуск записи автозапуска (глубина, reparse, oversized
        // registry value, битый XML задачи) — потеря покрытия (аудит 2, п. 7/11/12).
        [this, &stats](const std::wstring& path, const wchar_t* reason) {
            stats.filesSkipped++;
            events_.Warning(L"persistence skipped (" + std::wstring(reason) + L"): " + path);
        });
}

void Scanner::ScanProcesses(ScanStats& stats, std::vector<Detection>& detections)
{
    // Снапшот процессов: недоступность — техническая ошибка скана, а не
    // «процессов нет» (аудит 2, п. 6).
    const bool enumerated = ProcessScanner::Enumerate([&](const ProcessInfo& process) {
        if (IsCancelled()) {
            return;
        }

        // Пустой exePath (OpenProcess/QueryFullProcessImageNameW fail) — объект
        // не проверен: coverage skip, а не молчаливое «не найдено».
        if (process.exePath.empty()) {
            stats.filesSkipped++;
            wchar_t pidText[16];
            swprintf_s(pidText, L"%lu", process.pid);
            events_.Warning(L"process path unavailable (PID " + std::wstring(pidText) + L"): "
                            + process.name);
            return;
        }

        std::string sha256;
        if (!Sha256::HashFile(process.exePath, sha256)) {
            stats.errors++;
            events_.Warning(L"process hash failed: " + process.exePath);
            return;
        }

        // В срезе детект — только hash-DB (п. 18: подписи/пути недостаточны).
        Verdict dbVerdict;
        std::wstring dbName;
        if (!database_.Lookup(sha256, dbVerdict, dbName)
            || dbVerdict != Verdict::Malware) {
            return;
        }

        wchar_t pidText[16];
        swprintf_s(pidText, L"%lu", process.pid);

        Detection detection;
        detection.path = process.exePath;
        detection.sha256 = sha256;
        detection.verdict = Verdict::Malware;
        detection.ruleId = L"HASH-DB";
        detection.description = L"Malicious process: " + dbName + L" (PID " + pidText + L")";
        detection.source = L"process";
        events_.DetectionEvent(detection);
        detections.push_back(detection);
        stats.detections++;
    });

    if (!enumerated) {
        stats.errors++;
        events_.Warning(L"process snapshot unavailable");
    }
}

} // namespace scan
