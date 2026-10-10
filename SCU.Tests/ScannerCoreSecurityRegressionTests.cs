using System.Security.Cryptography;
using System.Text;
using SCU.Interop;
using SCU.Models.Scan;
using Xunit;

namespace SCU.Tests;

// Обязательные security regression tests (п. 11 аудита): кэш не перекрывает
// signed DB; смена версии движка/базы/профиля инвалидирует дисковый кэш;
// контейнер архива не кэшируется как доказательство безопасности; persistence-
// цель-архив проходит полный archive-скан; malformed hashes.txt отклоняется
// целиком. Вторая частичная часть ScannerCoreIntegrationTests (общие хелперы).
public partial class ScannerCoreIntegrationTests
{
    // Кэш и ключ переехали в доверенный каталог (ProgramData, строгий DACL —
    // подмена ключа не-elevated процессом невозможна); LocalApplicationData
    // остался только хранилищем старого кэша.
    private static string CacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SCU", "scanner-cache");

    private static string CacheFilePath() => Path.Combine(CacheDirectory(), "scan-cache.txt");

    private static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static List<string> ReadCacheLines()
    {
        var path = CacheFilePath();
        return File.Exists(path)
            ? [.. File.ReadAllLines(path)]
            : [];
    }

    private static void WriteCacheLines(IReadOnlyList<string> lines)
    {
        // Формат кэша LF-only (пишет ScannerCore '\n'): CRLF ломал бы сравнение
        // заголовка и HMAC строк при загрузке.
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath())!);
        File.WriteAllText(CacheFilePath(), string.Join("\n", lines) + "\n");
    }

    // Формат v3: строка = "<hex-hmac>\t<payload>", payload = "<sha256>\t<вердикт>
    // …". Проверка принадлежности хэша — по payload, а не по началу строки:
    // строку открывает HMAC, а не хэш записи.
    private static bool CacheLineHasPayload(string line, string sha256) =>
        line.EndsWith("\t" + sha256 + "\tclean", StringComparison.Ordinal)
        || line.Contains("\t" + sha256 + "\t", StringComparison.Ordinal);

    // Подмена вердикта в кэше на clean: если движок доверяет кэшу больше, чем
    // подписанной базе, детект исчезнет — тест это ловит.
    private static void TamperCacheVerdictToClean(string sha256)
    {
        var lines = ReadCacheLines();
        var prefix = sha256 + "\t";
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.Ordinal));
        var cleanLine = sha256 + "\tclean";
        if (index > 0)
        {
            lines[index] = cleanLine;
        }
        else
        {
            lines.Insert(Math.Min(1, lines.Count), cleanLine);
        }
        WriteCacheLines(lines);
    }

    private static void SeedCleanCacheEntry(string sha256)
    {
        var lines = ReadCacheLines();
        if (lines.Count == 0)
        {
            return; // нет кэша — некуда сеять; тест упадёт на assert и это честно
        }

        // Формат v3: каждая строка = "<hex-hmac>\t<payload>". Сеяная запись
        // обязана быть подписана тем же ключом (cache.key), иначе движок
        // отбросит её при загрузке — как и любую подделанную.
        var keyPath = Path.Combine(CacheDirectory(), "cache.key");
        if (!File.Exists(keyPath))
        {
            return; // ключа нет — дисковый кэш отключён; assert поймает это честно
        }

        var payload = sha256 + "\tclean";
        using var hmac = new HMACSHA256(File.ReadAllBytes(keyPath));
        var mac = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        lines.Insert(1, mac + "\t" + payload);
        WriteCacheLines(lines);
    }

    private static void TryDeleteCache()
    {
        try
        {
            File.Delete(CacheFilePath());
        }
        catch (IOException)
        {
        }
    }

    private static async Task InstallSignedPackageAsync(
        string scannerPath, string keyPath, string sha256, string name, string version)
    {
        var temp = Path.Combine(Path.GetTempPath(), "scu-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var hashesBytes = Encoding.UTF8.GetBytes($"{sha256}\tmalware\t{name}\n");
            var versionJson = Encoding.UTF8.GetBytes(
                $"{{\n  \"version\": \"{version}\",\n  \"date\": \"2099-01-01\",\n  \"entries\": 1\n}}\n");
            var packagePath = Path.Combine(temp, "package.zip");
            await File.WriteAllBytesAsync(packagePath, BuildSignedPackage(hashesBytes, versionJson, keyPath));

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("update");
                startInfo.ArgumentList.Add("--package");
                startInfo.ArgumentList.Add(packagePath);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            if (exitCode != 0)
            {
                // Диагностика на CI: события update и отпечаток приватного ключа
                // (только SHA256 — сам ключ не печатается), чтобы отличить
                // «не тот ключ» от иных причин сбоя установки пакета.
                Console.WriteLine(
                    "UPDATE FAILED | rc=" + exitCode
                    + " | key-sha256=" + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(keyPath))).ToLowerInvariant()[..16]
                    + " | events=" + string.Join("; ", events.Select(e =>
                        e.Kind + "/" + e.UpdateStatus + "/" + e.Message)));
            }

            Assert.Equal(0, exitCode);
            var updateEvent = Assert.Single(events, e => e.Kind == ScanEventKind.Update);
            Assert.Equal("ok", updateEvent.UpdateStatus);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static async Task<ScanResultDto?> ScanWithProfileAsync(string scannerPath, string target, long maxFileSize)
    {
        var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
        {
            startInfo.ArgumentList.Add("scan");
            startInfo.ArgumentList.Add("--mode");
            startInfo.ArgumentList.Add("custom");
            startInfo.ArgumentList.Add("--path");
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("--max-size");
            startInfo.ArgumentList.Add(maxFileSize.ToString());
            startInfo.ArgumentList.Add("--dev-unsigned-ok");
        });

        Assert.True(exitCode is 0 or 1, $"scanner failed: rc={exitCode}");
        return events.Find(e => e.Kind == ScanEventKind.Finished)?.Result;
    }

    // П. 1 аудита: hash в signed malware DB + «чистый» кэш = malware.
    [Fact]
    public async Task ScannerCore_SignedDb_BeatsTamperedCleanCache()
    {
        var keyPath = FindDatabaseSigningKey();
        if (FindScannerCore() is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

        if (SkipIfNotElevated())
        {
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var scannerPath = Path.Combine(sandbox, "ScannerCore.exe");
        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var ioc = "scu-cache-bypass-ioc-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(temp, "ioc.bin"), ioc);
            var iocHash = Sha256Hex(ioc);

            await InstallSignedPackageAsync(scannerPath, keyPath, iocHash, "Cache-Bypass-Ioc", "2099.03.01");

            var first = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(first);
            Assert.Contains(first!.Detections, d => d.Verdict == "malware" && d.Sha256 == iocHash);

            // Кэш после первого скана содержит вердикт — подменяем на clean.
            TamperCacheVerdictToClean(iocHash);

            var second = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(second);
            Assert.Contains(second!.Detections, d => d.Verdict == "malware" && d.Sha256 == iocHash);
        }
        finally
        {
            TryDeleteCache();
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // П. 2 аудита: смена версии базы инвалидирует дисковый кэш — записи из
    // старого кэша не переносятся в новый.
    [Fact]
    public async Task ScannerCore_DbVersionChange_InvalidatesCache()
    {
        var keyPath = FindDatabaseSigningKey();
        if (FindScannerCore() is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var scannerPath = Path.Combine(sandbox, "ScannerCore.exe");
        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(temp, "clean.txt"), "clean content");

            var baseline = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(baseline);

            var markerHash = Sha256Hex("scu-cache-marker-" + Guid.NewGuid().ToString("N"));
            SeedCleanCacheEntry(markerHash);

            var ioc = "scu-db-version-ioc-" + Guid.NewGuid().ToString("N");
            await InstallSignedPackageAsync(scannerPath, keyPath, Sha256Hex(ioc), "Db-Version-Ioc", "2099.03.02");

            await ScanDirectoryAsync(scannerPath, temp);

            var lines = ReadCacheLines();
            Assert.DoesNotContain(lines, line => line.Contains(markerHash, StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("#scucache", StringComparison.Ordinal)
                                            && line.Contains("db=2099.03.02", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteCache();
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // П. 2 аудита: изменение параметров профиля (maxFileSize) инвалидирует кэш.
    // Контрольный случай: при том же профиле сеяная запись сохраняется.
    [Fact]
    public async Task ScannerCore_ProfileChange_InvalidatesCache()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        if (SkipIfNotElevated())
        {
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(temp, "clean.txt"), "clean content");

            var baseline = await ScanWithProfileAsync(scannerPath, temp, maxFileSize: 1048576);
            Assert.NotNull(baseline);

            var markerHash = Sha256Hex("scu-profile-marker-" + Guid.NewGuid().ToString("N"));
            SeedCleanCacheEntry(markerHash);

            // Контроль: тот же профиль — сеяная запись переживает скан.
            await ScanWithProfileAsync(scannerPath, temp, maxFileSize: 1048576);
            Assert.Contains(ReadCacheLines(), line => line.Contains(markerHash, StringComparison.Ordinal));

            // Другой профиль — весь старый кэш отброшен.
            var changed = await ScanWithProfileAsync(scannerPath, temp, maxFileSize: 104857600);
            Assert.NotNull(changed);
            var lines = ReadCacheLines();
            Assert.DoesNotContain(lines, line => line.Contains(markerHash, StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("#scucache", StringComparison.Ordinal)
                                            && line.Contains("maxbytes=104857600", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteCache();
            Directory.Delete(temp, recursive: true);
        }
    }

    // П. 3 аудита: контейнер архива не кэшируется как proof-of-safety; даже
    // вручную подсунутая «чистая» запись кэша не отменяет скан содержимого.
    [Fact]
    public async Task ScannerCore_ArchiveContainer_NotTrustedFromCache()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var zipPath = Path.Combine(temp, "archive.zip");
            using (var zip = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("eicar.com");
                using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync(Eicar);
            }

            var first = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(first);
            Assert.Equal(1, first!.Summary.Detections);

            var zipHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zipPath))).ToLowerInvariant();

            // Контейнер не должен попасть в кэш вовсе (v3: payload внутри строки
            // за HMAC, поэтому проверка не по началу строки).
            Assert.DoesNotContain(ReadCacheLines(), line => CacheLineHasPayload(line, zipHash));

            // Даже подсаженная «чистая» запись не прячет malware в членах.
            TamperCacheVerdictToClean(zipHash);
            var second = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(second);
            Assert.Equal(1, second!.Summary.Detections);
        }
        finally
        {
            TryDeleteCache();
            Directory.Delete(temp, recursive: true);
        }
    }

    // П. 4 аудита: persistence-цель — архив: выполняется полный archive-скан,
    // malware внутри цели прокидывается в PERSISTENCE-детект.
    [Fact]
    public async Task ScannerCore_PersistenceCorrelation_ArchiveTarget()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var testKey = "Software\\SCU\\TestRun-" + Guid.NewGuid().ToString("N");
        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var zipPath = Path.Combine(temp, "autostart.zip");
            using (var zip = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("eicar.com");
                using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync(Eicar);
            }

            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey))
            {
                key.SetValue("SCU-Test-Mal", zipPath, Microsoft.Win32.RegistryValueKind.String);
            }

            Environment.SetEnvironmentVariable("SCU_TEST_RUN_KEY", testKey);
            try
            {
                var result = await ScanDirectoryAsync(scannerPath, temp);

                Assert.NotNull(result);
                Assert.Contains(result!.Detections, detection =>
                    detection.Source == "persistence"
                    && detection.Verdict == "malware"
                    && detection.RuleId == "PERSISTENCE");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SCU_TEST_RUN_KEY", null);
            }
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
            Directory.Delete(temp, recursive: true);
        }
    }

    // П. 7 аудита: malformed hashes.txt отклоняется ЦЕЛИКОМ, даже с валидной
    // подписью. Каждый случай — отдельная строка с корректной ECDSA-подписью.
    public static TheoryData<string, string> MalformedHashesCases() => new()
    {
        { "short hash", new string('a', 63) + "\tmalware\tX\n" },
        { "long hash", new string('a', 65) + "\tmalware\tX\n" },
        { "uppercase hex", new string('A', 64) + "\tmalware\tX\n" },
        { "non-hex chars", new string('g', 64) + "\tmalware\tX\n" },
        { "empty hash", "\tmalware\tX\n" },
        { "invalid verdict", new string('a', 64) + "\tvirus\tX\n" },
        { "missing name", new string('a', 64) + "\tmalware\n" },
        { "extra field", new string('a', 64) + "\tmalware\tX\textra\n" },
        { "duplicate hash", new string('a', 64) + "\tmalware\tX\n" + new string('a', 64) + "\tmalware\tY\n" },
        { "oversized name", new string('a', 64) + "\tmalware\t" + new string('n', 257) + "\n" },
        { "crlf line", new string('a', 64) + "\tmalware\tX\r\n" },
        { "missing tab", new string('a', 64) + "malware X\n" },
    };

    [Theory]
    [MemberData(nameof(MalformedHashesCases))]
    public async Task ScannerCore_RejectsMalformedHashesFile(string _, string hashesContent)
    {
        var keyPath = FindDatabaseSigningKey();
        if (FindScannerCore() is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var scannerPath = Path.Combine(sandbox, "ScannerCore.exe");
        var temp = Path.Combine(Path.GetTempPath(), "scu-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var hashesBytes = Encoding.UTF8.GetBytes(hashesContent);
            var versionJson = Encoding.UTF8.GetBytes(
                "{\n  \"version\": \"2099.04.01\",\n  \"date\": \"2099-01-01\",\n  \"entries\": 1\n}\n");
            var packagePath = Path.Combine(temp, "package.zip");
            await File.WriteAllBytesAsync(packagePath, BuildSignedPackage(hashesBytes, versionJson, keyPath));

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("update");
                startInfo.ArgumentList.Add("--package");
                startInfo.ArgumentList.Add(packagePath);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.NotEqual(0, exitCode);
            var updateEvent = Assert.Single(events, e => e.Kind == ScanEventKind.Update);
            Assert.Equal("failed", updateEvent.UpdateStatus);
            Assert.Contains("rejected", updateEvent.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }
    // П. 11 аудита: пакет, подписанный чужим ключом, отклоняется — подпись
    // обязана проверяться против вшитого публичного ключа издателя.
    [Fact]
    public async Task ScannerCore_RejectsPackageSignedWithWrongKey()
    {
        if (FindScannerCore() is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var scannerPath = Path.Combine(sandbox, "ScannerCore.exe");
        var temp = Path.Combine(Path.GetTempPath(), "scu-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var hashesBytes = System.Text.Encoding.UTF8.GetBytes(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\tmalware\tWrong-Key\n");
            var versionJson = System.Text.Encoding.UTF8.GetBytes(
                "{\n  \"version\": \"2099.05.01\",\n  \"date\": \"2099-01-01\",\n  \"entries\": 1\n}\n");

            // Чужой ключ: подпись валидна математически, но не от издателя.
            using var outsider = System.Security.Cryptography.ECDsa.Create();
            var signature = outsider.SignData(hashesBytes, System.Security.Cryptography.HashAlgorithmName.SHA256);

            var packagePath = Path.Combine(temp, "wrong-key.zip");
            using (var stream = File.Create(packagePath))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                AddZipEntry(zip, "hashes.txt", hashesBytes);
                AddZipEntry(zip, "hashes.txt.sig", signature);
                AddZipEntry(zip, "db-version.json", versionJson);
            }

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("update");
                startInfo.ArgumentList.Add("--package");
                startInfo.ArgumentList.Add(packagePath);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.NotEqual(0, exitCode);
            var updateEvent = Assert.Single(events, e => e.Kind == ScanEventKind.Update);
            Assert.Equal("failed", updateEvent.UpdateStatus);
            Assert.Contains("signature", updateEvent.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // П. 11 аудита: отмена прерывает скан тем же механизмом, что и у
    // пользователя — частичный отчёт, код 3, cancelled=true.
    [Fact]
    public async Task ScannerCore_Cancellation_StopsWithPartialResult()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            for (var i = 0; i < 30; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(temp, $"file-{i}.txt"), $"content {i}");
            }

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("scan");
                startInfo.ArgumentList.Add("--mode");
                startInfo.ArgumentList.Add("custom");
                startInfo.ArgumentList.Add("--path");
                startInfo.ArgumentList.Add(temp);
                startInfo.ArgumentList.Add("--threads");
                startInfo.ArgumentList.Add("1");
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
                startInfo.EnvironmentVariables["SCU_TEST_CANCEL_AFTER"] = "5";
            });

            Assert.Equal(3, exitCode);
            var result = events.Find(e => e.Kind == ScanEventKind.Finished)?.Result;
            Assert.NotNull(result);
            Assert.True(result!.Cancelled);
            Assert.True(result.Summary.FilesScanned < 30, "скан должен прерваться до конца списка");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    // П. 11 аудита: повреждённая база на диске не роняет сканер — fail-closed,
    // отчёт формируется (встроенный EICAR IOC продолжает детектироваться).
    [Fact]
    public async Task ScannerCore_CorruptedDatabaseOnDisk_FailsClosed()
    {
        if (FindScannerCore() is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        // П. 5 аудита: порча базы — только в собственной песочнице теста.
        var scannerPath = Path.Combine(sandbox, "ScannerCore.exe");
        var databaseFile = Path.Combine(sandbox, "security", "database", "hashes.txt");
        if (!File.Exists(databaseFile))
        {
            Assert.Fail("security/database/hashes.txt не найден в песочнице — отсутствует сид SecurityScanner/database.");
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await File.WriteAllBytesAsync(databaseFile,
                new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02, 0x0A, 0xFF });
            await File.WriteAllTextAsync(Path.Combine(temp, "e1.com"), Eicar);

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("scan");
                startInfo.ArgumentList.Add("--mode");
                startInfo.ArgumentList.Add("custom");
                startInfo.ArgumentList.Add("--path");
                startInfo.ArgumentList.Add(temp);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.True(exitCode is 0 or 1, $"scanner crashed on corrupted db: rc={exitCode}");
            var result = events.Find(e => e.Kind == ScanEventKind.Finished)?.Result;
            Assert.NotNull(result);
            Assert.Contains(result!.Detections, d => d.Verdict == "malware");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // П. 11 аудита: несовпадение версии движка в заголовке кэша отбрасывает ВЕСЬ
    // файл кэша — подсаженная «чистая» запись не переносится. Заголовок берётся
    // реальный (после базового скана), подделывается только engine=.
    [Fact]
    public async Task ScannerCore_EngineVersionChange_InvalidatesCache()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        if (SkipIfNotElevated())
        {
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(temp, "clean.txt"), "clean content");

            var baseline = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(baseline);

            var lines = ReadCacheLines();
            Assert.True(lines.Count > 0, "после скана дисковый кэш обязан существовать");
            var forgedHeader = System.Text.RegularExpressions.Regex.Replace(
                lines[0], @"engine=\S+", "engine=0.0.0-forged");
            Assert.NotEqual(lines[0], forgedHeader);

            var markerHash = Sha256Hex("scu-engine-marker-" + Guid.NewGuid().ToString("N"));
            WriteCacheLines([forgedHeader, markerHash + "\tclean"]);

            await ScanDirectoryAsync(scannerPath, temp);

            var after = ReadCacheLines();
            Assert.DoesNotContain(after, line => line.Contains(markerHash, StringComparison.Ordinal));
            Assert.Contains(after, line => line.StartsWith("#scucache", StringComparison.Ordinal)
                                            && !line.Contains("0.0.0-forged", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteCache();
            Directory.Delete(temp, recursive: true);
        }
    }

    // П. 11 аудита: вложенный архив (zip в zip) — «чистая» запись кэша внешнего
    // контейнера не отменяет скан содержимого; malware из inner.zip детектируется
    // при повторном скане, внешний контейнер не кэшируется.
    [Fact]
    public async Task ScannerCore_NestedArchive_CleanCacheOnContainer_DoesNotHideMalware()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var outerPath = Path.Combine(temp, "outer.zip");
            using (var outer = new System.IO.Compression.ZipArchive(
                File.Create(outerPath), System.IO.Compression.ZipArchiveMode.Create))
            {
                var innerEntry = outer.CreateEntry("inner.zip");
                using (var innerStream = innerEntry.Open())
                using (var inner = new System.IO.Compression.ZipArchive(
                    innerStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = inner.CreateEntry("eicar.com");
                    using var writer = new StreamWriter(entry.Open());
                    await writer.WriteAsync(Eicar);
                }
            }

            var first = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(first);
            Assert.Equal(1, first!.Summary.Detections);

            var outerHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(outerPath)))
                .ToLowerInvariant();
            Assert.DoesNotContain(ReadCacheLines(),
                line => CacheLineHasPayload(line, outerHash));

            // Подсаживаем «чистый» вердикт для внешнего контейнера — не помогает.
            TamperCacheVerdictToClean(outerHash);
            var second = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(second);
            Assert.Equal(1, second!.Summary.Detections);
        }
        finally
        {
            TryDeleteCache();
            Directory.Delete(temp, recursive: true);
        }
    }

    // П. 11 аудита: payload пакета, изменённый ПОСЛЕ подписи, отклоняется —
    // подпись обязана проверяться именно против того содержимого, что в пакете.
    [Fact]
    public async Task ScannerCore_RejectsModifiedPayloadWithValidSignature()
    {
        var keyPath = FindDatabaseSigningKey();
        if (FindScannerCore() is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var scannerPath = Path.Combine(sandbox, "ScannerCore.exe");
        var temp = Path.Combine(Path.GetTempPath(), "scu-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var hashesBytes = Encoding.UTF8.GetBytes(
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\tmalware\tOriginal\n");
            var versionJson = Encoding.UTF8.GetBytes(
                "{\n  \"version\": \"2099.07.01\",\n  \"date\": \"2099-01-01\",\n  \"entries\": 1\n}\n");
            var package = BuildSignedPackage(hashesBytes, versionJson, keyPath);

            // Payload заменён после подписи: подпись осталась от исходного содержимого.
            var tamperedHashes = Encoding.UTF8.GetBytes(
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\tmalware\tModified\n");
            var packagePath = Path.Combine(temp, "modified-payload.zip");
            await File.WriteAllBytesAsync(packagePath, ReplaceZipEntry(package, "hashes.txt", tamperedHashes));

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("update");
                startInfo.ArgumentList.Add("--package");
                startInfo.ArgumentList.Add(packagePath);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.NotEqual(0, exitCode);
            var updateEvent = Assert.Single(events, e => e.Kind == ScanEventKind.Update);
            Assert.Equal("failed", updateEvent.UpdateStatus);
            Assert.Contains("signature", updateEvent.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // П. 11 аудита: atomic replacement — НЕУДАВШЕЕСЯ обновление не портит
    // установленную базу: IOC из предыдущего пакета продолжает детектироваться,
    // версия базы не меняется.
    [Fact]
    public async Task ScannerCore_FailedUpdate_KeepsPreviousDatabase()
    {
        var keyPath = FindDatabaseSigningKey();
        if (FindScannerCore() is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var scannerPath = Path.Combine(sandbox, "ScannerCore.exe");
        var databaseDirectory = Path.Combine(sandbox, "security", "database");
        var databaseFile = Path.Combine(databaseDirectory, "hashes.txt");
        var versionFile = Path.Combine(databaseDirectory, "db-version.json");
        if (!File.Exists(databaseFile))
        {
            Assert.Fail("security/database/hashes.txt не найден в песочнице — отсутствует сид SecurityScanner/database.");
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-scan-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var ioc = "scu-atomic-ioc-" + Guid.NewGuid().ToString("N");
            await InstallSignedPackageAsync(scannerPath, keyPath, Sha256Hex(ioc), "Atomic-Ioc", "2099.08.01");

            // Обновление с битым пакетом (подпись удалена из zip) обязано провалиться.
            var brokenPath = Path.Combine(temp, "broken.zip");
            using (var stream = File.Create(brokenPath))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                AddZipEntry(zip, "hashes.txt", Encoding.UTF8.GetBytes(
                    "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\tmalware\tBroken\n"));
                AddZipEntry(zip, "db-version.json", Encoding.UTF8.GetBytes(
                    "{\n  \"version\": \"2099.08.02\",\n  \"date\": \"2099-01-01\",\n  \"entries\": 1\n}\n"));
            }

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("update");
                startInfo.ArgumentList.Add("--package");
                startInfo.ArgumentList.Add(brokenPath);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });
            Assert.NotEqual(0, exitCode);

            // База от предыдущего успешного обновления жива: IOC детектируется.
            await File.WriteAllTextAsync(Path.Combine(temp, "ioc.bin"), ioc);
            var scan = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(scan);
            Assert.Contains(scan!.Detections, d => d.Verdict == "malware" && d.Sha256 == Sha256Hex(ioc));

            var version = await File.ReadAllTextAsync(versionFile);
            Assert.Contains("2099.08.01", version, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // Аудит пакета базы (P1): член архива, распаковывающийся в огромный размер,
    // отклоняется по заголовку ZIP ДО выделения памяти — сжатый член при этом
    // копеечный, поэтому раньше лимит срабатывал только после аллокации.
    [Fact]
    public async Task ScannerCore_RejectsPackageWithOversizedUncompressedEntry()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var packagePath = Path.Combine(temp, "oversized.zip");
            using (var stream = File.Create(packagePath))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                // ~70 МБ повторяющихся строк: сжатие делает член копеечным,
                // распакованный размер — сверх лимита 64 МБ.
                var entry = zip.CreateEntry("hashes.txt");
                using var writer = new StreamWriter(entry.Open());
                var line = new string('a', 100) + "\n";
                for (var i = 0; i < 700_000; i++)
                {
                    writer.Write(line);
                }
            }

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("update");
                startInfo.ArgumentList.Add("--package");
                startInfo.ArgumentList.Add(packagePath);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.NotEqual(0, exitCode);
            var updateEvent = Assert.Single(events, e => e.Kind == ScanEventKind.Update);
            Assert.Equal("failed", updateEvent.UpdateStatus);
            Assert.Contains("too large", updateEvent.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // Аудит пакета базы (P1): лимит на число членов архива — защита от
    // мусорных/злонамеренных пакетов с сотнями членов.
    [Fact]
    public async Task ScannerCore_RejectsPackageWithTooManyMembers()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var packagePath = Path.Combine(temp, "many-members.zip");
            using (var stream = File.Create(packagePath))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                for (var i = 0; i < 40; i++)
                {
                    AddZipEntry(zip, $"filler-{i}.txt", Encoding.UTF8.GetBytes("x"));
                }
            }

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("update");
                startInfo.ArgumentList.Add("--package");
                startInfo.ArgumentList.Add(packagePath);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.NotEqual(0, exitCode);
            var updateEvent = Assert.Single(events, e => e.Kind == ScanEventKind.Update);
            Assert.Equal("failed", updateEvent.UpdateStatus);
            Assert.Contains("too many entries", updateEvent.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // Аудит пакета базы: metadata entries не совпадает с фактическим числом
    // записей — пакет с рассинхроном metadata отклоняется целиком.
    [Fact]
    public async Task ScannerCore_RejectsPackageWithEntriesMetadataMismatch()
    {
        var keyPath = FindDatabaseSigningKey();
        if (FindScannerCore() is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var scannerPath = Path.Combine(sandbox, "ScannerCore.exe");
        var temp = Path.Combine(Path.GetTempPath(), "scu-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // Подпись валидна, схема валидна, но metadata объявляет 2 записи
            // при одной фактической.
            var hashesBytes = Encoding.UTF8.GetBytes(
                "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\tmalware\tMismatch\n");
            var versionJson = Encoding.UTF8.GetBytes(
                "{\n  \"version\": \"2099.09.01\",\n  \"date\": \"2099-01-01\",\n  \"entries\": 2\n}\n");
            var packagePath = Path.Combine(temp, "mismatch.zip");
            await File.WriteAllBytesAsync(packagePath, BuildSignedPackage(hashesBytes, versionJson, keyPath));

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("update");
                startInfo.ArgumentList.Add("--package");
                startInfo.ArgumentList.Add(packagePath);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.NotEqual(0, exitCode);
            var updateEvent = Assert.Single(events, e => e.Kind == ScanEventKind.Update);
            Assert.Equal("failed", updateEvent.UpdateStatus);
            Assert.Contains("entries mismatch", updateEvent.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // Аудит PE-анализатора: malformed PE с экстремальными значениями заголовков
    // обязан обрабатываться без crash — corpus проходит скан целиком, сканер
    // завершает анализ с валидным кодом возврата (checked в ScanDirectoryAsync).
    [Fact]
    public async Task ScannerCore_MalformedPeCorpus_AnalyzesWithoutCrash()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var sandbox = TryCreateScannerSandbox();
        if (sandbox is null)
        {
            ReportSkip();
            return;
        }

        var scannerPathInSandbox = Path.Combine(sandbox, "ScannerCore.exe");
        var temp = Path.Combine(Path.GetTempPath(), "scu-pe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // e_lfanew: 0, UINT32_MAX, ровно размер буфера чтения, на границе.
            await File.WriteAllBytesAsync(Path.Combine(temp, "pe-lfanew-zero.bin"),
                BuildMalformedPe(eLfanew: 0, withNtHeaders: false, numberOfSections: 0, sizeOfOptionalHeader: 0));
            await File.WriteAllBytesAsync(Path.Combine(temp, "pe-lfanew-max.bin"),
                BuildMalformedPe(eLfanew: unchecked((int)0xFFFFFFFF), withNtHeaders: false, numberOfSections: 0, sizeOfOptionalHeader: 0));
            await File.WriteAllBytesAsync(Path.Combine(temp, "pe-lfanew-at-read.bin"),
                BuildMalformedPe(eLfanew: 64 * 1024, withNtHeaders: false, numberOfSections: 0, sizeOfOptionalHeader: 0));
            await File.WriteAllBytesAsync(Path.Combine(temp, "pe-lfanew-inside.bin"),
                BuildMalformedPe(eLfanew: 100, withNtHeaders: false, numberOfSections: 0, sizeOfOptionalHeader: 0));
            // Валидная сигнатура PE, но экстремальные FileHeader: секционная таблица
            // не влезает в 64-КБ буфер — malformed, не crash.
            await File.WriteAllBytesAsync(Path.Combine(temp, "pe-sections-ffff.bin"),
                BuildMalformedPe(eLfanew: 64, withNtHeaders: true, numberOfSections: 0xFFFF, sizeOfOptionalHeader: 0xFFFF));
            await File.WriteAllBytesAsync(Path.Combine(temp, "pe-sections-max-opt.bin"),
                BuildMalformedPe(eLfanew: 64, withNtHeaders: true, numberOfSections: 0x2000, sizeOfOptionalHeader: 0x1000));

            var result = await ScanDirectoryAsync(scannerPathInSandbox, temp);

            // Главное утверждение: сканер дожил до finished с результатом.
            Assert.NotNull(result);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // Сборка malformed PE: «MZ» + e_lfanew по смещению 0x3C; при withNtHeaders —
    // «PE\0\0» + IMAGE_FILE_HEADER с заданными NumberOfSections/SizeOfOptionalHeader.
    private static byte[] BuildMalformedPe(int eLfanew, bool withNtHeaders, ushort numberOfSections, ushort sizeOfOptionalHeader)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x5A4D); // IMAGE_DOS_SIGNATURE «MZ»
        writer.Write(new byte[0x3A]); // хвост DOS header до e_lfanew
        writer.Write(eLfanew);        // LONG e_lfanew на смещении 0x3C
        if (withNtHeaders)
        {
            writer.Seek(eLfanew, SeekOrigin.Begin);
            writer.Write(0x00004550u); // IMAGE_NT_SIGNATURE «PE\0\0»
            writer.Write((ushort)0x8664);             // Machine
            writer.Write(numberOfSections);           // NumberOfSections
            writer.Write(0u);                         // TimeDateStamp
            writer.Write(0u);                         // PointerToSymbolTable
            writer.Write(0u);                         // NumberOfSymbols
            writer.Write(sizeOfOptionalHeader);       // SizeOfOptionalHeader
            writer.Write((ushort)0x0102);             // Characteristics
        }

        // Минимум 64 байта: меньше читатель заголовков не рассматривает.
        if (stream.Length < 64)
        {
            writer.Write(new byte[64 - stream.Length]);
        }

        writer.Flush();
        return stream.ToArray();
    }

    // Замена содержимого одной записи в уже собранном zip-пакете.
    private static byte[] ReplaceZipEntry(byte[] package, string name, byte[] content)
    {
        using var source = new MemoryStream(package);
        using var output = new MemoryStream();
        using (var input = new System.IO.Compression.ZipArchive(source, System.IO.Compression.ZipArchiveMode.Read))
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var entry in input.Entries)
            {
                var target = zip.CreateEntry(entry.FullName);
                using var entryStream = entry.Open();
                using var targetStream = target.Open();
                if (entry.FullName == name)
                {
                    targetStream.Write(content);
                }
                else
                {
                    entryStream.CopyTo(targetStream);
                }
            }
        }
        return output.ToArray();
    }
}

