namespace SCU.Models;

// Состояние сканирования одной области Smart Scan. Attention зарезервирована
// для этапов 2–3 (Before/After diff): в MVP чек-лист использует Pending/Running/Ok/Failed.
public enum AreaScanState
{
    Pending,
    Running,
    Ok,
    Attention,
    Failed
}
