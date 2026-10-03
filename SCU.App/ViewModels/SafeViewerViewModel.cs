using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Views.Controls;

namespace SCU.ViewModels;

// Элемент списка в режиме папки.
public sealed class SafeViewerEntry
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public string SizeText { get; init; } = string.Empty;
    public string ModifiedText { get; init; } = string.Empty;
}

// Безопасный просмотр (документ п. 22: «не исполнять сканируемый файл»):
// файлы и папки — только как данные, внутри приложения, с лимитами чтения.
// Открывается даже вредоносное содержимое и объекты карантина: доступ
// FileShare.ReadWrite|Delete, ничего не исполняется, пути не покидают процесс.
public partial class SafeViewerViewModel : ObservableObject
{
    private string _currentFile = string.Empty;

    [ObservableProperty]
    private bool _isFolderMode;

    [ObservableProperty]
    private string _locationText = string.Empty;

    [ObservableProperty]
    private string _infoText = string.Empty;

    [ObservableProperty]
    private string _verdictLine = string.Empty;

    [ObservableProperty]
    private string _noteText = string.Empty;

    [ObservableProperty]
    private string _hexText = string.Empty;

    [ObservableProperty]
    private string _textContent = string.Empty;

    [ObservableProperty]
    private string _stringsText = string.Empty;

    [ObservableProperty]
    private bool _hasText;

    [ObservableProperty]
    private bool _isHashComputing;

    public ObservableCollection<SafeViewerEntry> Entries { get; } = new();

    public string BannerText => L.T("Только чтение: содержимое не исполняется и не передаётся внешним программам.");

    // ---------- Точки входа (файл / папка / универсальный) ----------

    public void OpenFile(string path, string? verdictLine = null, string? note = null)
    {
        IsFolderMode = false;
        VerdictLine = verdictLine ?? string.Empty;
        NoteText = note ?? string.Empty;
        _currentFile = path;
        LocationText = path;

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            InfoText = L.T("Файл не найден.");
            HexText = TextContent = StringsText = string.Empty;
            HasText = false;
            return;
        }

        InfoText = L.T("Размер: {0:N0} байт", info.Length);
        ReadFileContent(path);
        StartHash(path, info.Length);
    }

    public void OpenFolder(string path)
    {
        IsFolderMode = true;
        VerdictLine = string.Empty;
        NavigateTo(path);
    }

    public void OpenPath(string path)
    {
        if (Directory.Exists(path))
        {
            OpenFolder(path);
        }
        else
        {
            OpenFile(path);
        }
    }

    // ---------- Навигация по папкам ----------

    [RelayCommand]
    private void OpenEntry(SafeViewerEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        OpenPath(entry.FullPath);
    }

    [RelayCommand]
    private void Up()
    {
        var parent = Path.GetDirectoryName(LocationText.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
        {
            NavigateTo(parent);
        }
    }

    private void NavigateTo(string path)
    {
        LocationText = path;
        Entries.Clear();

        DirectoryInfo[]? directories = null;
        FileInfo[]? files = null;
        try
        {
            var directory = new DirectoryInfo(path);
            directories = directory.GetDirectories();
            files = directory.GetFiles();
        }
        catch (UnauthorizedAccessException)
        {
            InfoText = L.T("Доступ к папке запрещён.");
            return;
        }
        catch (IOException)
        {
            InfoText = L.T("Папка недоступна.");
            return;
        }

        InfoText = L.T("Папок: {0} | Файлов: {1}", directories.Length, files.Length);

        foreach (var subDirectory in directories.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            Entries.Add(new SafeViewerEntry
            {
                Name = subDirectory.Name,
                FullPath = subDirectory.FullName,
                IsDirectory = true,
                SizeText = L.T("<папка>"),
                ModifiedText = subDirectory.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
            });
        }

        foreach (var file in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            Entries.Add(new SafeViewerEntry
            {
                Name = file.Name,
                FullPath = file.FullName,
                IsDirectory = false,
                SizeText = L.T("{0:N0} байт", file.Length),
                ModifiedText = file.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
            });
        }
    }

    // ---------- Чтение файла ----------

    private void ReadFileContent(string path)
    {
        byte[] content;
        long totalLength;
        try
        {
            // FileShare.ReadWrite|Delete: читаем даже залоченные/запущенные файлы,
            // ничего не блокируем и не меняем.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            totalLength = stream.Length;
            var toRead = (int)Math.Min(totalLength, SafeContent.MaxReadBytes);
            content = new byte[toRead];
            var read = 0;
            while (read < toRead)
            {
                var chunk = stream.Read(content, read, toRead - read);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }

            if (read < toRead)
            {
                Array.Resize(ref content, read);
            }
        }
        catch (IOException exception)
        {
            InfoText = L.T("Файл не читается: {0}", exception.Message);
            HexText = TextContent = StringsText = string.Empty;
            HasText = false;
            return;
        }

        if (totalLength > content.Length)
        {
            NoteText = string.IsNullOrEmpty(NoteText)
                ? L.T("Показаны первые {0:N0} байт из {1:N0}.", content.Length, totalLength)
                : NoteText + " " + L.T("Показаны первые {0:N0} байт из {1:N0}.", content.Length, totalLength);
        }

        HexText = SafeContent.ToHexDump(content.AsSpan(0, Math.Min(content.Length, SafeContent.HexPreviewBytes)));
        if (content.Length > SafeContent.HexPreviewBytes)
        {
            HexText += L.T("\n… hex-дамп ограничен первыми {0:N0} байтами.", SafeContent.HexPreviewBytes);
        }

        HasText = SafeContent.IsLikelyText(content);
        if (HasText)
        {
            TextContent = SafeContent.DecodeText(content);
        }
        else
        {
            TextContent = string.Empty;
            var strings = SafeContent.ExtractStrings(content);
            StringsText = strings.Count == 0
                ? L.T("Строки не найдены.")
                : string.Join("\n", strings);
        }
    }

    // Поколение фоновой hash-задачи (аудит 3, п. 8): открытие нового файла
    // отменяет предыдущий подсчёт; спиннер снимает только актуальная задача.
    private int _hashGeneration;
    private CancellationTokenSource? _hashCts;

    private void StartHash(string path, long length)
    {
        // SHA-256 считаем фоном и только для разумных размеров — просмотр
        // не должен блокироваться хешированием больших файлов.
        if (length > 200_000_000)
        {
            InfoText += " · " + L.T("SHA-256: — (файл слишком велик)");
            return;
        }

        // Предыдущий подсчёт (другой файл) больше не актуален: отменяем и
        // фиксируем новое поколение — его завершение не снимет спиннер за нас.
        _hashCts?.Cancel();
        _hashCts?.Dispose();
        _hashCts = new CancellationTokenSource();
        var cancellationToken = _hashCts.Token;
        var generation = ++_hashGeneration;

        IsHashComputing = true;
        Task.Run(async () =>
        {
            string? message = null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                var hex = Convert.ToHexString(hash).ToLowerInvariant();

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_currentFile == path && generation == _hashGeneration)
                    {
                        InfoText += " · SHA-256: " + hex;
                    }
                });
                return;
            }
            catch (OperationCanceledException)
            {
                return; // файл сменился — результат не нужен
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException or System.Security.SecurityException)
            {
                message = "SHA-256: — (файл не читается)";
            }
            catch (Exception exception)
            {
                // Аудит п. 10: faulted Task больше не остаётся необработанным.
                message = "SHA-256: — (ошибка вычисления)";
                Logger.CurrentRun.Error("SAFEVIEWER | hash failed | " + exception.Message);
            }

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_currentFile == path && generation == _hashGeneration && message is not null)
                {
                    InfoText += " · " + L.T(message);
                }

                // Спиннер снимает только актуальное поколение: завершение
                // устаревшего подсчёта не гасит индикатор нового (аудит 3, п. 8).
                if (generation == _hashGeneration)
                {
                    IsHashComputing = false;
                }
            });
        });
    }

    // ---------- Открытие окна ----------

    public static void ShowFile(string path, string? verdictLine = null, string? note = null)
    {
        var viewModel = new SafeViewerViewModel();
        viewModel.OpenFile(path, verdictLine, note);
        Show(viewModel);
    }

    public static void ShowFolder(string path)
    {
        var viewModel = new SafeViewerViewModel();
        viewModel.OpenFolder(path);
        Show(viewModel);
    }

    private static void Show(SafeViewerViewModel viewModel)
    {
        var window = new SafeViewerWindow(viewModel)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
                    ?? Application.Current?.MainWindow,
        };
        window.ShowDialog();
    }
}
