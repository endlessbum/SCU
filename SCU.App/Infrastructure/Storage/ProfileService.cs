using SCU.Common;
using SCU.Models;

namespace SCU.Infrastructure.Storage;

// Единый каталог декларативных шагов профилей и движок их проверки/применения.
// Ключевой принцип: каждый шаг — вызов СУЩЕСТВУЮЩЕГО сервиса (питание, сеть, ввод,
// приватность). Своей системной логики у профилей нет — они лишь описывают цель,
// поэтому «применить профиль» и «откатить вручную» в разделах дают один результат.
public sealed class ProfileService
{
    // Идентификаторы профилей. Хранятся в profile.json как строки — константы
    // менять нельзя: старые файлы перестанут читаться.
    public const string ProfileNone = "none";
    public const string ProfileGaming = "gaming";
    public const string ProfileBalanced = "balanced";
    public const string ProfilePrivacy = "privacy";
    public const string ProfileCustom = "custom";

    // Id шагов. Используются как ключи словаря настраиваемого профиля и в истории.
    public const string StepPowerUltimate = "power.ultimate";
    public const string StepPowerBalanced = "power.balanced";
    public const string StepNetworkGaming = "network.gaming";
    public const string StepGameBar = "games.gamebar";
    public const string StepGameDvr = "games.gamedvr";
    public const string StepPrivacyTelemetry = "privacy.telemetry";
    public const string StepPrivacyNotifications = "privacy.notifications";
    public const string StepPrivacyUwp = "privacy.uwp";
    public const string StepPrivacyCopilot = "privacy.copilot";
    public const string StepPrivacyDelivery = "privacy.do";
    public const string StepPrivacyCeip = "privacy.ceip";

    private const string CacheActivePlan = "power.active";
    private const string CacheTcpGlobal = "network.tcp";

    private readonly Logger _logger;
    private readonly PowerService _power;
    private readonly NetworkService _network;
    private readonly InputService _input;
    private readonly PrivacyService _privacy;

    public ProfileService(
        Logger logger,
        PowerService power,
        NetworkService network,
        InputService input,
        PrivacyService privacy)
    {
        _logger = logger;
        _power = power;
        _network = network;
        _input = input;
        _privacy = privacy;
        Catalog = BuildCatalog();
    }

    public IReadOnlyList<ProfileStep> Catalog { get; }

    // Декларативные профили: stepId → желаемое состояние. Gaming — максимум
    // производительности и минимум фоновых игровых сервисов; Balanced — заводское
    // поведение; Privacy — все категории приватности применены.
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, StepDesire>> BuiltIn { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, StepDesire>>(StringComparer.Ordinal)
        {
            [ProfileGaming] = new Dictionary<string, StepDesire>(StringComparer.Ordinal)
            {
                [StepPowerUltimate] = StepDesire.On,
                [StepNetworkGaming] = StepDesire.On,
                [StepGameBar] = StepDesire.Off,
                [StepGameDvr] = StepDesire.Off
            },
            [ProfileBalanced] = new Dictionary<string, StepDesire>(StringComparer.Ordinal)
            {
                [StepPowerBalanced] = StepDesire.On,
                [StepNetworkGaming] = StepDesire.Off
            },
            [ProfilePrivacy] = new Dictionary<string, StepDesire>(StringComparer.Ordinal)
            {
                [StepPrivacyTelemetry] = StepDesire.On,
                [StepPrivacyNotifications] = StepDesire.On,
                [StepPrivacyUwp] = StepDesire.On,
                [StepPrivacyCopilot] = StepDesire.On,
                [StepPrivacyDelivery] = StepDesire.On,
                [StepPrivacyCeip] = StepDesire.On
            }
        };

    // Целевой словарь профиля. null — «не следить ни за каким профилем»
    // (пользователь выбрал «— нет —»). Для custom словарь — пользовательский.
    public static IReadOnlyDictionary<string, StepDesire>? GetTarget(
        string? profileId,
        IReadOnlyDictionary<string, StepDesire>? customSteps) => profileId switch
    {
        ProfileGaming or ProfileBalanced or ProfilePrivacy => BuiltIn[profileId],
        ProfileCustom => customSteps ?? new Dictionary<string, StepDesire>(),
        _ => null
    };

    // Чтение состояния всех шагов каталога. Кэш на проход: netsh/powercfg запускаются
    // один раз, а не по разу на шаг (см. ReadCachedAsync).
    public Task<IReadOnlyDictionary<string, bool?>> CheckStepsAsync(CancellationToken ct = default) =>
        CheckStepsAsync(Catalog, ct);

    public async Task<IReadOnlyDictionary<string, bool?>> CheckStepsAsync(
        IReadOnlyList<ProfileStep> steps,
        CancellationToken ct = default)
    {
        var cache = new Dictionary<string, object?>(StringComparer.Ordinal);
        var states = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            states[step.Id] = await CheckStepCoreAsync(step, cache, ct).ConfigureAwait(false);
        }

        return states;
    }

    // Проверка одного шага «с нуля» (без общего кэша) — для точечных случаев.
    public Task<bool?> CheckStepAsync(ProfileStep step, CancellationToken ct = default) =>
        CheckStepCoreAsync(step, new Dictionary<string, object?>(StringComparer.Ordinal), ct);

    // Применение шага через соответствующий сервис. Ошибка шага не бросается наружу:
    // вызывающий (VM) собирает результаты и продолжает остальные шаги.
    public async Task<Result> ApplyStepAsync(ProfileStep step, StepDesire desire, CancellationToken ct = default)
    {
        var on = desire == StepDesire.On;
        Result result;
        try
        {
            switch (step.Kind)
            {
                case ProfileTargetKind.PowerPlan:
                    // Для схемы питания осмысленно только «включить»: сам выбор другой
                    // схемы является откатом, отдельного «выключить схему» не существует.
                    result = on
                        ? await _power.SetPowerPlanAsync(step.ReferenceId!, ct).ConfigureAwait(false)
                        : Result.Failure("Для схемы электропитания поддерживается только желание «включить».");
                    break;

                case ProfileTargetKind.NetworkGaming:
                    result = on
                        ? await _network.ApplyGamingProfileAsync(ct).ConfigureAwait(false)
                        : await RestoreNetworkDefaultsAsync(ct).ConfigureAwait(false);
                    break;

                case ProfileTargetKind.GameSwitch:
                    var option = step.ReferenceId is null ? null : _input.Find(step.ReferenceId);
                    result = option is null
                        ? Result.Failure("Неизвестный переключатель: " + step.ReferenceId)
                        : _input.SetSwitch(option, on);
                    break;

                case ProfileTargetKind.PrivacyCategory:
                    result = step.ReferenceId is null
                        ? Result.Failure("У шага не задана категория приватности.")
                        : on
                            ? await _privacy.DisableAsync(step.ReferenceId, ct).ConfigureAwait(false)
                            : await _privacy.EnableAsync(step.ReferenceId, ct).ConfigureAwait(false);
                    break;

                default:
                    result = Result.Failure("Неизвестный тип шага: " + step.Kind);
                    break;
            }
        }
        catch (Exception exception)
        {
            // Сервисы возвращают Result, но исключение (WMI, права, IO) не должно
            // прерывать остальные шаги профиля — превращаем его в неуспешный шаг.
            result = Result.Failure(exception.Message);
        }

        _logger.Info($"PROFILE | step | {step.Id} | {(on ? "on" : "off")} | {(result.IsSuccess ? "ok" : "fail")}");
        return result;
    }

    // «Выключить» игровой профиль сети = вернуть TCP Global и QoS из бэкапа.
    private async Task<Result> RestoreNetworkDefaultsAsync(CancellationToken ct)
    {
        var tcp = await _network.RestoreTcpGlobalAsync(ct).ConfigureAwait(false);
        var qos = _network.RestoreQosOverride();
        if (tcp.IsSuccess && qos.IsSuccess)
        {
            return Result.Success("TCP Global и QoS возвращены из бэкапа.");
        }

        var failures = new[] { tcp, qos }
            .Where(result => !result.IsSuccess)
            .Select(result => result.Message);
        return Result.Failure("Откат сети выполнен не полностью: " + string.Join(" ", failures));
    }

    private async Task<bool?> CheckStepCoreAsync(
        ProfileStep step,
        Dictionary<string, object?> cache,
        CancellationToken ct)
    {
        try
        {
            switch (step.Kind)
            {
                case ProfileTargetKind.PowerPlan:
                    var plan = await ReadCachedAsync(cache, CacheActivePlan, () => _power.GetActivePlanGuidAsync(ct))
                        .ConfigureAwait(false);
                    if (!plan.IsSuccess || plan.Value is not { Length: > 0 } active)
                    {
                        return null;
                    }

                    return step.Id == StepPowerUltimate
                        ? IsGuid(active, PowerService.UltimatePerformancePlanGuid)
                            || IsGuid(active, PowerService.UltimatePerformanceCopyGuid)
                        : IsGuid(active, PowerService.BalancedPlanGuid);

                case ProfileTargetKind.NetworkGaming:
                    var tcp = await ReadCachedAsync(cache, CacheTcpGlobal, () => _network.GetTcpGlobalAsync(ct))
                        .ConfigureAwait(false);
                    if (!tcp.IsSuccess || tcp.Value is null)
                    {
                        return null;
                    }

                    // QoS override читается из реестра — быстро, кэш не нужен.
                    return string.Equals(tcp.Value.AutoTuning, "disabled", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(tcp.Value.Ecn, "disabled", StringComparison.OrdinalIgnoreCase)
                        && _network.GetQosOverride() == 0;

                case ProfileTargetKind.GameSwitch:
                    var option = step.ReferenceId is null ? null : _input.Find(step.ReferenceId);
                    return option is null ? null : _input.IsSwitchOn(option);

                case ProfileTargetKind.PrivacyCategory:
                    return step.ReferenceId is null ? null : _privacy.IsCategoryApplied(step.ReferenceId);

                default:
                    return null;
            }
        }
        catch (Exception exception)
        {
            // Честное «не удалось прочитать»: не выдумываем ни true, ни false.
            _logger.Warn($"PROFILE | check failed | {step.Id} | {exception.Message}");
            return null;
        }
    }

    private static async Task<Result<T>> ReadCachedAsync<T>(
        Dictionary<string, object?> cache,
        string key,
        Func<Task<Result<T>>> read)
    {
        if (cache.TryGetValue(key, out var cached) && cached is Result<T> result)
        {
            return result;
        }

        var value = await read().ConfigureAwait(false);
        cache[key] = value;
        return value;
    }

    private static bool IsGuid(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProfileStep> BuildCatalog() =>
    [
        new ProfileStep(
            StepPowerUltimate,
            "Питание",
            "Максимальная производительность (питание)",
            ProfileTargetKind.PowerPlan,
            PowerService.UltimatePerformancePlanGuid),
        new ProfileStep(
            StepPowerBalanced,
            "Питание",
            "Сбалансированная схема питания",
            ProfileTargetKind.PowerPlan,
            PowerService.BalancedPlanGuid),
        new ProfileStep(
            StepNetworkGaming,
            "Сеть",
            "Игровой профиль сети",
            ProfileTargetKind.NetworkGaming,
            null),
        new ProfileStep(
            StepGameBar,
            "Игры",
            "Game Bar",
            ProfileTargetKind.GameSwitch,
            "game-bar"),
        new ProfileStep(
            StepGameDvr,
            "Игры",
            "Фоновая запись игр (Game DVR)",
            ProfileTargetKind.GameSwitch,
            "game-dvr"),
        // Категории приватности: цель «применена» = параметры отключены/задачи выключены.
        .. PrivacyService.Categories.Select(category => new ProfileStep(
            "privacy." + category.Id,
            "Приватность",
            category.Title,
            ProfileTargetKind.PrivacyCategory,
            category.Id))
    ];
}
