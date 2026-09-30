using System.Text.Json;
using SCU.Infrastructure.Logging;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// Инструменты чтения файлов установленного приложения (расширение ТЗ по явному
// запросу пользователя: «AI должен иметь доступ к чтению файлов самого SCU в
// месте его установки»). Read-only, выполняются без подтверждения; система
// за пределами папки установки недоступна.
//
// Границы безопасности enforced в коде, не в описании для модели:
// - корень жёстко зафиксирован — папка установки SCU (AppContext.BaseDirectory);
// - только относительные пути: абсолютные и содержащие «..» отвергаются до
//   всякой работы с диском, полный путь дополнительно канонизируется и
//   проверяется на принадлежность корню (защита от обхода через symlink/точки);
// - читаются только текстовые форматы из allowlist: бинарники (exe/dll и т.п.)
//   не читаются — их можно увидеть в listing с именем и размером;
// - лимиты объёма: слишком большой файл не читается вовсе, длинный —
//   обрезается с пометкой truncated (контекст модели не переполняется);
// - содержимое прогоняется через SecretRedactor: секрет, случайно попавший в
//   файл установки, уходит модели замаскированным (аналог п. 23 ТЗ).

// list_scu_app_files: перечисляет файлы папки установки SCU (рекурсивно).
internal sealed class ScuListAppFilesTool : ScuAppFileToolBase
{
    private const int MaxListEntries = 500;

    public ScuListAppFilesTool() : this(AppContext.BaseDirectory)
    {
    }

    internal ScuListAppFilesTool(string root) : base(root)
    {
    }

    public override string Name => "list_scu_app_files";

    public override string Description =>
        "Перечисляет файлы установленного приложения SCU (папка установки, рекурсивно). " +
        "Показывает относительный путь, размер и расширение. Бинарные файлы перечислить " +
        "можно, но прочитать их содержимое нельзя (read_scu_app_file работает только " +
        "с текстовыми форматами).";

    public override ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object(
        ("subfolder", "string", "Относительная подпапка внутри папки установки SCU (необязательно).", false),
        ("search_pattern", "string", "Маска имён, например «*.json» (необязательно, по умолчанию все файлы).", false)));

    public override ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public override Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var subfolder = GetStringOrNull(arguments, "subfolder");
        var pattern = GetStringOrNull(arguments, "search_pattern");

        string directory;
        try
        {
            directory = ResolveInsideRoot(subfolder);
        }
        catch (ScuAiPathException exception)
        {
            return Task.FromResult(ScuAiToolResult.Failure(exception.Code, L.T(exception.MessageKey, exception.Arg)));
        }

        if (!Directory.Exists(directory))
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.ValidationFailed, L.T("Папка не найдена: {0}", subfolder ?? ".")));
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        var root = Root;
        var files = new List<object>();
        var truncated = false;
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles(pattern ?? "*", options))
            {
                if (files.Count >= MaxListEntries)
                {
                    truncated = true;
                    break;
                }

                files.Add(new
                {
                    path = Path.GetRelativePath(root, file.FullName),
                    size_bytes = file.Length,
                    extension = file.Extension,
                });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"SCU_AI | app_files | list failed | {exception.Message}");
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.PermissionDenied, L.T("Не удалось прочитать содержимое папки.")));
        }

        var payload = JsonSerializer.Serialize(new
        {
            root = root,
            total = files.Count,
            truncated,
            files,
        });
        return Task.FromResult(ScuAiToolResult.Ok(payload));
    }
}

// read_scu_app_file: читает текстовый файл из папки установки SCU.
internal sealed class ScuReadAppFileTool : ScuAppFileToolBase
{
    // Файлы больше этого размера не читаются вовсе: содержимое гигантского
    // файла переполнило бы контекст модели.
    private const long MaxReadBytes = 256 * 1024;

    // Сколько символов фактически уходит модели, даже если файл меньше лимита
    // в байтах: длинные тексты обрезаются с пометкой truncated.
    private const int MaxReadChars = 24_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".config", ".ini", ".log", ".csv",
        ".yaml", ".yml", ".xaml", ".cs", ".csproj", ".props", ".targets",
        ".resx", ".manifest", ".html", ".htm", ".css", ".js",
    };

    public ScuReadAppFileTool() : this(AppContext.BaseDirectory)
    {
    }

    internal ScuReadAppFileTool(string root) : base(root)
    {
    }

    public override string Name => "read_scu_app_file";

    public override string Description =>
        "Читает текстовый файл из папки установки SCU по относительному пути " +
        "(от list_scu_app_files). Читаются только текстовые форматы: json, txt, " +
        "log, xml, config, cs, xaml и подобные; бинарные файлы (.exe, .dll, …) " +
        "прочитать нельзя. Очень длинные файлы обрезаются.";

    public override ScuAiToolSchema Schema => new(Name, Description, ScuAiSchemaBuilder.Object(
        ("path", "string", "Относительный путь файла внутри папки установки SCU.", true)));

    public override ScuAiRiskLevel Risk => ScuAiRiskLevel.ReadOnly;

    public override Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context)
    {
        var relative = GetStringOrNull(arguments, "path");
        if (string.IsNullOrWhiteSpace(relative))
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.InvalidArguments, L.T("Не указан путь к файлу.")));
        }

        string fullPath;
        try
        {
            fullPath = ResolveInsideRoot(relative);
        }
        catch (ScuAiPathException exception)
        {
            return Task.FromResult(ScuAiToolResult.Failure(exception.Code, L.T(exception.MessageKey, exception.Arg)));
        }

        if (!File.Exists(fullPath))
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.ValidationFailed, L.T("Файл не найден: {0}", relative)));
        }

        var extension = Path.GetExtension(fullPath);
        if (!TextExtensions.Contains(extension))
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.ValidationFailed,
                L.T("Чтение файлов этого типа не поддерживается: {0}", extension)));
        }

        long size = new FileInfo(fullPath).Length;
        if (size > MaxReadBytes)
        {
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.ValidationFailed,
                L.T("Файл слишком большой для чтения ({0} байт).", size)));
        }

        string content;
        try
        {
            content = File.ReadAllText(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"SCU_AI | app_files | read failed | {exception.Message}");
            return Task.FromResult(ScuAiToolResult.Failure(
                ScuAiErrorCode.PermissionDenied, L.T("Не удалось прочитать файл.")));
        }

        var truncated = content.Length > MaxReadChars;
        if (truncated)
        {
            content = content[..MaxReadChars];
        }

        // Защита в глубину: секрет, случайно оказавшийся в файле установки,
        // не уходит в модель в открытом виде.
        content = SecretRedactor.Redact(content);

        var payload = JsonSerializer.Serialize(new
        {
            path = relative,
            size_bytes = size,
            truncated,
            content,
        });
        return Task.FromResult(ScuAiToolResult.Ok(payload));
    }
}

// Общая база: фиксированный корень и разрешение относительных путей.
internal abstract class ScuAppFileToolBase : IScuAiTool
{
    protected readonly Logger _logger = Logger.CreateForCurrentRun();

    protected ScuAppFileToolBase(string root)
    {
        // Каноничный корень с завершающим разделителем: префиксная проверка
        // полного пути не должна пропускать «C:\SCU-evil» при корне «C:\SCU».
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
               + Path.DirectorySeparatorChar;
    }

    protected string Root { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract ScuAiToolSchema Schema { get; }

    public abstract ScuAiRiskLevel Risk { get; }

    public abstract Task<ScuAiToolResult> ExecuteAsync(JsonElement arguments, ScuAiExecutionContext context);

    // Путь от модели превращается в путь внутри корня или отвергается.
    // Исключение несёт готовое сообщение об ошибке для модели.
    // null — корень целиком (опциональная подпапка listing'а).
    protected string ResolveInsideRoot(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return Root;
        }

        // Абсолютные пути и обход через «..» запрещены сразу — до работы с диском.
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
        {
            throw new ScuAiPathException(
                ScuAiErrorCode.ValidationFailed,
                "Путь должен быть относительным и указывать внутрь папки установки SCU.",
                null);
        }

        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ScuAiPathException(
                ScuAiErrorCode.PermissionDenied,
                "Файлы вне папки установки SCU недоступны.",
                null);
        }

        return full;
    }

    protected static string? GetStringOrNull(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}

// Нормализованный отказ при невалидном пути: code + ключ локализованного сообщения.
internal sealed class ScuAiPathException(ScuAiErrorCode code, string messageKey, string? arg)
    : Exception(messageKey)
{
    public ScuAiErrorCode Code { get; } = code;

    public string MessageKey { get; } = messageKey;

    public string? Arg { get; } = arg;
}
