using System.Globalization;
using System.Management;
using Microsoft.Win32;
using SCU.Common;
using SCU.Interop;
using SCU.Models;

namespace SCU.Infrastructure.Windows.Power;

// Ограничения CPU/ОЗУ в BCD, температуры и сжатие памяти (п. 13 аудита: зона ответственности).
public sealed partial class PowerService
{
    // Снятие ограничений numproc/truncatememory через bcdedit.
    // Обязательно: backup (export) → изменение → повторное чтение → verify.
    public async Task<Result> ClearCpuMemoryLimitsAsync(CancellationToken ct = default)
    {
        var status = await _runner
            .RunAsync("bcdedit", ["/enum", "{current}"], null, ct)
            .ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            return Result.Failure("Не удалось прочитать BCD: " + status.Message, status.Code);
        }

        var output = status.Value ?? string.Empty;
        var hasNumproc = PowerParsers.ContainsBcdValue(output, "numproc");
        var hasTruncate = PowerParsers.ContainsBcdValue(output, "truncatememory");

        if (!hasNumproc && !hasTruncate)
        {
            return Result.Success("Ограничения CPU/ОЗУ в BCD уже отсутствуют.");
        }

        // Backup ДО изменения. Без успешного export изменение отменяем.
        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCU", "backup", "bcd");
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var exportPath = Path.Combine(backupDir, $"bcd_export_{stamp}");
        var export = await _runner
            .RunAsync("bcdedit", ["/export", exportPath], null, ct)
            .ConfigureAwait(false);
        if (!export.IsSuccess)
        {
            _logger.Warn("BCD | export failed | " + export.Message);
            return Result.Failure(
                "Не удалось создать резервную копию BCD — изменение отменено: " + export.Message,
                export.Code);
        }

        _logger.Info("BCD | export -> " + exportPath);

        // Сохраняем исходные значения параметров для аудита.
        try
        {
            var metaPath = exportPath + ".meta.json";
            var meta = System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 1,
                createdAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                bootIdentifier = "{current}",
                parameters = new
                {
                    numproc = hasNumproc ? PowerParsers.ExtractBcdValue(output, "numproc") : null,
                    truncatememory = hasTruncate ? PowerParsers.ExtractBcdValue(output, "truncatememory") : null
                },
                exportPath
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, meta);
        }
        catch (Exception ex)
        {
            _logger.Warn("BCD | meta save failed | " + ex.Message);
        }

        if (hasNumproc)
        {
            var delete = await _runner
                .RunAsync("bcdedit", ["/deletevalue", "{current}", "numproc"], null, ct)
                .ConfigureAwait(false);
            _logger.Info("BCD | delete numproc | rc=" + delete.Code);
            if (!delete.IsSuccess)
            {
                return Result.Failure(delete.Message, delete.Code);
            }
        }

        if (hasTruncate)
        {
            var delete = await _runner
                .RunAsync("bcdedit", ["/deletevalue", "{current}", "truncatememory"], null, ct)
                .ConfigureAwait(false);
            _logger.Info("BCD | delete truncatememory | rc=" + delete.Code);
            if (!delete.IsSuccess)
            {
                return Result.Failure(delete.Message, delete.Code);
            }
        }

        var verify = await _runner
            .RunAsync("bcdedit", ["/enum", "{current}"], null, ct)
            .ConfigureAwait(false);
        if (!verify.IsSuccess)
        {
            return Result.Failure("Не удалось повторно проверить BCD: " + verify.Message, verify.Code);
        }

        var verifyOutput = verify.Value ?? string.Empty;
        if (PowerParsers.ContainsBcdValue(verifyOutput, "numproc") || PowerParsers.ContainsBcdValue(verifyOutput, "truncatememory"))
        {
            return Result.Failure("BCD: ограничения не сняты после удаления (проверка чтением не прошла).");
        }

        _logger.Info("BCD | numproc/truncatememory absent | verified");
        return Result.Success("Ограничения CPU/ОЗУ в BCD сняты. Резервная копия создана. Требуется перезагрузка Windows.");
    }


    // Только чтение: компактная сводка ограничений BCD (numproc/truncatememory)
    // и температуры CPU — вместо полного дампа powercfg /query.
    public async Task<Result<string>> GetCpuLimitsSummaryAsync(CancellationToken ct = default)
    {
        var status = await _runner
            .RunAsync("bcdedit", ["/enum", "{current}"], null, ct)
            .ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            return Result<string>.Failure("Не удалось прочитать BCD: " + status.Message, status.Code);
        }

        var output = status.Value ?? string.Empty;
        var numproc = PowerParsers.ExtractBcdValue(output, "numproc");
        var truncate = PowerParsers.ExtractBcdValue(output, "truncatememory");

        var lines = new List<string>
        {
            numproc is null ? "numproc — не задано" : $"numproc — {numproc}",
            truncate is null ? "truncatememory — не задано" : $"truncatememory — {truncate}"
        };

        // Источник — MSAcpi_ThermalZoneTemperature, не датчик CPU Package.
        // Название в UI должно отражать реальный источник.
        var temperature = await GetThermalZoneTemperatureAsync(ct).ConfigureAwait(false);
        var formatted = PowerParsers.FormatThermalZoneTemperature(temperature);
        if (formatted is not null)
        {
            lines.Add("Температура по ACPI Thermal Zone: " + formatted);
        }
        else
        {
            lines.Add("Температура по ACPI Thermal Zone: нет данных");
        }

        return Result<string>.Success(string.Join(Environment.NewLine, lines));
    }



    /// <summary>
    /// Температура ACPI Thermal Zone (не CPU Package).
    /// При отсутствии датчика — «нет данных», не 0 °C.
    /// </summary>
    public async Task<string> GetThermalZoneTemperatureAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var searcher = new ManagementObjectSearcher(
                    "root/wmi",
                    "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                var lines = new List<string>();
                using var results = searcher.Get();
                foreach (var zone in results.OfType<ManagementObject>())
                {
                    var celsius = (zone["CurrentTemperature"] as double? ?? Convert.ToDouble(zone["CurrentTemperature"])) / 10.0 - 273.15;
                    lines.Add($"{zone["InstanceName"]}: {celsius:F1} C");
                    zone.Dispose();
                }

                return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "(датчики температуры не найдены)";
            }, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "(датчик температуры недоступен)";
        }
    }

    /// <summary>Устаревшее имя. Используйте <see cref="GetThermalZoneTemperatureAsync"/>.</summary>
    public Task<string> GetCpuTemperatureAsync(CancellationToken ct = default)
        => GetThermalZoneTemperatureAsync(ct);

    // ===================== Память и файловая система =====================

    public async Task<Result<bool>> GetMemoryCompressionAsync(CancellationToken ct = default)
    {
        var result = await _runner
            .RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NoLogo", "-Command", "(Get-MMAgent).MemoryCompression"],
                null,
                ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            // Деталь (stderr/причина запуска) важна: без неё пользователь видит
            // только «код 1» и не может понять, что именно сломалось.
            return Result<bool>.Failure(
                "Не удалось прочитать Memory Compression (код " + result.Code + "): " + result.Message,
                result.Code);
        }

        var value = (result.Value ?? string.Empty).Trim();
        if (value.Equals("True", StringComparison.OrdinalIgnoreCase))
            return Result<bool>.Success(true);
        if (value.Equals("False", StringComparison.OrdinalIgnoreCase))
            return Result<bool>.Success(false);

        // Нераспознанный вывод — Unknown, не false.
        return Result<bool>.Failure("Не удалось распознать состояние Memory Compression: «" + value + "».");
    }

    public async Task<Result> SetMemoryCompressionAsync(bool enable, CancellationToken ct = default)
    {
        var command = enable ? "Enable-MMAgent -mc" : "Disable-MMAgent -mc";
        var result = await _runner
            .RunAsync("powershell.exe", ["-NoProfile", "-NoLogo", "-Command", command], null, ct)
            .ConfigureAwait(false);
        _logger.Info($"POWER | memory compression={enable} | rc={result.Code}");
        return result.IsSuccess
            ? Result.Success(enable ? "Сжатие памяти включено." : "Сжатие памяти отключено.")
            : Result.Failure("Не удалось переключить сжатие памяти (код " + result.Code + ").", result.Code);
    }
}
