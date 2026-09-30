#:property LangVersion=latest
#:property Nullable=enable
#:package System.IO.Compression.ZipFile@4.3.0

// DatabaseBuilder — сборка подписанных offline-пакетов базы сигнатур
// (документ п. 12/30/33). Приватный ключ живёт ТОЛЬКО здесь (tools/),
// в приложении и ScannerCore — только публичный.
//
// Команды:
//   dotnet run DatabaseBuilder.cs -- genkeys <output-dir>
//   dotnet run DatabaseBuilder.cs -- fetch --out <hashes.txt>
//                                   [--source <имя|url>]...   (по умолчанию — внешние дампы)
//   dotnet run DatabaseBuilder.cs -- build --key <private.pem> --hashes <hashes.txt>
//                                   --version 2026.09.25 --out <package.zip>
//
// fetch собирает hashes.txt из публичных дампов SHA256 (MalwareBazaar recent и
// безключевые GitHub-агрегаторы): строки, совпавшие с ^[0-9a-fA-F]{64}$,
// приводятся к нижнему регистру, дедуплицируются и сортируются. Файл пишется
// в формате пакета (sha256<TAB>malware<TAB>имя), LF-only, UTF-8 без BOM —
// строгая схема ValidateHashesSchema в ScannerCore принимает его без правок.
//
// Формат пакета (ZIP): hashes.txt (sha256<TAB>verdict<TAB>name), hashes.txt.sig
// (ECDSA P-256 подпись SHA-256(hashes.txt), r||s 64 байта), db-version.json.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: genkeys <dir> | fetch --out <txt> [--source <name|url>]... | build --key <pem> --hashes <txt> --version <v> --out <zip>");
    return 2;
}

switch (args[0])
{
    case "genkeys": return RunGenKeys(args);
    case "fetch": return RunFetch(args);
    case "build": return RunBuild(args);
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}

// Источники по умолчанию: ключ не нужен. MalwareBazaar recent — свежие образцы
// за сутки; агрегаторы на GitHub — накопленные списки (~85k и ~120k хешей).
static (string Name, string Url)[] DefaultSources() =>
[
    ("MalwareBazaar", "https://bazaar.abuse.ch/export/txt/sha256/recent/"),
    ("malicious-hash", "https://raw.githubusercontent.com/romainmarcoux/malicious-hash/main/full-hash-sha256-aa.txt"),
    ("MHTL-aa", "https://raw.githubusercontent.com/amitambekar510/Malicious-Hash-Threat-List/main/hashes/sha256/malicious_SHA256_hashes_aa.txt"),
    ("MHTL-ab", "https://raw.githubusercontent.com/amitambekar510/Malicious-Hash-Threat-List/main/hashes/sha256/malicious_SHA256_hashes_ab.txt"),
    ("MHTL-ac", "https://raw.githubusercontent.com/amitambekar510/Malicious-Hash-Threat-List/main/hashes/sha256/malicious_SHA256_hashes_ac.txt"),
];

static int RunFetch(string[] args)
{
    string? outputPath = null;
    var sources = new List<(string Name, string Url)>();
    for (var i = 1; i < args.Length - 1; i += 2)
    {
        switch (args[i])
        {
            case "--out":
                outputPath = args[i + 1];
                break;
            case "--source":
                var separator = args[i + 1].IndexOf('|');
                if (separator <= 0 || separator == args[i + 1].Length - 1)
                {
                    Console.Error.WriteLine("fetch: --source expects <name|url>");
                    return 2;
                }
                sources.Add((args[i + 1][..separator], args[i + 1][(separator + 1)..]));
                break;
        }
    }

    if (outputPath is null)
    {
        Console.Error.WriteLine("fetch: --out is required");
        return 2;
    }

    if (sources.Count == 0)
    {
        sources.AddRange(DefaultSources());
    }

    // hash -> имя источника: при дедупликации побеждает первый источник,
    // написавший хеш (порядок в DefaultSources).
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    var succeeded = 0;
    using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
    {
        // abuse.ch и raw.githubusercontent отклоняют запросы без User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SCU-DatabaseBuilder/1.0");
        foreach (var (name, url) in sources)
        {
            try
            {
                var text = http.GetStringAsync(url).GetAwaiter().GetResult();
                var added = AddHashes(hashes, text, name);
                succeeded++;
                Console.WriteLine($"source ok: {name} (+{added} new, {hashes.Count} total)");
            }
            catch (Exception exception)
            {
                // Один упавший источник (сеть, лимит, требование ключа) не срывает
                // сборку: база соберётся из остальных; все — ошибка ниже.
                Console.Error.WriteLine($"source failed: {name} | {exception.Message}");
            }
        }
    }

    if (succeeded == 0)
    {
        Console.Error.WriteLine("fetch: all sources failed");
        return 1;
    }

    if (hashes.Count == 0)
    {
        Console.Error.WriteLine("fetch: no hashes parsed");
        return 1;
    }

    WriteHashesFile(outputPath, hashes);
    Console.WriteLine($"hashes written: {outputPath} ({hashes.Count} entries)");
    return 0;
}

// Извлекает хеши из дампа: строка целиком — 64 hex-символа (заголовки, комментарии
// и мусор отфильтровываются), регистр приводится к нижнему (требование схемы).
static int AddHashes(Dictionary<string, string> hashes, string text, string name)
{
    var added = 0;
    foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        if (line.Length == 64)
        {
            var isHex = true;
            foreach (var c in line)
            {
                if (!char.IsAsciiHexDigit(c))
                {
                    isHex = false;
                    break;
                }
            }

            if (isHex && hashes.TryAdd(line.ToLowerInvariant(), name))
            {
                added++;
            }
        }
    }

    return added;
}

// Строгая схема пакета: LF-only (никаких CR), UTF-8 без BOM, имя источника
// без TAB/CR/LF и не длиннее 256 байт (иначе ValidateHashesSchema отвергнет
// весь файл).
static void WriteHashesFile(string outputPath, Dictionary<string, string> hashes)
{
    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using var writer = new StreamWriter(outputPath, append: false, utf8);
    writer.NewLine = "\n";
    foreach (var hash in hashes.Keys.OrderBy(h => h, StringComparer.Ordinal))
    {
        writer.Write(hash);
        writer.Write('\t');
        writer.Write("malware");
        writer.Write('\t');
        writer.WriteLine(SanitizeSourceName(hashes[hash]));
    }
}

static string SanitizeSourceName(string name)
{
    var cleaned = name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
    if (cleaned.Length == 0)
    {
        cleaned = "unknown";
    }

    // Обрезка по границе символа: имя попадает в UI, обрубленная UTF-8
    // последовательность отвергается схемой.
    var utf8 = Encoding.UTF8;
    if (utf8.GetByteCount(cleaned) <= 256)
    {
        return cleaned;
    }

    var result = cleaned;
    while (utf8.GetByteCount(result) > 256)
    {
        result = result[..^1];
    }

    return result;
}

static int RunGenKeys(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("genkeys: missing output dir");
        return 2;
    }

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var parameters = ecdsa.ExportExplicitParameters(includePrivateParameters: true);
    var x = Convert.ToHexString(parameters.Q.X).ToLowerInvariant();
    var y = Convert.ToHexString(parameters.Q.Y).ToLowerInvariant();

    Directory.CreateDirectory(args[1]);
    File.WriteAllText(Path.Combine(args[1], "db-signing-private.pem"), ecdsa.ExportECPrivateKeyPem());
    File.WriteAllText(Path.Combine(args[1], "db-signing-public.pem"), ecdsa.ExportSubjectPublicKeyInfoPem());

    Console.WriteLine("PUBLIC_XY=" + x + y);
    Console.WriteLine("keys written to " + args[1]);
    return 0;
}

static int RunBuild(string[] args)
{
    string? keyPath = null, hashesPath = null, version = null, outputPath = null;
    for (var i = 1; i < args.Length - 1; i += 2)
    {
        switch (args[i])
        {
            case "--key": keyPath = args[i + 1]; break;
            case "--hashes": hashesPath = args[i + 1]; break;
            case "--version": version = args[i + 1]; break;
            case "--out": outputPath = args[i + 1]; break;
        }
    }

    if (keyPath is null || hashesPath is null || version is null || outputPath is null)
    {
        Console.Error.WriteLine("build: --key, --hashes, --version and --out are required");
        return 2;
    }

    var hashesBytes = File.ReadAllBytes(hashesPath);
    var entries = CountEntries(hashesBytes);
    if (entries == 0)
    {
        Console.Error.WriteLine("build: hashes file is empty");
        return 2;
    }

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(File.ReadAllText(keyPath));

    var signature = ecdsa.SignData(hashesBytes, HashAlgorithmName.SHA256);
    if (signature.Length != 64)
    {
        Console.Error.WriteLine($"build: unexpected signature length {signature.Length}");
        return 2;
    }

    var dbVersionJson = new StringBuilder()
        .Append("{\n")
        .Append("  \"version\": \"").Append(version).Append("\",\n")
        .Append("  \"date\": \"").Append(DateTime.UtcNow.ToString("yyyy-MM-dd")).Append("\",\n")
        .Append("  \"entries\": ").Append(entries).Append("\n")
        .Append("}\n")
        .ToString();

    using var stream = File.Create(outputPath);
    using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
    WriteEntry(zip, "hashes.txt", hashesBytes);
    WriteEntry(zip, "hashes.txt.sig", signature);
    WriteEntry(zip, "db-version.json", Encoding.UTF8.GetBytes(dbVersionJson));

    Console.WriteLine($"package written: {outputPath} ({entries} entries, version {version})");
    return 0;
}

static void WriteEntry(ZipArchive zip, string name, byte[] content)
{
    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
    using var entryStream = entry.Open();
    entryStream.Write(content);
}

static int CountEntries(byte[] bytes)
{
    var count = 0;
    foreach (var b in bytes)
    {
        if (b == (byte)'\n')
        {
            count++;
        }
    }

    return count;
}
