namespace SCU.Models;

// Тип системной цели, которой управляет шаг профиля. Каждый шаг — декларативная
// ссылка на СУЩЕСТВУЮЩИЙ сервис: никакой новой системной логики в профилях нет.
public enum ProfileTargetKind
{
    PowerPlan,        // схема электропитания (PowerService.SetPowerPlanAsync)
    NetworkGaming,    // игровой профиль сети (NetworkService.ApplyGamingProfileAsync / Restore*)
    GameSwitch,       // переключатель раздела «Ввод» (InputService.SetSwitch)
    PrivacyCategory   // категория приватности (PrivacyService.DisableAsync / EnableAsync)
}

// Желаемое состояние шага: On = цель применена, Off = возвращено к исходному.
// Для PowerPlan осмысленно только On (выбор другой схемы сам является откатом);
// для NetworkGaming On = игровой профиль, Off = состояние из бэкапа.
public enum StepDesire
{
    Off = 0,
    On = 1
}

// Один шаг профиля: стабильный Id (ключ сохранений и истории), категория и название —
// ключи L.T (русский текст), ReferenceId — идентификатор цели у конкретного сервиса
// (GUID схемы, id переключателя, id категории приватности).
public sealed record ProfileStep(
    string Id,
    string Category,
    string TitleKey,
    ProfileTargetKind Kind,
    string? ReferenceId);

// Строка оценки соответствия профилю: Desired — цель профиля, Actual — фактически
// прочитанное состояние (null = прочитать не удалось), Matches = совпало ли.
// Unknown-состояние честно считается несовпадением, но в списке изменений не трогается.
public sealed record ProfileComplianceEntry(
    string StepId,
    StepDesire Desired,
    bool? Actual,
    bool Matches);

// Итог оценки: строки по шагам целевого профиля (порядок каталога) и счётчики.
// Matched не включает шаги с неизвестным состоянием — «не удалось прочитать» не
// засчитывается как соответствие.
public sealed record ProfileComplianceResult(
    IReadOnlyList<ProfileComplianceEntry> Entries,
    int Matched,
    int Total);

// Сохраняемое состояние профилей: %AppData%\SCU\state\profile.json.
// ActiveProfile — выбранный профиль (null = «не следить»); CustomSteps — словарь
// stepId → желание для настраиваемого профиля (отсутствие шага = «не важно»).
// Старые файлы читаются: отсутствующие поля подставляются значениями по умолчанию.
public sealed class CustomProfileState
{
    public string? ActiveProfile { get; set; }

    public Dictionary<string, StepDesire> CustomSteps { get; set; } = [];
}
