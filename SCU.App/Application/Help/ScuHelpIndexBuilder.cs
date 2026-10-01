using SCU.ViewModels.Sections;

namespace SCU.AppCore.Help;

// Сборка индекса справки из реальных источников SCU: разделы, утилиты
// Dashboard, переключатели разделов UI/Input, тексты ⓘ-информеров (I_*).
// Ничего не выдумывается: каждая запись ведёт к существующему объекту SCU.
public sealed class ScuHelpIndexBuilder
{
    private readonly List<ScuHelpEntry> _entries = [];
    private readonly Func<string, string?> _resolveResource;

    // resolveResource: чтение строки из XAML-словаря (S_*/I_*). В тестах —
    // словарь-заглушка, в приложении — Application.Current.TryFindResource.
    public ScuHelpIndexBuilder(Func<string, string?> resolveResource)
    {
        _resolveResource = resolveResource;
    }

    public IReadOnlyList<ScuHelpEntry> Build() => _entries;

    // Раздел: S_SectionNN_Title/Desc — заголовок и описание из локализации.
    public ScuHelpIndexBuilder AddSection(int sectionNumber)
    {
        _entries.Add(new ScuHelpEntry
        {
            Id = "section_" + sectionNumber.ToString("00"),
            SectionNumber = sectionNumber,
            Title = Resolve($"S_Section{sectionNumber:00}_Title"),
            Description = Resolve($"S_Section{sectionNumber:00}_Desc"),
            Keywords = SectionKeywords(sectionNumber),
        });
        return this;
    }

    // Утилита Dashboard: заголовок берётся как у самой утилиты (ResolveTitle:
    // ключ словаря или русский текст под L.T), keywords — уже двуязычные.
    public ScuHelpIndexBuilder AddUtility(BatchUtility utility)
    {
        _entries.Add(new ScuHelpEntry
        {
            Id = "utility_" + utility.Id,
            SectionNumber = utility.Section,
            Title = utility.ResolveTitle(),
            Description = utility.TargetText is null ? string.Empty : Resolve(utility.TargetText),
            Keywords = utility.Keywords,
            RelatedUtilityId = utility.Id,
        });
        return this;
    }

    // Переключатель раздела UI/Input: постоянное описание функции — что именно
    // делает настройка (не «включить/отключить»).
    public ScuHelpIndexBuilder AddSwitch(string switchId, int sectionNumber, SwitchRow row)
    {
        _entries.Add(new ScuHelpEntry
        {
            Id = "switch_" + sectionNumber.ToString("00") + "_" + switchId,
            SectionNumber = sectionNumber,
            Title = row.LocalizedTitle,
            Description = row.ActionDescription,
            Keywords = SwitchKeywords(switchId),
            RelatedUtilityId = "row_" + switchId,
        });
        return this;
    }

    // Текст ⓘ-информера (I_*): подробное описание действия/функции. Связь с
    // конкретной утилитой — где она есть, иначе привязка только к разделу.
    public ScuHelpIndexBuilder AddInfo(string infoKey, int sectionNumber, string? utilityId = null)
    {
        var title = utilityId is { Length: > 0 }
            ? _entries.LastOrDefault(entry => entry.RelatedUtilityId == utilityId)?.Title
            : null;
        _entries.Add(new ScuHelpEntry
        {
            Id = "info_" + infoKey,
            SectionNumber = sectionNumber,
            Title = title ?? Resolve($"S_Section{sectionNumber:00}_Title"),
            InfoText = Resolve(infoKey),
            RelatedUtilityId = utilityId,
        });
        return this;
    }

    private string Resolve(string key) => _resolveResource(key) ?? key;

    // Двуязычные keywords для разделов: заголовок/описание берутся на языке UI,
    // а эти стемы дают английскому запросу находить раздел на русском UI и наоборот.
    private static string SectionKeywords(int sectionNumber) => sectionNumber switch
    {
        0 => "главн dashboard home состояние pc пакет выключатель",
        1 => "информация система о железе cpu ram gpu информация о системе system info",
        2 => "компонент components directx vc++ .net установка redistributable",
        3 => "очист clean temp кэш корзин браузер update кэш диск cleanup",
        4 => "мусор bloat appx uwp удаление uninstall crapware",
        5 => "приватн privacy телеметри telemetry шпион spy слеж uwp copilot советы",
        6 => "служб services бэкап backup восстанов restore отключ disable",
        7 => "автозагруз startup автозапуск boot login run startup",
        8 => "питан power план hibernat гиберн fastboot быстр памят pagefile cpu ядер",
        9 => "сеть network tcp mtu qos netbios ecn autotun адаптер adapter интернет",
        10 => "интерфейс ui explorer проводник анимац animations прозрачн эскиз тени контекст меню панел задач taskbar",
        11 => "ввод input мышь mouse клавиатур keyboard game bar dvr game mode edge игры",
        12 => "обслужив maintenance dism sfc целостн integrity winsxs restore point дамп dump",
        13 => "безопасн uac контроль учётных защит admin",
        15 => "задач задач планировщ scheduler task бэкап backup",
        16 => "обновл update windows update драйвер driver пауза pause блок block",
        18 => "история history лог журнал операции",
        19 => "приложен apps установленн installed programs uninstall",
        20 => "бэнчмарк benchmark индекс состояние оценка score",
        21 => "сканер scanner антивирус virus quarantine карантин eicar scan",
        22 => "браузер browser интернет web вкладк",
        23 => "диагност troubleshooting неполадк проблема probe проверка исправлен",
        24 => "ai ассистент помощник deepseek api чат",
        _ => string.Empty,
    };

    // Явные стемы для id переключателей: Title/Description — на языке UI, а поиск
    // должен находить функцию и английским запросом на русском UI.
    private static string SwitchKeywords(string switchId) => switchId switch
    {
        "open-to-this-pc" => "проводник explorer этот компьютер главн home quick access",
        "home-button" => "главн home кнопка навигация",
        "gallery-button" => "галере gallery фото кнопка навигация",
        "network-button" => "сеть network кнопка навигация",
        "recycle-nav" => "корзин recycle навигация",
        "recycle-desktop" => "корзин recycle рабочий стол desktop",
        "compact-view" => "компактн compact вид проводник",
        "recent-files" => "недавн recent files быстры доступ приватн",
        "classic-context-menu" => "классическ classic контекст context menu меню",
        "show-file-extensions" => "расширени extension файл расширения файлов",
        "show-hidden-files" => "скрыт hidden системн файлов",
        "full-path" => "полн path путь заголовок адресн",
        "item-checkboxes" => "флажк checkbox выбор элемент",
        "onedrive-nav" => "onedrive облако навигация",
        "animations" => "анимац animation эффект visual эффекты окон меню",
        "transparency" => "прозрачн transparen aero стекло",
        "thumbnails" => "эскиз thumbnail значк icons предпросмотр",
        "shadows" => "тен shadow выделен",
        "recommended" => "рекомендуем recommended пуск start меню",
        "mouse-acceleration" => "мышь mouse ускорен accelerat указател курсор pointer",
        "sticky-keys" => "залипан sticky keys клавиатур keyboard",
        "edge-startup-boost" => "edge браузер ускорен startup boost фон",
        "game-bar" => "game bar игров панель win g overlay оверлей",
        "game-dvr" => "dvr запись record capture gameplay фон геймплей",
        "game-mode" => "game mode режим игр производительность ресурсов",
        _ => switchId.Replace('-', ' '),
    };
}
