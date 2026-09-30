namespace SCU.Common;

// П. 14 аудита: структурированное описание опасного изменения, показываемое
// пользователю перед применением. Все элементы обязательного флоу:
// текущее состояние -> новое состояние -> последствия -> откат (если возможен).
// Политики SecurityPrompts подчиняется так же, как обычные подтверждения:
// тумблер «UAC» выключен — пользователь сознательно отказался от уведомлений.
public sealed record DestructiveChange(
    string Title,
    string CurrentState,
    string NewState,
    string Consequences,
    string? Rollback = null,
    string? ConfirmText = null);
