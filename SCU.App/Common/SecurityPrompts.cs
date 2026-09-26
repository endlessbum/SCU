namespace SCU.Common;

// Глобальная политика подтверждений рисковых действий (тумблер «UAC» в настройках).
// Включено (по умолчанию) — операции спрашивают подтверждение через
// IConfirmDialogService; выключено — все такие уведомления пропускаются,
// операции выполняются сразу (Ask возвращает «подтверждено»).
public static class SecurityPrompts
{
    public static bool Enabled { get; set; } = true;
}
