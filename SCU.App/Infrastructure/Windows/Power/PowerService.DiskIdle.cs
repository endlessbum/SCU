using System.Globalization;
using System.Text.RegularExpressions;
using SCU.Common;

namespace SCU.Infrastructure.Windows.Power;

// Таймаут отключения диска в простое (SUB_DISK/DISKIDLE активной схемы электропитания).
// «Отключить диск» (тумблер вкл) = DISKIDLE 0 — диск не переводится в спящий режим;
// тумблер выкл = 20 минут (1200 с) — стандартное значение Windows из powercfg.cpl.
// Значение задаётся для AC и DC сразу (иначе состояние «смешанное»), читается AC;
// применение — /setactive SCHEME_CURRENT (пересборка активной схемы без её смены).
public sealed partial class PowerService
{
    public const int DiskIdleDisabledSeconds = 0;
    public const int DiskIdleDefaultSeconds = 1200; // 20 минут

    private const string SubDiskGuid = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string DiskIdleGuid = "6738e2c4-e8a5-4a42-b16a-e040e769756e";

    // Только чтение: AC-таймаут диска активной схемы, в секундах. -1 — не распознано.
    public async Task<Result<int>> GetDiskIdleTimeoutAsync(CancellationToken ct = default)
    {
        var query = await _runner
            .RunAsync("powercfg", ["/q", "SCHEME_CURRENT", SubDiskGuid, DiskIdleGuid], null, ct)
            .ConfigureAwait(false);
        if (!query.IsSuccess)
        {
            return Result<int>.Failure(
                "Не удалось прочитать таймаут диска (powercfg): " + query.Message, query.Code);
        }

        var seconds = ParseDiskIdleAcIndex(query.Value ?? string.Empty);
        return seconds >= 0
            ? Result<int>.Success(seconds)
            : Result<int>.Failure("Не удалось распознать значение DISKIDLE в выводе powercfg.");
    }

    // Блок параметра начинается со строки с GUID'ом DISKIDLE; первое hex-значение
    // (0x…) после неё — Current AC Power Setting Index. Подписи строк локализованы,
    // hex-значения — нет, поэтому парсим hex, а не текст («от сети»/«AC»).
    internal static int ParseDiskIdleAcIndex(string output)
    {
        var lines = output.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(DiskIdleGuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var j = i + 1; j < lines.Length; j++)
            {
                var match = Regex.Match(lines[j], @"0x[0-9a-fA-F]{1,8}");
                if (match.Success)
                {
                    return int.Parse(match.Value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
            }

            break;
        }

        return -1;
    }

    public async Task<Result> SetDiskIdleDisabledAsync(bool disable, CancellationToken ct = default)
    {
        var seconds = disable ? DiskIdleDisabledSeconds : DiskIdleDefaultSeconds;
        var secondsText = seconds.ToString(CultureInfo.InvariantCulture);

        var ac = await _runner
            .RunAsync("powercfg", ["/setacvalueindex", "SCHEME_CURRENT", SubDiskGuid, DiskIdleGuid, secondsText], null, ct)
            .ConfigureAwait(false);
        if (!ac.IsSuccess)
        {
            return Result.Failure("Не удалось задать AC-таймаут диска: " + ac.Message, ac.Code);
        }

        var dc = await _runner
            .RunAsync("powercfg", ["/setdcvalueindex", "SCHEME_CURRENT", SubDiskGuid, DiskIdleGuid, secondsText], null, ct)
            .ConfigureAwait(false);
        if (!dc.IsSuccess)
        {
            return Result.Failure("Не удалось задать DC-таймаут диска: " + dc.Message, dc.Code);
        }

        var apply = await _runner
            .RunAsync("powercfg", ["/setactive", "SCHEME_CURRENT"], null, ct)
            .ConfigureAwait(false);
        if (!apply.IsSuccess)
        {
            return Result.Failure("Не удалось применить схему электропитания: " + apply.Message, apply.Code);
        }

        _logger.Info($"POWER | disk idle timeout={seconds}s | applied");

        // Подтверждение чтением: значение обязано совпасть с ожидаемым.
        var readback = await GetDiskIdleTimeoutAsync(ct).ConfigureAwait(false);
        if (!readback.IsSuccess || readback.Value != seconds)
        {
            return Result.Failure(
                "Таймаут задан, но повторное чтение не подтвердило значение: "
                + (readback.IsSuccess ? readback.Value.ToString(CultureInfo.InvariantCulture) : readback.Message));
        }

        return Result.Success(disable
            ? "Отключение диска в режиме простоя запрещено."
            : "Таймаут отключения диска: 20 минут.");
    }
}
