using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SCU.Common;
using SCU.Models.Scan;

namespace SCU.Infrastructure.Storage;

// QuarantineManager, срез v2 (документ п. 21): %LOCALAPPDATA%\SCU\Quarantine\,
// objects/ + metadata/. Никогда не удалять по умолчанию — только изолировать.
// После успешной фиксации копии оригинал удаляется. Восстановление и
// безвозвратное удаление — по явному действию пользователя (с подтверждением).
// Член архива изолируется контейнером целиком (п. 20), поэтому здесь всегда
// обычные файлы с реальными путями.

public sealed class QuarantineItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("originalPath")]
    public string OriginalPath { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    // Хэш самого изолированного объекта (копии). Не равен Sha256 детекции:
    // у членов архива изолируется контейнер, чей хэш другой. Старые метаданные
    // без этого поля восстанавливаются без проверки целостности.
    [JsonPropertyName("objectSha256")]
    public string ObjectSha256 { get; set; } = string.Empty;

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = string.Empty;

    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("quarantinedAt")]
    public DateTime QuarantinedAt { get; set; }

    // Для UI: единый формат дат интерфейса (дд.мм.гггг чч:мм), а не культура потока.
    [JsonIgnore]
    public string QuarantinedAtText => SCU.Common.L.DateTime(QuarantinedAt);

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

public sealed class QuarantineService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly string _objectsDir;
    private readonly string _metadataDir;

    private readonly Logger _logger;

    public QuarantineService(string? root = null, Logger? logger = null)
    {
        _logger = logger ?? Logger.CurrentRun;
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SCU", "Quarantine");
        // Путь не рядом с exe (документ п. 21.испр): иначе карантин попадает
        // в следующий Full Scan сканера.
        _objectsDir = Path.Combine(_root, "objects");
        _metadataDir = Path.Combine(_root, "metadata");
        Directory.CreateDirectory(_objectsDir);
        Directory.CreateDirectory(_metadataDir);

        // Метаданные карантина — внешняя граница доверия: по OriginalPath повышенный
        // SCU восстанавливает файлы. Пока сеанс повышен, каталог закрывается DACL
        // «Administrators + SYSTEM», чтобы процесс того же пользователя не мог
        // подменить метаданные до восстановления. Best-effort: сбой ACL не должен
        // ломать запуск сканера, решение фиксируется в журнале.
        if (Elevation.IsAdmin())
        {
            try
            {
                SecureDirectory.EnsureAdminsOnly(_root);
                _logger.Info("QUARANTINE | secure acl applied | " + _root);
            }
            catch (Exception exception)
            {
                _logger.Warn("QUARANTINE | secure acl failed | " + exception.Message);
            }
        }
    }

    public string Root => _root;

    // Путь к изолированному объекту (для безопасного просмотра карантинного файла).
    // Id приходит из внешних метаданных — невалидный даёт пустой путь, а не выход
    // за пределы objects/ (аудит п. 4).
    public string GetObjectPath(string id) =>
        IsValidQuarantineId(id) ? Path.Combine(_objectsDir, id + ".qtn") : string.Empty;

    // Изоляция файла: копия в objects/ с обезличенным именем + метаданные,
    // после успешной записи метаданных оригинал удаляется. Возврат false,
    // если файл занят или недоступен — оригинал остаётся на месте.
    public bool Quarantine(DetectionDto detection, out string error)
    {
        error = string.Empty;
        var source = detection.QuarantineTarget;
        if (!File.Exists(source))
        {
            error = "Файл не найден.";
            return false;
        }

        var id = Guid.NewGuid().ToString("N");
        var objectPath = Path.Combine(_objectsDir, id + ".qtn");
        var metadataPath = Path.Combine(_metadataDir, id + ".json");

        try
        {
            // Снимок источника (SCU-03): дескриптор открыт через CreateFile с
            // GENERIC_READ | DELETE и без FILE_SHARE_WRITE/FILE_SHARE_DELETE —
            // пока он жив, другой процесс не может записать, переименовать,
            // заменить или удалить исходник: имя и содержимое объекта «приколоты»
            // на весь цикл сверка→копия→удаление. Хэш источника снимается до
            // копирования и сверяется с хэшем копии — между «сканер увидел файл»
            // и «карантин зафиксировал объект» содержимое не может измениться
            // незамеченным. FileAccess.Delete в .NET не существует, поэтому
            // Delete-доступ даёт только прямое CreateFile.
            string? sourceSha;
            using (var sourceHandle = OpenPinnedSourceHandle(source))
            using (var sourceStream = new FileStream(sourceHandle, FileAccess.Read))
            {
                sourceSha = ComputeSha256(sourceStream);
                if (sourceSha is null)
                {
                    error = "Исходный файл не читается.";
                    return false;
                }

                // Копия читается из закреплённого потока, а не по пути — никакого
                // повторного разрешения имени между проверкой и фиксацией.
                sourceStream.Position = 0;
                using (var objectStream = File.Create(objectPath))
                {
                    sourceStream.CopyTo(objectStream);
                }

                // Проверка фиксации: размер и хэш копии совпадают с оригиналом.
                if (new FileInfo(objectPath).Length != sourceStream.Length)
                {
                    File.Delete(objectPath);
                    error = "Размер копии не совпал с оригиналом.";
                    return false;
                }

                var objectSha = ComputeSha256(objectPath);
                if (objectSha is null
                    || !string.Equals(objectSha, sourceSha, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(objectPath);
                    error = "Хэш копии не совпал с оригиналом.";
                    return false;
                }

                var item = new QuarantineItem
                {
                    Id = id,
                    OriginalPath = source,
                    Sha256 = detection.Sha256,
                    ObjectSha256 = objectSha,
                    Verdict = detection.Verdict,
                    RuleId = detection.RuleId,
                    Description = detection.Description,
                    QuarantinedAt = DateTime.Now,
                    SizeBytes = new FileInfo(objectPath).Length,
                };
                File.WriteAllText(metadataPath, JsonSerializer.Serialize(item, JsonOptions));

                // Удаление проверенного объекта (SCU-03): раньше File.Delete(source)
                // выполнялся ПОСЛЕ закрытия дескриптора по имени пути — конкурентная
                // замена файла в этом окне приводила к удалению чужого файла. Теперь
                // объект удаляется через тот же дескриптор, по которому снимался хэш:
                // FILE_DISPOSITION_INFO помечает объект, он удаляется при закрытии
                // последнего хэндла; в окне «помечен, но не закрыт» новые открытия
                // имени отвергаются, подмена невозможна. Если метка не встала —
                // исходник остаётся на месте, а частичная запись откатывается.
                MarkFileForDeletion(sourceHandle);
            }

            // Обычный случай: объект удалён при закрытии дескриптора.
            if (!File.Exists(source))
            {
                return true;
            }

            // Отложенное удаление: внешний процесс держит свой хэндл, объект
            // помечен и будет удалён при его закрытии. Записи карантина НЕ
            // откатываем: если помеченный объект исчезнет позже, запись с копией
            // останется корректной, а откат при живом исходнике не оставил бы
            // «призрачного» состояния — пользователю честно сообщаем о занятости.
            error = "Исходник помечен на удаление, но занят другим процессом — удаление завершится после освобождения.";
            _logger.Warn("QUARANTINE | source delete deferred by external handle | " + source);
            return false;
        }
        catch (IOException exception)
        {
            // Оригинал мог остаться на месте (занят, отказ в доступе): запись без него
            // неполна — удаляем и объект, и метаданные, чтобы не оставлять «призрачную»
            // запись карантина, из которой нельзя восстановиться.
            TryDelete(objectPath);
            TryDelete(metadataPath);
            error = exception.Message;
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            TryDelete(objectPath);
            TryDelete(metadataPath);
            error = exception.Message;
            return false;
        }
    }

    // Количество записей, пропущенных последним List() из-за нечитаемых
    // метаданных (аудит 2, п. 29): пользователь видит, что записи скрыты,
    // а не «карантин пуст».
    public int CorruptedMetadataCount { get; private set; }

    public IReadOnlyList<QuarantineItem> List()
    {
        var items = new List<QuarantineItem>();
        var corrupted = 0;
        foreach (var metadataPath in Directory.EnumerateFiles(_metadataDir, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<QuarantineItem>(
                    File.ReadAllText(metadataPath), JsonOptions);
                if (item is null)
                {
                    continue;
                }

                // Reconciliation orphans (аудит): метаданные без объекта-компаньона
                // не должны отображаться вечно — они же блокируют повторный
                // DeletePermanently той записи. Объект мог быть потерян только
                // при сбое среднего шага Restore.
                if (!File.Exists(ObjectPath(item.Id)))
                {
                    _logger.Warn("QUARANTINE | orphan metadata (no object) cleaned | " + item.Id);
                    try
                    {
                        File.Delete(metadataPath);
                    }
                    catch (Exception deleteException)
                    {
                        _logger.Warn("QUARANTINE | orphan cleanup failed | " + deleteException.Message);
                        corrupted++;
                    }

                    continue;
                }

                items.Add(item);
            }
            catch (IOException exception)
            {
                // Повреждённый файл метаданных не должен ронять список, но и
                // молча скрывать запись тоже нельзя.
                corrupted++;
                _logger.Warn("QUARANTINE | metadata unreadable | " + metadataPath
                             + " | " + exception.Message);
            }
            catch (JsonException)
            {
                corrupted++;
                _logger.Warn("QUARANTINE | metadata corrupt | " + metadataPath);
            }
        }

        CorruptedMetadataCount = corrupted;
        return items.OrderByDescending(item => item.QuarantinedAt).ToList();
    }

    // Восстановление: копия обратно по оригинальному пути; при занятом
    // месте — путь с суффиксом. Объект и метаданные удаляются при успехе.
    public bool Restore(QuarantineItem item, out string error)
    {
        error = string.Empty;
        // Id из внешних метаданных не превращается в путь без проверки.
        if (!IsValidQuarantineId(item.Id))
        {
            error = "Метаданные повреждены: некорректный идентификатор объекта.";
            return false;
        }

        var objectPath = ObjectPath(item.Id);
        if (!File.Exists(objectPath))
        {
            error = "Объект карантина не найден.";
            return false;
        }

        // Путь из метаданных — внешний вход: абсолютный путь обязателен, иначе
        // копия уйдёт относительно рабочей директории приложения.
        if (string.IsNullOrWhiteSpace(item.OriginalPath) || !Path.IsPathRooted(item.OriginalPath))
        {
            error = "Метаданные повреждены: некорректный исходный путь.";
            return false;
        }

        // Целостность: хэш объекта сверяется с вычисленным при изоляции.
        // Легаси-метаданные без ObjectSha256 (аудит 2, п. 26) больше не
        // восстанавливаются вслепую: текущий хэш объекта фиксируется в
        // метаданных (миграция), и все последующие восстановления проверяются.
        if (item.ObjectSha256.Length > 0)
        {
            var actualSha = ComputeSha256(objectPath);
            if (actualSha is null
                || !string.Equals(actualSha, item.ObjectSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "Хэш объекта не совпадает с записанным — файл повреждён или подменён.";
                return false;
            }
        }
        else
        {
            var migratedSha = ComputeSha256(objectPath);
            if (migratedSha is null)
            {
                error = "Объект карантина не читается.";
                return false;
            }

            item.ObjectSha256 = migratedSha;
            try
            {
                File.WriteAllText(MetadataPath(item.Id), JsonSerializer.Serialize(item, JsonOptions));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = "Не удалось зафиксировать хэш объекта в метаданных: " + exception.Message;
                return false;
            }
        }

        var target = item.OriginalPath;
        if (File.Exists(target))
        {
            target = Path.Combine(
                Path.GetDirectoryName(target) ?? ".",
                Path.GetFileNameWithoutExtension(target) + " (restored)" + Path.GetExtension(target));
        }

        try
        {
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);

                // Граница пути: каталог назначения мог быть перенаправлен junction/
                // symlink после изоляции — повышенный SCU писал бы вне ожидаемого
                // места. Фактический финальный путь обязан совпасть с запрошенным.
                if (!PathSafety.TryResolveFinalPath(directory, out var resolved)
                    || !string.Equals(resolved, Path.GetFullPath(directory).TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "Каталог назначения перенаправлен (reparse point) — восстановление отменено.";
                    return false;
                }
            }

            // Аудит идемпотентности: цель — восстановленный файл у пользователя.
            // Delete(metadata) не влияет на успех: сбой оставляет orphan-метаданные,
            // которые подчищаются reconciliation'ом при старте/повторе. А вот
            // сбой Delete(object) после Copy — полный откат: удаляем
            // восстановленный target, объект возвращается в карантин.
            File.Copy(objectPath, target, overwrite: false);
            try
            {
                File.Delete(objectPath);
            }
            catch (Exception deleteException)
            {
                try
                {
                    File.Delete(target);
                }
                catch (Exception rollbackException)
                {
                    // Откат тоже не удался: в отчёте — обе ошибки, файл-копия
                    // остаётся у пользователя как единственная копия данных.
                    error = "Объект не удалён из карантина, восстановленная копия оставлена: "
                            + deleteException.Message + " Откат: " + rollbackException.Message;
                    return false;
                }

                error = "Объект не удалён из карантина: " + deleteException.Message;
                return false;
            }

            try
            {
                File.Delete(MetadataPath(item.Id));
            }
            catch (Exception metadataException)
            {
                // Восстановление состоялось; orphan-метаданные не мешают
                // повторному Restore (он ответит «объект не найден») и будут
                // подчищены повторным DeletePermanently.
                _logger.Warn("QUARANTINE | restore | metadata cleanup failed | " + metadataException.Message);
            }

            return true;
        }
        catch (IOException exception)
        {
            error = exception.Message;
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    // Безвозвратное удаление: объект + метаданные. Вызывающая сторона обязана
    // запросить подтверждение (IConfirmDialogService, документ п. 46).
    public bool DeletePermanently(QuarantineItem item, out string error)
    {
        error = string.Empty;
        if (!IsValidQuarantineId(item.Id))
        {
            error = "Метаданные повреждены: некорректный идентификатор объекта.";
            return false;
        }

        try
        {
            var objectPath = ObjectPath(item.Id);
            if (File.Exists(objectPath))
            {
                File.Delete(objectPath);
            }

            File.Delete(MetadataPath(item.Id));
            return true;
        }
        catch (IOException exception)
        {
            error = exception.Message;
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    // Единственная граница допустимых id (аудит п. 4): только нормализованный
    // GUID "N" — 32 hex-символа без разделителей. Значение Id читается из JSON
    // метаданных (внешняя граница доверия) и больше нигде не превращается в
    // часть пути напрямую.
    internal static bool IsValidQuarantineId(string? id) =>
        !string.IsNullOrEmpty(id) && Guid.TryParseExact(id, "N", out _);

    private string ObjectPath(string id) => SafeChildPath(_objectsDir, id + ".qtn");

    private string MetadataPath(string id) => SafeChildPath(_metadataDir, id + ".json");

    // Defense-in-depth: собранный путь обязан остаться внутри ожидаемого каталога
    // (Path.Combine с "N"-GUID уже безопасен; проверка ловит рассинхрон границы).
    private string SafeChildPath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        return PathSafety.IsUnderDirectoryOrEqual(Path.GetFullPath(candidate), directory)
            ? candidate
            : throw new IOException("Путь карантина вышел за пределы каталога: " + fileName);
    }

    private static string? ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ComputeSha256(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ComputeSha256(Stream stream)
    {
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Объектное удаление исходника (SCU-03): FILE_DISPOSITION_INFO помечает
    // удаление по самому дескриптору, без повторного разрешения пути. Требует,
    // чтобы дескриптор был открыт с DELETE-доступом; откат удаления невозможен,
    // поэтому вызывается только после успешной сверки копии.
    private static void MarkFileForDeletion(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var info = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle, FileDispositionInfoClass, ref info, sizeof(int)))
        {
            throw new IOException(
                "Не удалось пометить исходник на удаление: "
                + new System.ComponentModel.Win32Exception().Message);
        }
    }

    // Открытие исходника с закреплением объекта (SCU-03): GENERIC_READ | DELETE
    // без FILE_SHARE_WRITE/FILE_SHARE_DELETE — до закрытия дескриптора файл
    // невозможно записать, переименовать, заменить или удалить из другого
    // процесса. FileAccess.Delete в .NET не существует, поэтому DELETE-доступ
    // даёт только прямое CreateFileW.
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenPinnedSourceHandle(string path)
    {
        var handle = CreateFileW(
            path, GenericRead | DeleteAccess, FileShareRead,
            IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(
                "Не удалось открыть исходный файл: "
                + new System.ComponentModel.Win32Exception().Message);
        }

        return handle;
    }

    private const int FileDispositionInfoClass = 4; // FILE_DISPOSITION_INFO
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;   // DELETE
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        // Win32 BOOL: 4 байта; размер фиксируем через sizeof(int) в вызове.
        public bool DeleteFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInfo fileDispositionInfo,
        int bufferSize);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
