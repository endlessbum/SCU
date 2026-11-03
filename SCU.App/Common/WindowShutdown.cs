namespace SCU.Common;

// Освобождение ресурсов окна при закрытии — ровно один раз и только тогда, когда
// закрытие действительно происходит.
//
// Инцидент (аудит, п. 1): OnClosing главного окна снимал подписки, останавливал
// таймеры, снимал глобальные горячие клавиши и вызывал ViewModel.Dispose() ДО
// base.OnClosing(e). Именно base.OnClosing(e) поднимает событие Closing, чей
// обработчик при включённом переводе раскладки отменяет закрытие и сворачивает
// окно. Итог: процесс оставался жив, но UI, переводчик и горячие клавиши были
// уже мертвы, а повторное закрытие снова отменялось — завершить приложение можно
// было только Диспетчером задач.
//
// Правило сведено в отдельный тип, чтобы порядок «отмена → выход без очистки»
// был покрыт тестом, а не только комментарием на месте вызова.
internal sealed class WindowShutdown
{
    private readonly Action _release;
    private bool _released;

    internal WindowShutdown(Action release) =>
        _release = release ?? throw new ArgumentNullException(nameof(release));

    /// <summary>Ресурсы уже освобождены.</summary>
    internal bool Released => _released;

    /// <summary>
    /// Освобождает ресурсы, если закрытие не отменено. Отменённое закрытие
    /// (фоновый режим) окно продолжает работать: ресурсы остаются нетронутыми,
    /// и следующий вызов с <paramref name="cancelled"/> = false освободит их.
    /// </summary>
    internal void ReleaseIfClosing(bool cancelled)
    {
        if (cancelled)
        {
            return;
        }

        Release();
    }

    /// <summary>Освобождает ресурсы; повторные вызовы ничего не делают.</summary>
    internal void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _release();
    }
}
