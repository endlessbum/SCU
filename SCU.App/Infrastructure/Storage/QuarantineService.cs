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
        _logger = logger ?? Logger.CreateForCurrentRun();
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
    public string GetObjectPath(string id) => Path.Combine(_objectsDir, id + ".qtn");

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
            File.Copy(source, objectPath, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Нечитаемый источник (отказ в доступе) даёт UnauthorizedAccessException ещё
            // до удаления оригинала — возвращаем ошибку вместо падения команды UI.
            error = exception.Message;
            return false;
        }

        try
        {
            // Проверка фиксации: размер копии совпадает с оригиналом.
            if (new FileInfo(objectPath).Length != new FileInfo(source).Length)
            {
                File.Delete(objectPath);
                error = "Размер копии не совпал с оригиналом.";
                return false;
            }

            var item = new QuarantineItem
            {
                Id = id,
                OriginalPath = source,
                Sha256 = detection.Sha256,
                ObjectSha256 = ComputeSha256(objectPath) ?? string.Empty,
                Verdict = detection.Verdict,
                RuleId = detection.RuleId,
                Description = detection.Description,
                QuarantinedAt = DateTime.Now,
                SizeBytes = new FileInfo(objectPath).Length,
            };
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(item, JsonOptions));

            // Метаданные записаны — оригинал можно удалять.
            File.Delete(source);
            return true;
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

    public IReadOnlyList<QuarantineItem> List()
    {
        var items = new List<QuarantineItem>();
        foreach (var metadataPath in Directory.EnumerateFiles(_metadataDir, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<QuarantineItem>(
                    File.ReadAllText(metadataPath), JsonOptions);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            catch (IOException)
            {
                // Повреждённый файл метаданных не должен ронять список.
            }
            catch (JsonException)
            {
            }
        }

        return items.OrderByDescending(item => item.QuarantinedAt).ToList();
    }

    // Восстановление: копия обратно по оригинальному пути; при занятом
    // месте — путь с суффиксом. Объект и метаданные удаляются при успехе.
    public bool Restore(QuarantineItem item, out string error)
    {
        error = string.Empty;
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

        // Целостность: хэш объекта сверяется с вычисленным при изоляции. Пустой
        // ObjectSha256 — старые метаданные без проверки (Sha256 детекции сверять
        // нельзя: у членов архива объектом является контейнер с другим хэшем).
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

            File.Copy(objectPath, target, overwrite: false);
            File.Delete(objectPath);
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

    // Безвозвратное удаление: объект + метаданные. Вызывающая сторона обязана
    // запросить подтверждение (IConfirmDialogService, документ п. 46).
    public bool DeletePermanently(QuarantineItem item, out string error)
    {
        error = string.Empty;
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

    private string ObjectPath(string id) => Path.Combine(_objectsDir, id + ".qtn");

    private string MetadataPath(string id) => Path.Combine(_metadataDir, id + ".json");

    private static string? ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
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
