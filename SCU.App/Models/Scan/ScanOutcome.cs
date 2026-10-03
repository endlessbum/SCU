namespace SCU.Models.Scan;

// Единая классификация исхода скана (аудит п. 14, приоритет 1): все потребители
// ScannerRunner (ScannerViewModel, BrowserViewModel, история, уведомления)
// ветвляются по одному Outcome, а не независимо трактуют IsSuccess + Summary.
public enum ScanOutcome
{
    // Чистый прогон: finished получен, отмены/ошибок/пропусков/детектов нет.
    Clean,

    // Найдены угрозы (полное покрытие).
    Threats,

    // Покрытие неполное: ошибки сканирования или пропущенные файлы/архивы.
    // Не должен попадать в ту же ветку, что Clean.
    Partial,

    // Сканирование прервано пользователем.
    Cancelled,

    // ScannerCore завершился без итогового отчёта / не запустился.
    Failed,
}

// Маппер по матрице аудита (п. 14, приоритет 7):
//   finished отсутствует              → Failed
//   finished.cancelled = true         → Cancelled
//   errors > 0 или filesSkipped > 0   → Partial
//   detections > 0                    → Threats
//   иначе                             → Clean
public static class ScanOutcomeMapper
{
    public static ScanOutcome Map(ScanResultDto? finished)
    {
        if (finished is null)
        {
            return ScanOutcome.Failed;
        }

        if (finished.Cancelled)
        {
            return ScanOutcome.Cancelled;
        }

        if (finished.Summary.Errors > 0 || finished.Summary.FilesSkipped > 0)
        {
            return ScanOutcome.Partial;
        }

        return finished.Detections.Count > 0 ? ScanOutcome.Threats : ScanOutcome.Clean;
    }
}
