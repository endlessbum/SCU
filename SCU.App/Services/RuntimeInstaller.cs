using System.Diagnostics;
using System.Net.Http;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.Services;

// Установка и определение общих рантаймов: DirectX, VC++ Redistributable, .NET Desktop Runtime.
// Повторяет раздел 2 «Utilities.bat»: скачивание -> VerifyExe -> тихая установка -> коды 0/1638/3010/1641.
public sealed class RuntimeInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    // URL и параметры тихой установки — как в Utilities.bat (:DoDirectX, :DoVC, :DoNET).
    private static readonly IReadOnlyList<RuntimeComponentDefinition> Definitions =
    [
        // DirectX End-User Runtime штатного тихого удаления не имеет — UninstallArgs пуст.
        new RuntimeComponentDefinition(
            "directx",
            "DirectX",
            "Веб-установщик dxwebsetup.",
            "https://download.microsoft.com/download/1/7/1/1718CCC4-6315-4D8E-9543-8E28A4E18C4C/dxwebsetup.exe",
            "dxwebsetup.exe",
            ["/Q"],
            null),
        new RuntimeComponentDefinition(
            "vc-x64",
            "Visual C++ Redistributable x64",
            "VC++ 2015–2022, 64-бит.",
            "https://aka.ms/vc14/vc_redist.x64.exe",
            "vc_redist.x64.exe",
            ["/install", "/quiet", "/norestart"],
            ["/uninstall", "/quiet", "/norestart"],
            ExpectedProductNameContains: "Visual C++"),
        new RuntimeComponentDefinition(
            "vc-x86",
            "Visual C++ Redistributable x86",
            "VC++ 2015–2022, 32-бит.",
            "https://aka.ms/vc14/vc_redist.x86.exe",
            "vc_redist.x86.exe",
            ["/install", "/quiet", "/norestart"],
            ["/uninstall", "/quiet", "/norestart"],
            ExpectedProductNameContains: "Visual C++"),
        // Предыдущие семейства VC++: официальные ссылки Microsoft, оставшиеся живыми
        // (для 2005/2010 прямые ссылки download.microsoft.com выведены из обращения — 404).
        new RuntimeComponentDefinition(
            "vc2013-x64",
            "Visual C++ 2013 x64",
            "VC++ 2013 Update 5 (msvcr120), 64-бит.",
            "https://aka.ms/highdpimfc2013x64enu",
            "vc2013_x64.exe",
            ["/install", "/quiet", "/norestart"],
            ["/uninstall", "/quiet", "/norestart"]),
        new RuntimeComponentDefinition(
            "vc2013-x86",
            "Visual C++ 2013 x86",
            "VC++ 2013 Update 5 (msvcr120), 32-бит.",
            "https://aka.ms/highdpimfc2013x86enu",
            "vc2013_x86.exe",
            ["/install", "/quiet", "/norestart"],
            ["/uninstall", "/quiet", "/norestart"]),
        new RuntimeComponentDefinition(
            "vc2012-x64",
            "Visual C++ 2012 x64",
            "VC++ 2012 Update 4 (msvcr110), 64-бит.",
            "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe",
            "vc2012_x64.exe",
            ["/install", "/quiet", "/norestart"],
            ["/uninstall", "/quiet", "/norestart"]),
        new RuntimeComponentDefinition(
            "vc2012-x86",
            "Visual C++ 2012 x86",
            "VC++ 2012 Update 4 (msvcr110), 32-бит.",
            "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe",
            "vc2012_x86.exe",
            ["/install", "/quiet", "/norestart"],
            ["/uninstall", "/quiet", "/norestart"]),
        // 2008 SP1 — актуальный дистрибутив из security-update KB2538242 (msvcr90).
        // Тихое удаление bootstrapper 2008 года не поддерживает — CanUninstall = false.
        new RuntimeComponentDefinition(
            "vc2008-x64",
            "Visual C++ 2008 SP1 x64",
            "VC++ 2008 SP1 (msvcr90), 64-бит.",
            "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe",
            "vc2008sp1_x64.exe",
            ["/Q"],
            null),
        new RuntimeComponentDefinition(
            "vc2008-x86",
            "Visual C++ 2008 SP1 x86",
            "VC++ 2008 SP1 (msvcr90), 32-бит.",
            "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe",
            "vc2008sp1_x86.exe",
            ["/Q"],
            null),
        // .NET Framework 4.x: 4.8.1 — оффлайн-установщик последней ветки 4.x.
        // На Win10/11 обычно уже установлен — DetectInstalled вернёт true и кнопка скроется.
        new RuntimeComponentDefinition(
            "netfx481",
            ".NET Framework 4.8.1 (4.x)",
            "Ветка 4.x: оффлайн-установщик 4.8.1.",
            "https://go.microsoft.com/fwlink/?LinkId=2203307",
            "netfx481.exe",
            ["/q", "/norestart"],
            null),
        // .NET Framework 3.5 — компонент Windows: включается DISM'ом (пакет
        // скачивается из Windows Update), веб-установщика у него нет.
        new RuntimeComponentDefinition(
            "netfx35",
            ".NET Framework 3.5",
            "Включение компонента Windows через DISM.",
            "",
            "",
            [],
            null),
        new RuntimeComponentDefinition(
            "dotnet-10",
            ".NET 10 Desktop Runtime x64",
            "Ветка .NET 10 (LTS).",
            "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe",
            "windowsdesktop-runtime-10-x64.exe",
            ["/install", "/quiet", "/norestart"],
            ["/uninstall", "/quiet", "/norestart"]),
        new RuntimeComponentDefinition(
            "dotnet-9",
            ".NET 9 Desktop Runtime x64",
            "Ветка .NET 9 (STS).",
            "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe",
            "windowsdesktop-runtime-9-x64.exe",
            ["/install", "/quiet", "/norestart"],
            ["/uninstall", "/quiet", "/norestart"]),
        new RuntimeComponentDefinition(
            "dotnet-8",
            ".NET 8 Desktop Runtime x64",
            "Ветка .NET 8 (LTS).",
            "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe",
            "windowsdesktop-runtime-8-x64.exe",
            ["/install", "/quiet", "/norestart"],
            ["/uninstall", "/quiet", "/norestart"])
    ];

    private readonly Logger _logger;
    private readonly SCURunner _runner;

    public RuntimeInstaller(Logger logger, SCURunner runner)
    {
        _logger = logger;
        _runner = runner;
    }

    public IReadOnlyList<ComponentItem> BuildComponentList()
    {
        var items = new List<ComponentItem>(Definitions.Count);
        foreach (var definition in Definitions)
        {
            items.Add(new ComponentItem
            {
                Id = definition.Id,
                Name = definition.Name,
                Description = definition.Description,
                IsInstalled = DetectInstalled(definition),
                CanUninstall = definition.UninstallArgs is not null
            });
        }

        return items;
    }

    // Тихое удаление через тот же официальный установщик с флагом /uninstall.
    // Установщик скачивается и проверяется заново: локальной копии на диске нет.
    public async Task<Result> UninstallAsync(
        ComponentItem component,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        var definition = Definitions.FirstOrDefault(d => d.Id == component.Id);
        if (definition is null)
        {
            return Result.Failure($"Неизвестный компонент: {component.Id}", 2);
        }

        if (definition.UninstallArgs is null)
        {
            return Result.Failure($"Для «{definition.Name}» тихое удаление не поддерживается.", 2);
        }

        if (!component.IsInstalled)
        {
            return Result.Success("Компонент не установлен.");
        }

        return await RunVerifiedInstallerAsync(definition, component, progress, uninstall: true, ct).ConfigureAwait(false);
    }

    public async Task<Result> InstallAsync(
        ComponentItem component,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        var definition = Definitions.FirstOrDefault(d => d.Id == component.Id);
        if (definition is null)
        {
            return Result.Failure($"Неизвестный компонент: {component.Id}", 2);
        }

        if (component.IsInstalled)
        {
            return Result.Success("Компонент уже установлен.");
        }

        // .NET Framework 3.5 устанавливается включением компонента Windows,
        // а не скачиванием установщика.
        if (component.Id == "netfx35")
        {
            return await EnableNetFx35Async(component, progress, ct).ConfigureAwait(false);
        }

        return await RunVerifiedInstallerAsync(definition, component, progress, uninstall: false, ct).ConfigureAwait(false);
    }

    // Включение компонента «.NET Framework 3.5 (включая .NET 2.0 и 3.0)»:
    // dism /online /enable-feature NetFx3 /all — пакет DISM догружает из
    // Windows Update. Коды 0/3010/1641 — по той же логике, что у установщиков.
    private async Task<Result> EnableNetFx35Async(
        ComponentItem component,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dism.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("/online");
        process.StartInfo.ArgumentList.Add("/enable-feature");
        process.StartInfo.ArgumentList.Add("/featurename:NetFx3");
        process.StartInfo.ArgumentList.Add("/all");
        process.StartInfo.ArgumentList.Add("/norestart");

        progress?.Report("DISM | включение .NET Framework 3.5…");
        _logger.Info("RUNTIME | install start | netfx35 (DISM)");

        try
        {
            if (!process.Start())
            {
                return Result.Failure("Не удалось запустить DISM.", 1);
            }
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось запустить DISM: " + exception.Message, 1);
        }

        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.Warn("CANCEL | install netfx35 | kill process tree");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception)
            {
                _logger.Error("CANCEL | kill failed | " + exception.Message);
            }
        });

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("CANCEL | install netfx35");
            return Result.Failure("Отменено", -1);
        }

        var code = process.ExitCode;
        _logger.Info($"RUNTIME | install netfx35 | rc={code}");

        switch (code)
        {
            case 0:
                component.IsInstalled = true;
                return Result.Success(".NET Framework 3.5 включён.");
            case 3010:
            case 1641:
                component.IsInstalled = true;
                return Result.Success(".NET Framework 3.5 включён, требуется перезагрузка.");
            default:
                return Result.Failure($".NET Framework 3.5: DISM завершился с кодом {code}", code);
        }
    }

    // Общий путь установки и удаления: защищённый каталог → скачивание →
    // проверка подписи Microsoft → тихий запуск (аргументы зависят от режима).
    private async Task<Result> RunVerifiedInstallerAsync(
        RuntimeComponentDefinition definition,
        ComponentItem component,
        IProgress<string>? progress,
        bool uninstall,
        CancellationToken ct)
    {
        // Скачивание идёт в защищённый каталог %ProgramData%\SCU\downloads.
        // Если строгий DACL не удалось установить/проверить, установка прекращается:
        // fail-open здесь недопустим, иначе окно TOCTOU остаётся доступным обычному пользователю.
        string installerDirectory;
        try
        {
            installerDirectory = GetDownloadsDirectory();
        }
        catch (Exception exception)
        {
            _logger.Error("RUNTIME | secure download directory failed | " + exception);
            return Result.Failure("Не удалось подготовить защищённый каталог загрузок: " + exception.Message, 1);
        }

        // Имя уникально на попытку: антивирус или незавершённый прошлый запуск
        // не должны блокировать новый файл с тем же именем.
        var installerPath = Path.Combine(
            installerDirectory,
            $"{Path.GetFileNameWithoutExtension(definition.FileName)}_{Guid.NewGuid():N}{Path.GetExtension(definition.FileName)}");

        try
        {
            var downloadResult = await DownloadAsync(definition, installerPath, progress, ct).ConfigureAwait(false);
            if (!downloadResult.IsSuccess)
            {
                return downloadResult;
            }

            var verifyResult = await VerifySignatureAsync(definition, installerPath, progress, ct).ConfigureAwait(false);
            if (!verifyResult.IsSuccess)
            {
                return verifyResult;
            }

            return await RunInstallerAsync(definition, component, installerPath, progress, uninstall, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(installerPath);
        }
    }

    private static async Task<Result> DownloadAsync(
        RuntimeComponentDefinition definition,
        string targetPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        TryDelete(targetPath);

        progress?.Report($"GET | {definition.DownloadUrl}");

        try
        {
            using var response = await Http
                .GetAsync(definition.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long written = 0;
            long nextReport = 4 * 1024 * 1024;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                if (total > 0 && written >= nextReport)
                {
                    nextReport += 4 * 1024 * 1024;
                    progress?.Report($"GET | {definition.FileName} | {written / (1024 * 1024)} / {total / (1024 * 1024)} MB");
                }
            }

            if (written < 262144)
            {
                return Result.Failure($"{definition.FileName}: файл подозрительно мал ({written} bytes)", 1);
            }

            return Result.Success($"Скачано {written} bytes.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result.Failure($"Скачивание {definition.FileName} не удалось: {exception.Message}", 1);
        }
    }

    // Проверка размера и подписи Microsoft — нативный .NET (PS VerifyExe сбоил в части сессий, код 6).
    private Task<Result> VerifySignatureAsync(
        RuntimeComponentDefinition definition,
        string installerPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report("Проверка подписи " + Path.GetFileName(installerPath) + "…");
        var result = SignatureVerifier.VerifyMicrosoftSigned(installerPath);
        if (!result.IsSuccess)
        {
            _logger.Error($"RUNTIME | signature verify failed | {result.Message}");
            return Task.FromResult(Result.Failure("Проверка подписи не пройдена: " + result.Message, result.Code));
        }

        var definitionCheck = VerifyDefinition(definition, installerPath);
        if (!definitionCheck.IsSuccess)
        {
            _logger.Error($"RUNTIME | definition verify failed | {definitionCheck.Message}");
            return Task.FromResult(definitionCheck);
        }

        _logger.Info("RUNTIME | signature verify | подпись Microsoft подтверждена");
        return Task.FromResult(Result.Success("Подпись подтверждена."));
    }

    // П. 19 аудита: артефакт должен соответствовать определению — ожидаемое
    // имя файла, имя продукта и (если задан) allowlist SHA-256. Fail-closed.
    private Result VerifyDefinition(RuntimeComponentDefinition definition, string installerPath)
    {
        var name = Path.GetFileName(installerPath);
        var expectedBase = Path.GetFileNameWithoutExtension(definition.FileName);
        var expectedExt = Path.GetExtension(definition.FileName);
        if (!name.StartsWith(expectedBase + "_", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure($"Имя установщика не соответствует ожидаемому ({definition.FileName}): {name}", 1);
        }

        if (!string.IsNullOrEmpty(definition.ExpectedProductNameContains))
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(installerPath);
            var productName = info.ProductName ?? string.Empty;
            if (!productName.Contains(definition.ExpectedProductNameContains, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure(
                    $"Продукт установщика не соответствует ожидаемому «{definition.ExpectedProductNameContains}»: «{productName}»", 1);
            }
        }

        if (definition.Sha256Allowlist is { Length: > 0 })
        {
            using var stream = File.OpenRead(installerPath);
            var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            if (!definition.Sha256Allowlist.Contains(sha256, StringComparer.OrdinalIgnoreCase))
            {
                return Result.Failure($"SHA-256 установщика отсутствует в allowlist: {sha256}", 1);
            }
        }

        return Result.Success("Артефакт соответствует определению.");
    }

    private async Task<Result> RunInstallerAsync(
        RuntimeComponentDefinition definition,
        ComponentItem component,
        string installerPath,
        IProgress<string>? progress,
        bool uninstall,
        CancellationToken ct)
    {
        // Повторная проверка непосредственно перед запуском и удержание read-handle
        // до Process.Start закрывают практическое окно подмены/удаления после проверки.
        var recheck = SignatureVerifier.VerifyMicrosoftSigned(installerPath);
        if (!recheck.IsSuccess)
        {
            _logger.Error($"RUNTIME | pre-start verify failed | {recheck.Message}");
            return Result.Failure("Проверка подписи перед запуском не пройдена: " + recheck.Message, recheck.Code);
        }

        var definitionRecheck = VerifyDefinition(definition, installerPath);
        if (!definitionRecheck.IsSuccess)
        {
            _logger.Error($"RUNTIME | pre-start definition verify failed | {definitionRecheck.Message}");
            return definitionRecheck;
        }

        if ((File.GetAttributes(installerPath) & FileAttributes.ReparsePoint) != 0)
        {
            return Result.Failure("Установщик находится по reparse-point/символьной ссылке — запуск заблокирован.", 1);
        }

        ct.ThrowIfCancellationRequested();
        using var launchLock = new FileStream(
            installerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.SequentialScan);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in uninstall
                     ? definition.UninstallArgs ?? []
                     : definition.SilentArgs)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        progress?.Report(uninstall ? $"UNINSTALL | {definition.Name}" : $"INSTALL | {definition.Name}");
        _logger.Info($"RUNTIME | {(uninstall ? "uninstall" : "install")} start | {definition.Id}");

        try
        {
            if (!process.Start())
            {
                return Result.Failure("Не удалось запустить установщик.", 1);
            }
        }
        catch (Exception exception)
        {
            return Result.Failure("Не удалось запустить установщик: " + exception.Message, 1);
        }

        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.Warn($"CANCEL | install {definition.Id} | kill process tree");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception)
            {
                _logger.Error("CANCEL | kill failed | " + exception.Message);
            }
        });

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"CANCEL | install {definition.Id}");
            return Result.Failure("Отменено", -1);
        }

        var code = process.ExitCode;
        _logger.Info($"RUNTIME | {(uninstall ? "uninstall" : "install")} {definition.Id} | rc={code}");

        if (uninstall)
        {
            switch (code)
            {
                case 0:
                case 3010:
                case 1641:
                    component.IsInstalled = false;
                    return Result.Success($"{definition.Name} удалён.");
                default:
                    return Result.Failure($"{definition.Name}: удаление завершилось с кодом {code}", code);
            }
        }

        switch (code)
        {
            case 0:
                component.IsInstalled = true;
                return Result.Success($"{definition.Name} установлен.");
            case 1638:
                component.IsInstalled = true;
                return Result.Success($"{definition.Name} уже установлен.");
            case 3010:
            case 1641:
                component.IsInstalled = true;
                return Result.Success($"{definition.Name} установлен, требуется перезагрузка.");
            default:
                return Result.Failure($"{definition.Name}: установщик завершился с кодом {code}", code);
        }
    }

    // Каталог скачивания с ACL «администраторы и SYSTEM — полный доступ».
    // Наследование от ProgramData отключается: иначе тот же пользователь без
    // повышения мог бы подменить установщик между проверкой подписи и запуском.
    private static string GetDownloadsDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SCU",
            "downloads");
        Directory.CreateDirectory(directory);
        EnsureSecureDownloadsDirectory(directory);
        return directory;
    }

    private static void EnsureSecureDownloadsDirectory(string directory)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var allowed = new HashSet<SecurityIdentifier> { administrators, system };

        var rules = new DirectorySecurity();
        rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        rules.SetOwner(administrators);
        rules.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        rules.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        var info = new DirectoryInfo(directory);
        info.SetAccessControl(rules);

        // Fail closed: наследование должно быть выключено, а каждый ACE — только для
        // Administrators/SYSTEM. Лишний Allow для Users/Everyone оставляет TOCTOU.
        var actual = info.GetAccessControl(AccessControlSections.Access);
        if (!actual.AreAccessRulesProtected)
        {
            throw new SecurityException("Не удалось отключить наследование DACL.");
        }

        var actualRules = actual.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var seen = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in actualRules)
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!allowed.Contains(sid))
            {
                throw new SecurityException($"Обнаружен неразрешённый ACL для SID {sid.Value}.");
            }

            if (rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights != FileSystemRights.FullControl)
            {
                throw new SecurityException($"Обнаружен слишком узкий/запрещающий ACL для SID {sid.Value}; ожидается FullControl Allow.");
            }

            seen.Add(sid);
        }

        if (!seen.SetEquals(allowed))
        {
            throw new SecurityException("Защищённый каталог не содержит полного доступа Administrators и SYSTEM.");
        }
    }

    private static bool DetectInstalled(RuntimeComponentDefinition definition) =>
        RuntimeDetector.IsInstalled(definition.Id);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Оставшийся временный файл не критичен.
        }
    }

    private sealed record RuntimeComponentDefinition(
        string Id,
        string Name,
        string Description,
        string DownloadUrl,
        string FileName,
        string[] SilentArgs,
        string[]? UninstallArgs,
        // П. 19 аудита: усиленная проверка артефакта. Издатель уже фиксирует
        // VerifyMicrosoftSigned (Microsoft Corporation); здесь — ожидаемое
        // имя продукта и опциональный allowlist SHA-256. Проверки fail-closed.
        string? ExpectedProductNameContains = null,
        string[]? Sha256Allowlist = null);
}
