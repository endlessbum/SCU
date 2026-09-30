namespace SCU.Interop;

// Общие гарантии раннеров внешних процессов: таймаут против навсегда зависшего
// powershell/netsh/сканера (комбинируется с пользовательской отменой) и тихое
// наблюдение read-тасков stdout/stderr после отмены/таймаута.
internal static class RunnerGuard
{
    // SCU.ps1 — отдельные bounded-действия; зависший скрипт (например, диалог WMI
    // или Read-Host) не должен держать операцию вечно.
    public static readonly TimeSpan PowerShellTimeout = TimeSpan.FromMinutes(15);

    // Системные утилиты и сканер: диск эвристически может сканиться долго (HDD,
    // большая директория), поэтому таймаут щедрый, но конечный.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    // Read-таски завершаются после kill по закрытию пайпов; их исключения
    // (pipe break, отмена) — штатный исход, не должны становиться UnobservedTaskException.
    public static async Task ObserveQuietly(Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
