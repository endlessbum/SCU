using SCU.Common;
using SCU.Models;

namespace SCU.Infrastructure.Windows.Troubleshooting;

/// <summary>
/// Правила: наблюдаемый факт + контекст + порог = находка (п. 32 плана).
/// Правила не изменяют систему и не исполняют исправления — только описывают их.
/// Каждая проверка — один логический блок «если X + Y → finding».
/// </summary>
public static class DiagnosticRules
{
    private const ulong LowSpaceWarningBytes = 10UL << 30;
    private const ulong LowSpaceCriticalBytes = 5UL << 30;
    private const int AppCrashThreshold = 3;
    private const int ServiceFailureThreshold = 3;
    private const int UpdateErrorThreshold = 3;

    public static List<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var findings = new List<DiagnosticFinding>();
        findings.AddRange(EvaluateDisk(context));
        findings.AddRange(EvaluateUpdate(context));
        findings.AddRange(EvaluateNetwork(context));
        findings.AddRange(EvaluateServices(context));
        findings.AddRange(EvaluateStartup(context));
        findings.AddRange(EvaluateSecurity(context));
        findings.AddRange(EvaluateEvents(context));
        findings.AddRange(EvaluateSystem(context));
        findings.AddRange(EvaluateDrivers(context));
        findings.AddRange(EvaluateStorage(context));
        findings.AddRange(EvaluateAudio(context));
        return findings;
    }

    // ===================== Диск =====================

    private static IEnumerable<DiagnosticFinding> EvaluateDisk(DiagnosticContext context)
    {
        foreach (var disk in context.Disks.Where(disk => disk.IsSystem))
        {
            if (disk.FreeBytes < LowSpaceCriticalBytes && disk.TotalBytes > 0)
            {
                yield return LowSpaceFinding(disk, DiagnosticSeverity.Critical);
            }
            else if (disk.FreeBytes < LowSpaceWarningBytes && disk.TotalBytes > 0)
            {
                yield return LowSpaceFinding(disk, DiagnosticSeverity.Warning);
            }
        }

        // Ошибки дисковой подсистемы в журнале (Disk/Ntfs/volmgr за 7 дней).
        var diskErrors = context.EventErrors
            .Where(error => IsProviderIn(error, "Disk", "Ntfs", "volmgr"))
            .ToList();
        if (diskErrors.Count > 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "disk.fs_errors",
                Category = DiagnosticCategory.Disk,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Ошибки файловой системы в журнале событий"),
                Description = L.T("За последние 7 дней журнал записал ошибки поставщиков Disk/Ntfs. Возможны проблемы с файловой системой или диском."),
                WhyItMatters = L.T("Ошибки файловой системы могут приводить к сбоям чтения, зависаниям и потере данных."),
                ExpectedState = L.T("Журнал не содержит ошибок Disk/Ntfs"),
                ActualState = L.T("Ошибок: {0} типов", diskErrors.Count),
                Evidence = diskErrors.Select(FormatEventEvidence).ToList(),
                Actions =
                [
                    OpenSection(L.T("Открыть очистку"), 3),
                    OpenSection(L.T("Открыть «Поиск и целостность»"), 12)
                ]
            };
        }
    }

    private static DiagnosticFinding LowSpaceFinding(DiagnosticDiskSnapshot disk, DiagnosticSeverity severity)
    {
        var freeGb = disk.FreeBytes / 1024.0 / 1024 / 1024;
        return new DiagnosticFinding
        {
            Id = "disk.low_space",
            Category = DiagnosticCategory.Disk,
            Severity = severity,
            Title = L.T("Мало свободного места на системном диске"),
            Description = L.T("На {0} свободно всего {1:0.0} ГБ.", disk.Letter, freeGb),
            WhyItMatters = L.T("Недостаток места мешает обновлениям, временным файлам и нормальной работе Windows."),
            ExpectedState = L.T("Свободно не менее 10 ГБ"),
            ActualState = L.T("Свободно {0}", FormatBytes(disk.FreeBytes)),
            Evidence =
            [
                new DiagnosticEvidence(L.T("Свободно"), FormatBytes(disk.FreeBytes)),
                new DiagnosticEvidence(L.T("Всего"), FormatBytes(disk.TotalBytes)),
                new DiagnosticEvidence(L.T("Диск"), disk.Letter)
            ],
            Actions = [OpenSection(L.T("Открыть очистку"), 3)]
        };
    }

    // ===================== Windows Update =====================

    private static IEnumerable<DiagnosticFinding> EvaluateUpdate(DiagnosticContext context)
    {
        var wuauserv = context.Services.FirstOrDefault(service =>
            service.Name.Equals("wuauserv", StringComparison.OrdinalIgnoreCase));
        if (wuauserv is not null && !wuauserv.IsRunning)
        {
            if (context.UpdateBlocked == true)
            {
                // Обновления отключены (обычно сознательно в разделе 16) — не ошибка.
                yield return new DiagnosticFinding
                {
                    Id = "update.blocked",
                    Category = DiagnosticCategory.WindowsUpdate,
                    Severity = DiagnosticSeverity.Info,
                    Title = L.T("Обновления Windows отключены"),
                    Description = L.T("Служба Windows Update переведена в состояние «Отключена» — система не будет устанавливать обновления."),
                    WhyItMatters = L.T("Отключённые обновления — частая осознанная настройка. Включайте, только если нужен штатный Update."),
                    ExpectedState = L.T("Служба wuauserv разрешена к запуску"),
                    ActualState = L.T("Служба wuauserv отключена"),
                    Evidence = [new DiagnosticEvidence("wuauserv", L.T("Отключена"))],
                    Actions = [OpenSection(L.T("Открыть «Обновления Windows»"), 16)]
                };
            }
            else
            {
                yield return new DiagnosticFinding
                {
                    Id = "update.service_stopped",
                    Category = DiagnosticCategory.WindowsUpdate,
                    Severity = DiagnosticSeverity.Warning,
                    Title = L.T("Служба Windows Update не работает"),
                    Description = L.T("Служба wuauserv остановлена, но не отключена — установка обновлений не выполняется."),
                    WhyItMatters = L.T("Без этой службы Windows не получает обновления безопасности."),
                    ExpectedState = L.T("Служба wuauserv работает"),
                    ActualState = L.T("Служба wuauserv остановлена"),
                    Evidence = [new DiagnosticEvidence("wuauserv", L.T("Остановлена"))],
                    Actions =
                    [
                        StartServiceAction("wuauserv"),
                        OpenSection(L.T("Открыть «Обновления Windows»"), 16)
                    ]
                };
            }
        }

        if (context.UpdatePaused == true)
        {
            yield return new DiagnosticFinding
            {
                Id = "update.paused",
                Category = DiagnosticCategory.WindowsUpdate,
                Severity = DiagnosticSeverity.Info,
                Title = L.T("Обновления Windows приостановлены"),
                Description = string.IsNullOrWhiteSpace(context.UpdatePauseInfo)
                    ? L.T("Пауза обновлений активна.")
                    : L.S(context.UpdatePauseInfo),
                WhyItMatters = L.T("Пауза обновлений — частая осознанная настройка; это состояние, а не ошибка."),
                Evidence = [new DiagnosticEvidence("Windows Update", L.T("Пауза активна"))],
                Actions = [OpenSection(L.T("Открыть «Обновления Windows»"), 16)]
            };
        }

        // Повторяющиеся ошибки установки обновлений в журнале.
        var updateErrors = context.EventErrors
            .Where(error => error.Provider.Equals("Microsoft-Windows-WindowsUpdateClient", StringComparison.OrdinalIgnoreCase))
            .Sum(error => (int?)error.Count);
        if (updateErrors >= UpdateErrorThreshold)
        {
            yield return new DiagnosticFinding
            {
                Id = "update.install_errors",
                Category = DiagnosticCategory.WindowsUpdate,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Повторяющиеся ошибки установки обновлений"),
                Description = L.T("За последние 7 дней журнал зафиксировал {0} ошибок WindowsUpdateClient.", updateErrors),
                WhyItMatters = L.T("Обновления могут устанавливаться циклично или не устанавливаться вовсе."),
                Evidence = context.EventErrors
                    .Where(error => error.Provider.Equals("Microsoft-Windows-WindowsUpdateClient", StringComparison.OrdinalIgnoreCase))
                    .Select(FormatEventEvidence)
                    .ToList(),
                Actions = [OpenSection(L.T("Открыть «Обновления Windows»"), 16)]
            };
        }
    }

    // ===================== Сеть =====================

    private static IEnumerable<DiagnosticFinding> EvaluateNetwork(DiagnosticContext context)
    {
        if (context.NetworkHasActiveAdapter == false)
        {
            yield return new DiagnosticFinding
            {
                Id = "network.no_adapter",
                Category = DiagnosticCategory.Network,
                Severity = DiagnosticSeverity.Critical,
                Title = L.T("Активный сетевой адаптер не найден"),
                Description = L.T("Все сетевые адаптеры отключены или не подключены к сети."),
                WhyItMatters = L.T("Без активного адаптера недоступны сеть и интернет."),
                ExpectedState = L.T("Есть адаптер в состоянии Up"),
                ActualState = L.T("Активных адаптеров нет"),
                Evidence = [new DiagnosticEvidence("NetworkInterface", L.T("Нет активных"))],
            };
            yield break;
        }

        if (context.NetworkHasGateway == false)
        {
            yield return new DiagnosticFinding
            {
                Id = "network.no_gateway",
                Category = DiagnosticCategory.Network,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Не найден шлюз по умолчанию"),
                Description = L.T("У активного адаптера не задан default gateway — доступ за пределы локальной сети невозможен."),
                WhyItMatters = L.T("Без шлюза не работает выход в интернет."),
                ExpectedState = L.T("Шлюз по умолчанию задан"),
                ActualState = L.T("Шлюз не найден"),
                Evidence = [new DiagnosticEvidence("Gateway", L.T("Не задан"))],
                Actions = [OpenSection(L.T("Открыть «Сеть»"), 9)]
            };
        }

        if (context.NetworkDnsResolves == false)
        {
            yield return new DiagnosticFinding
            {
                Id = "network.dns_failure",
                Category = DiagnosticCategory.Network,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Не выполняется разрешение DNS-имён"),
                Description = L.T("Тестовое имя {0} не разрешилось в IP-адрес. Возможны проблемы с настройкой DNS.", context.NetworkDnsTarget ?? "-"),
                WhyItMatters = L.T("Сбой DNS внешне выглядит как «нет интернета», даже когда связь есть."),
                ExpectedState = L.T("Имя разрешается"),
                ActualState = L.T("Разрешение не удалось"),
                Evidence =
                [
                    new DiagnosticEvidence("DNS", L.T("Проверка не прошла")),
                    new DiagnosticEvidence("Host", context.NetworkDnsTarget ?? "-")
                ],
                Actions =
                [
                    new DiagnosticAction(
                        DiagnosticActionKind.RunFix,
                        L.T("Сбросить кэш DNS"),
                        FixId: "flush_dns",
                        VerifyProbeIds: ["network"]),
                    OpenSection(L.T("Открыть «Сеть»"), 9)
                ]
            };
        }
    }

    // ===================== Службы =====================

    private static IEnumerable<DiagnosticFinding> EvaluateServices(DiagnosticContext context)
    {
        // wuauserv обрабатывается правилами Windows Update — здесь не дублируем.
        foreach (var service in context.Services)
        {
            if (service.Name.Equals("wuauserv", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!service.IsRunning && !service.IsStartAllowed)
            {
                // Отключённая критичная служба: Defender — предупреждение, остальное — информация.
                var defender = service.Name.Equals("WinDefend", StringComparison.OrdinalIgnoreCase)
                    || service.Name.Equals("wscsvc", StringComparison.OrdinalIgnoreCase);
                yield return new DiagnosticFinding
                {
                    Id = "services.disabled." + service.Name,
                    Category = DiagnosticCategory.Services,
                    Severity = defender ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                    Title = L.T("Служба {0} отключена", service.Name),
                    Description = L.T("Критичная служба {0} ({1}) переведена в состояние «Отключена».", service.Name, service.DisplayName),
                    WhyItMatters = defender
                        ? L.T("Отключён Defender/центр безопасности — защита системы может не работать.")
                        : L.T("Отключённая служба может нарушать работу зависящих от неё компонентов."),
                    ExpectedState = L.T("Служба разрешена к запуску"),
                    ActualState = L.T("Служба отключена"),
                    Evidence = [new DiagnosticEvidence(service.Name, L.T("Отключена"))],
                    Actions = [OpenSection(L.T("Открыть «Службы Windows»"), 6)]
                };
            }
            else if (!service.IsRunning && IsAlwaysOnService(service.Name))
            {
                yield return new DiagnosticFinding
                {
                    Id = "services.stopped." + service.Name,
                    Category = DiagnosticCategory.Services,
                    Severity = DiagnosticSeverity.Warning,
                    Title = L.T("Служба {0} не работает", service.Name),
                    Description = L.T("Служба {0} ({1}) остановлена, хотя должна работать постоянно.", service.Name, service.DisplayName),
                    WhyItMatters = L.T("Остановленная системная служба нарушает работу зависящих компонентов."),
                    ExpectedState = L.T("Служба работает"),
                    ActualState = L.T("Служба остановлена"),
                    Evidence = [new DiagnosticEvidence(service.Name, L.T("Остановлена"))],
                    Actions =
                    [
                        StartServiceAction(service.Name),
                        OpenSection(L.T("Открыть «Службы Windows»"), 6)
                    ]
                };
            }
        }

        // Отказы служб в журнале (SCM 7000/7011/…): только устойчивые повторы.
        var scmFailures = context.EventErrors
            .Where(error => error.Provider.Equals("Service Control Manager", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (scmFailures.Sum(error => error.Count) >= ServiceFailureThreshold)
        {
            yield return new DiagnosticFinding
            {
                Id = "services.scm_failures",
                Category = DiagnosticCategory.Services,
                Severity = DiagnosticSeverity.Info,
                Title = L.T("Повторяющиеся сбои служб в журнале"),
                Description = L.T("Service Control Manager зафиксировал {0} отказов за 7 дней.", scmFailures.Sum(error => error.Count)),
                WhyItMatters = L.T("Повторяющиеся отказы служб — признак проблемы в конкретном компоненте."),
                Evidence = scmFailures.Select(FormatEventEvidence).ToList(),
                Actions = [OpenSection(L.T("Открыть «Службы Windows»"), 6)]
            };
        }

        // Звук: остановленная Audiosrv означает неработающий звук (п. 24 плана).
        var audio = context.Services.FirstOrDefault(service =>
            service.Name.Equals("Audiosrv", StringComparison.OrdinalIgnoreCase));
        if (audio is not null && !audio.IsRunning && audio.IsStartAllowed)
        {
            yield return new DiagnosticFinding
            {
                Id = "services.audio_stopped",
                Category = DiagnosticCategory.Services,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Служба звука не работает"),
                Description = L.T("Служба Audiosrv остановлена — звук в системе не воспроизводится."),
                WhyItMatters = L.T("Без Windows Audio не работают ни динамики, ни наушники, ни приложения со звуком."),
                ExpectedState = L.T("Служба Audiosrv работает"),
                ActualState = L.T("Служба Audiosrv остановлена"),
                Evidence = [new DiagnosticEvidence("Audiosrv", L.T("Остановлена"))],
                Actions =
                [
                    StartServiceAction("Audiosrv"),
                    OpenSection(L.T("Открыть «Службы Windows»"), 6)
                ]
            };
        }

        // AudioEndpointBuilder — вторая критичная зависимость звука: без него
        // устройства вывода не появляются в системе, даже если Audiosrv работает.
        var endpointBuilder = context.Services.FirstOrDefault(service =>
            service.Name.Equals("AudioEndpointBuilder", StringComparison.OrdinalIgnoreCase));
        if (endpointBuilder is not null && !endpointBuilder.IsRunning && endpointBuilder.IsStartAllowed)
        {
            yield return new DiagnosticFinding
            {
                Id = "services.audio_endpoint_stopped",
                Category = DiagnosticCategory.Services,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Служба построения аудиоконечных точек не работает"),
                Description = L.T("Служба AudioEndpointBuilder остановлена — аудиоустройства не будут появляться в системе."),
                WhyItMatters = L.T("Даже при работающем Windows Audio без конечных точек звука нет: ни вывода, ни устройств в микшере."),
                ExpectedState = L.T("Служба AudioEndpointBuilder работает"),
                ActualState = L.T("Служба AudioEndpointBuilder остановлена"),
                Evidence = [new DiagnosticEvidence("AudioEndpointBuilder", L.T("Остановлена"))],
                Actions =
                [
                    StartServiceAction("AudioEndpointBuilder"),
                    OpenSection(L.T("Открыть «Службы Windows»"), 6)
                ]
            };
        }

        // Печать: остановленный/отключённый Spooler — частая осознанная настройка.
        var spooler = context.Services.FirstOrDefault(service =>
            service.Name.Equals("Spooler", StringComparison.OrdinalIgnoreCase));
        if (spooler is not null && !spooler.IsRunning)
        {
            yield return new DiagnosticFinding
            {
                Id = "services.spooler_stopped",
                Category = DiagnosticCategory.Services,
                Severity = DiagnosticSeverity.Info,
                Title = L.T("Служба печати не работает"),
                Description = L.T("Диспетчер печати (Spooler) не запущен — печать и просмотр принтеров недоступны."),
                WhyItMatters = L.T("Отключение Spooler — распространённая осознанная настройка; это состояние, а не ошибка."),
                ExpectedState = L.T("Служба Spooler работает (если нужна печать)"),
                ActualState = L.T("Служба Spooler не запущена"),
                Evidence = [new DiagnosticEvidence("Spooler", L.T("Остановлена"))],
                Actions = [OpenSection(L.T("Открыть «Службы Windows»"), 6)]
            };
        }
    }

    // Службы, которые должны работать постоянно (автозапуск, без триггеров).
    private static bool IsAlwaysOnService(string name) => name.ToLowerInvariant() switch
    {
        "bits" or "usosvc" or "wsearch" or "eventlog" or "dhcp" or "dnscache"
            or "nlasvc" or "nsi" or "windefend" or "wscsvc" or "schedule" => true,
        _ => false
    };

    // ===================== Драйверы (п. 7.8) =====================

    private static IEnumerable<DiagnosticFinding> EvaluateDrivers(DiagnosticContext context)
    {
        // Код 22 — устройство отключено вручную (осознанная настройка → Info).
        var broken = context.DriverProblems
            .Where(problem => problem.ProblemCode != 22)
            .ToList();
        if (broken.Count > 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "drivers.problem_devices",
                Category = DiagnosticCategory.Drivers,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Устройства с ошибками драйверов"),
                Description = L.T("Диспетчер устройств сообщает о {0} устройствах с проблемами загрузки драйверов.", broken.Count),
                WhyItMatters = L.T("Устройство с problem code не работает, пока драйвер не установлен или не исправлен."),
                ExpectedState = L.T("Нет устройств с ошибками (код 0)"),
                ActualState = L.T("Проблемных устройств: {0}", broken.Count),
                Evidence = broken
                    .Take(5)
                    .Select(problem => new DiagnosticEvidence(
                        problem.Name,
                        "CM " + problem.ProblemCode + (problem.Manufacturer.Length > 0 ? " · " + problem.Manufacturer : string.Empty)))
                    .ToList(),
                Actions =
                [
                    OpenApp("Открыть диспетчер устройств", "devmgmt.msc"),
                    OpenSection(L.T("Открыть «Обновления Windows»"), 16)
                ]
            };
        }

        var disabledDevices = context.DriverProblems
            .Where(problem => problem.ProblemCode == 22)
            .ToList();
        if (disabledDevices.Count > 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "drivers.disabled_devices",
                Category = DiagnosticCategory.Drivers,
                Severity = DiagnosticSeverity.Info,
                Title = L.T("Отключённые устройства"),
                Description = L.T("В системе есть {0} отключённых вручную устройств.", disabledDevices.Count),
                WhyItMatters = L.T("Отключение устройства — осознанная настройка; это состояние, а не ошибка."),
                Evidence = disabledDevices
                    .Take(5)
                    .Select(problem => new DiagnosticEvidence(problem.Name, "CM 22"))
                    .ToList(),
                Actions = [OpenApp("Открыть диспетчер устройств", "devmgmt.msc")]
            };
        }
    }

    // ===================== Автозагрузка =====================

    private static IEnumerable<DiagnosticFinding> EvaluateStartup(DiagnosticContext context)
    {
        var invalid = context.StartupEntries
            .Where(entry => entry.Problem == "missing")
            .ToList();
        if (invalid.Count > 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "startup.invalid_entries",
                Category = DiagnosticCategory.Startup,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Недействительные записи автозагрузки"),
                Description = L.T("Найдено {0} записей автозагрузки, указывающих на отсутствующие файлы.", invalid.Count),
                WhyItMatters = L.T("Такие записи бесполезно нагружают запуск и захламляют автозагрузку."),
                ExpectedState = L.T("Все записи указывают на существующие файлы"),
                ActualState = L.T("Недействительных записей: {0}", invalid.Count),
                Evidence = invalid
                    .Take(5)
                    .Select(entry => new DiagnosticEvidence(entry.Source, entry.Command))
                    .ToList(),
                Actions = [OpenSection(L.T("Открыть «Автозагрузку»"), 7)]
            };
        }

        var duplicates = context.StartupEntries
            .Where(entry => entry.Problem == "duplicate")
            .ToList();
        if (duplicates.Count > 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "startup.duplicates",
                Category = DiagnosticCategory.Startup,
                Severity = DiagnosticSeverity.Info,
                Title = L.T("Дублирующиеся записи автозагрузки"),
                Description = L.T("Некоторые программы прописаны в автозагрузке более одного раза."),
                WhyItMatters = L.T("Дубликаты запускают одну и ту же программу дважды — лишняя нагрузка."),
                Evidence = duplicates
                    .Take(5)
                    .Select(entry => new DiagnosticEvidence(L.T("Команда"), entry.Command))
                    .ToList(),
                Actions = [OpenSection(L.T("Открыть «Автозагрузку»"), 7)]
            };
        }
    }

    // ===================== Безопасность =====================

    private static IEnumerable<DiagnosticFinding> EvaluateSecurity(DiagnosticContext context)
    {
        if (context.AntivirusRealTimeOn == false)
        {
            yield return new DiagnosticFinding
            {
                Id = "security.realtime_off",
                Category = DiagnosticCategory.Security,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Защита в реальном времени отключена"),
                Description = L.T("Зарегистрированное антивирусное ПО сообщает о выключенной защите в реальном времени."),
                WhyItMatters = L.T("С отключённой защитой система открыта для вредоносного ПО."),
                ExpectedState = L.T("Защита в реальном времени включена"),
                ActualState = L.T("Защита отключена"),
                Evidence = [new DiagnosticEvidence("SecurityCenter2", L.T("Защита отключена"))],
                Actions = [OpenApp("Открыть «Безопасность Windows»", "windowsdefender://")]
            };
        }

        if (context.AntivirusRegistered == false)
        {
            yield return new DiagnosticFinding
            {
                Id = "security.no_av",
                Category = DiagnosticCategory.Security,
                Severity = DiagnosticSeverity.Info,
                Title = L.T("Антивирусное ПО не зарегистрировано"),
                Description = L.T("Центр безопасности Windows не сообщает ни об одном антивирусном продукте."),
                WhyItMatters = L.T("Может означать отключённый Defender либо нестандартную защиту."),
                Evidence = [new DiagnosticEvidence("SecurityCenter2", L.T("Продуктов не найдено"))],
            };
        }
    }

    // ===================== События =====================

    private static IEnumerable<DiagnosticFinding> EvaluateEvents(DiagnosticContext context)
    {
        var crashes = context.EventErrors
            .Where(error => IsProviderIn(error, "Application Error", "Windows Error Reporting", ".NET Runtime"))
            .ToList();
        var crashCount = crashes.Sum(error => error.Count);
        if (crashCount >= AppCrashThreshold)
        {
            yield return new DiagnosticFinding
            {
                Id = "events.app_crashes",
                Category = DiagnosticCategory.Events,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Повторяющиеся падения приложений"),
                Description = L.T("За 7 дней зафиксировано {0} аварийных завершений приложений.", crashCount),
                WhyItMatters = L.T("Повторяющиеся падения указывают на проблемную программу или повреждённые файлы."),
                Evidence = crashes
                    .OrderByDescending(error => error.Count)
                    .Take(5)
                    .Select(FormatEventEvidence)
                    .ToList(),
            };
        }

        var bugChecks = context.EventErrors.Where(error =>
            error.Provider.Equals("BugCheck", StringComparison.OrdinalIgnoreCase)).ToList();
        var unexpectedReboots = context.EventErrors.Where(error =>
            error.Provider.Equals("Kernel-Power", StringComparison.OrdinalIgnoreCase)
            && error.EventId == 41).ToList();
        if (bugChecks.Count > 0 || unexpectedReboots.Count > 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "events.unexpected_reboot",
                Category = DiagnosticCategory.Events,
                Severity = bugChecks.Count > 0 ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning,
                Title = bugChecks.Count > 0
                    ? L.T("Обнаружены сбои с BSOD (BugCheck)")
                    : L.T("Неожиданные перезагрузки"),
                Description = bugChecks.Count > 0
                    ? L.T("Система фиксирует остановку с ошибкой ядра ({0} событий за 7 дней).", bugChecks.Sum(error => error.Count))
                    : L.T("Kernel-Power 41 зафиксировал {0} неожиданных отключений за 7 дней.", unexpectedReboots.Sum(error => error.Count)),
                WhyItMatters = L.T("Причины: драйверы, память, питание. Нужна оценка по коду ошибки."),
                Evidence = bugChecks.Concat(unexpectedReboots)
                    .OrderByDescending(error => error.Count)
                    .Select(FormatEventEvidence)
                    .ToList(),
            };
        }

        var whea = context.EventErrors.Where(error =>
            error.Provider.Equals("WHEA-Logger", StringComparison.OrdinalIgnoreCase)).ToList();
        if (whea.Count > 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "events.whea",
                Category = DiagnosticCategory.Events,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Аппаратные ошибки (WHEA)"),
                Description = L.T("WHEA-Logger зафиксировал {0} аппаратных событий за 7 дней.", whea.Sum(error => error.Count)),
                WhyItMatters = L.T("Аппаратные ошибки могут предшествовать сбоям и BSOD."),
                Evidence = whea.Select(FormatEventEvidence).ToList(),
            };
        }
    }

    // ===================== Система =====================

    private static IEnumerable<DiagnosticFinding> EvaluateSystem(DiagnosticContext context)
    {
        if (context.PendingReboot == true)
        {
            yield return new DiagnosticFinding
            {
                Id = "system.pending_reboot",
                Category = DiagnosticCategory.System,
                Severity = DiagnosticSeverity.Info,
                Title = L.T("Требуется перезагрузка"),
                Description = L.T("В системе есть незавершённые изменения ({0}).", context.PendingRebootSource ?? "-"),
                WhyItMatters = L.T("Часть установленных изменений не работает до перезагрузки."),
                Evidence = [new DiagnosticEvidence("PendingReboot", context.PendingRebootSource ?? "-")],
            };
        }

        if (context.ComponentStoreCorrupted == true)
        {
            yield return new DiagnosticFinding
            {
                Id = "sysfiles.corruption",
                Category = DiagnosticCategory.SystemFiles,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Хранилище компонентов Windows помечено как повреждённое"),
                Description = L.T("DISM /CheckHealth сообщает о повреждении хранилища компонентов. Возможны сбои установки обновлений и компонентов."),
                WhyItMatters = L.T("Повреждённое хранилище ломает обновления и установку компонентов."),
                ExpectedState = L.T("Повреждений не обнаружено"),
                ActualState = L.T("DISM сообщает о повреждении"),
                Evidence = context.ComponentStoreSample is null
                    ? []
                    : [new DiagnosticEvidence("DISM /CheckHealth", context.ComponentStoreSample)],
                Actions =
                [
                    new DiagnosticAction(
                        DiagnosticActionKind.RunFix,
                        L.T("Восстановить (DISM + SFC)"),
                        FixId: "dism_restore",
                        VerifyProbeIds: ["sysfiles"],
                        RequiresConfirm: true)
                ]
            };
        }
    }

    // ===================== Накопители и время (вторая очередь) =====================

    private static IEnumerable<DiagnosticFinding> EvaluateStorage(DiagnosticContext context)
    {
        if (context.SmartFailures.Count > 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "storage.smart_failure",
                Category = DiagnosticCategory.Storage,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("SMART предсказывает отказ диска"),
                Description = L.T("Для {0} накопителя(ей) SMART сообщает о вероятном отказе в ближайшее время.", context.SmartFailures.Count),
                WhyItMatters = L.T("Отказ диска означает потерю данных — стоит как можно скорее скопировать важное."),
                ExpectedState = L.T("PredictFailure = false на всех накопителях"),
                ActualState = L.T("PredictFailure = true: {0}", context.SmartFailures.Count),
                Evidence = context.SmartFailures
                    .Take(5)
                    .Select(failure => new DiagnosticEvidence(failure.DriveName, "PredictFailure = true"))
                    .ToList(),
                Actions = [OpenApp("Открыть управление дисками", "diskmgmt.msc")]
            };
        }

        // Синхронизация времени: остановленная w32time ведёт к уходу часов,
        // но не является поломкой → Info (п. 24 плана).
        var timeService = context.Services.FirstOrDefault(service =>
            service.Name.Equals("w32time", StringComparison.OrdinalIgnoreCase));
        if (timeService is not null && !timeService.IsRunning && timeService.IsStartAllowed)
        {
            yield return new DiagnosticFinding
            {
                Id = "services.timesync_stopped",
                Category = DiagnosticCategory.Services,
                Severity = DiagnosticSeverity.Info,
                Title = L.T("Синхронизация времени не работает"),
                Description = L.T("Служба времени Windows (w32time) не запущена — часы могут уходить от реального времени."),
                WhyItMatters = L.T("Расхождение часов ломает HTTPS, лицензии и планировщик."),
                ExpectedState = L.T("Служба w32time работает (или стартует по триггеру)"),
                ActualState = L.T("Служба w32time остановлена"),
                Evidence = [new DiagnosticEvidence("w32time", L.T("Остановлена"))],
                Actions =
                [
                    StartServiceAction("w32time"),
                    OpenSection(L.T("Открыть «Службы Windows»"), 6)
                ]
            };
        }
    }

    // ===================== Аудио: устройства (п. 24) =====================

    private static IEnumerable<DiagnosticFinding> EvaluateAudio(DiagnosticContext context)
    {
        var devices = context.AudioDevices;

        // Ни одного звукового устройства: без звука в принципе, но не всегда
        // поломка (звук может быть на HDMI/USB, который система не опросила).
        if (devices.Count == 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "audio.no_devices",
                Category = DiagnosticCategory.Audio,
                Severity = DiagnosticSeverity.Info,
                Title = L.T("Звуковые устройства не обнаружены"),
                Description = L.T("Windows не сообщает ни об одном звуковом устройстве. Если звука нет — проверьте подключения и BIOS."),
                WhyItMatters = L.T("Отсутствие устройств объясняет «нет звука»: системе просто нечего выводить."),
                ExpectedState = L.T("Есть хотя бы одно устройство вывода звука"),
                ActualState = L.T("Устройств нет"),
                Evidence = [new DiagnosticEvidence("Win32_SoundDevice", L.T("Устройств нет"))],
                Actions = [OpenApp("Открыть диспетчер устройств", "devmgmt.msc")]
            };
            yield break;
        }

        // Устройства с ошибкой статуса, которые DriverProbe не покрыл
        // (код 0, но Status = Error): дубликатов с drivers.problem_devices нет.
        var errors = devices
            .Where(device => device.ProblemCode == 0
                && device.Status.Equals("Error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (errors.Count > 0)
        {
            yield return new DiagnosticFinding
            {
                Id = "audio.device_error",
                Category = DiagnosticCategory.Audio,
                Severity = DiagnosticSeverity.Warning,
                Title = L.T("Звуковое устройство сообщает об ошибке"),
                Description = L.T("{0} звуковое(ых) устройство(й) в состоянии «Error» без problem code — вероятен сбой драйвера.", errors.Count),
                WhyItMatters = L.T("Устройство есть, но не отвечает: звук на нём работать не будет."),
                ExpectedState = L.T("Статус всех звуковых устройств — OK"),
                ActualState = L.T("Устройств с ошибкой: {0}", errors.Count),
                Evidence = errors
                    .Take(5)
                    .Select(device => new DiagnosticEvidence(device.Name, "Status = Error"))
                    .ToList(),
                Actions = [OpenApp("Открыть диспетчер устройств", "devmgmt.msc")]
            };
        }
    }

    // ===================== Хелперы =====================

    private static DiagnosticAction OpenSection(string title, int sectionNumber) =>
        new(DiagnosticActionKind.OpenSection, title, SectionNumber: sectionNumber);

    private static DiagnosticAction OpenApp(string title, string target) =>
        new(DiagnosticActionKind.OpenApp, title, AppTarget: target);

    private static DiagnosticAction StartServiceAction(string serviceName) =>
        new(
            DiagnosticActionKind.RunFix,
            L.T("Запустить службу {0}", serviceName),
            FixId: "start_service",
            Parameter: serviceName,
            VerifyProbeIds: ["services", "update"],
            RequiresConfirm: true);

    private static bool IsProviderIn(EventErrorSummary error, params string[] providers) =>
        providers.Any(provider => error.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));

    private static DiagnosticEvidence FormatEventEvidence(EventErrorSummary error) => new(
        error.Provider + " " + error.EventId,
        error.Count + " ×" + (error.LatestTime.HasValue ? " (" + error.LatestTime.Value.ToString("dd.MM.yyyy HH:mm") + ")" : string.Empty));

    private static string FormatBytes(ulong bytes)
    {
        if (bytes == 0)
        {
            return "0 Б";
        }

        double value = bytes;
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : "0.0";
        return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
