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
    private static string CacheFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCU", "scan-cache.txt");

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
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath())!);
        File.WriteAllLines(CacheFilePath(), lines);
    }

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
        lines.Insert(1, sha256 + "\tclean");
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
        var scannerPath = FindScannerCore();
        var keyPath = FindDatabaseSigningKey();
        if (scannerPath is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

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
        }
    }

    // П. 2 аудита: смена версии базы инвалидирует дисковый кэш — записи из
    // старого кэша не переносятся в новый.
    [Fact]
    public async Task ScannerCore_DbVersionChange_InvalidatesCache()
    {
        var scannerPath = FindScannerCore();
        var keyPath = FindDatabaseSigningKey();
        if (scannerPath is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

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
            Assert.DoesNotContain(lines, line => line.StartsWith(markerHash, StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("#scucache", StringComparison.Ordinal)
                                            && line.Contains("db=2099.03.02", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteCache();
            Directory.Delete(temp, recursive: true);
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
            Assert.Contains(ReadCacheLines(), line => line.StartsWith(markerHash, StringComparison.Ordinal));

            // Другой профиль — весь старый кэш отброшен.
            var changed = await ScanWithProfileAsync(scannerPath, temp, maxFileSize: 104857600);
            Assert.NotNull(changed);
            var lines = ReadCacheLines();
            Assert.DoesNotContain(lines, line => line.StartsWith(markerHash, StringComparison.Ordinal));
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

            // Контейнер не должен попасть в кэш вовсе.
            Assert.DoesNotContain(ReadCacheLines(), line => line.StartsWith(zipHash + "\t", StringComparison.Ordinal));

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
        var scannerPath = FindScannerCore();
        var keyPath = FindDatabaseSigningKey();
        if (scannerPath is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

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
        }
    }
    // П. 11 аудита: пакет, подписанный чужим ключом, отклоняется — подпись
    // обязана проверяться против вшитого публичного ключа издателя.
    [Fact]
    public async Task ScannerCore_RejectsPackageSignedWithWrongKey()
    {
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

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
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var databaseFile = Path.Combine(
            Path.GetDirectoryName(scannerPath)!, "security", "database", "hashes.txt");
        if (!File.Exists(databaseFile))
        {
            Assert.Fail("security/database/hashes.txt не найден рядом со ScannerCore — установите пакет базы.");
        }

        var backup = await File.ReadAllBytesAsync(databaseFile);
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
            await File.WriteAllBytesAsync(databaseFile, backup);
            Directory.Delete(temp, recursive: true);
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
            Assert.DoesNotContain(after, line => line.StartsWith(markerHash, StringComparison.Ordinal));
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
                line => line.StartsWith(outerHash + "\t", StringComparison.Ordinal));

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
        var scannerPath = FindScannerCore();
        var keyPath = FindDatabaseSigningKey();
        if (scannerPath is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

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
        }
    }

    // П. 11 аудита: atomic replacement — НЕУДАВШЕЕСЯ обновление не портит
    // установленную базу: IOC из предыдущего пакета продолжает детектироваться,
    // версия базы не меняется.
    [Fact]
    public async Task ScannerCore_FailedUpdate_KeepsPreviousDatabase()
    {
        var scannerPath = FindScannerCore();
        var keyPath = FindDatabaseSigningKey();
        if (scannerPath is null || keyPath is null)
        {
            ReportSkip();
            return;
        }

        var databaseDirectory = Path.Combine(Path.GetDirectoryName(scannerPath)!, "security", "database");
        var databaseFile = Path.Combine(databaseDirectory, "hashes.txt");
        var versionFile = Path.Combine(databaseDirectory, "db-version.json");
        if (!File.Exists(databaseFile))
        {
            Assert.Fail("security/database/hashes.txt не найден рядом со ScannerCore — установите пакет базы.");
        }

        var backupHashes = await File.ReadAllBytesAsync(databaseFile);
        var backupVersion = File.Exists(versionFile) ? await File.ReadAllBytesAsync(versionFile) : null;
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
            await File.WriteAllBytesAsync(databaseFile, backupHashes);
            if (backupVersion is null)
            {
                File.Delete(versionFile);
            }
            else
            {
                await File.WriteAllBytesAsync(versionFile, backupVersion);
            }
            Directory.Delete(temp, recursive: true);
        }
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

