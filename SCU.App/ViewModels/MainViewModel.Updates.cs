using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Interop;
using SCU.Models;
using SCU.ViewModels.Sections;
using SCU.Views.Controls;

namespace SCU.ViewModels;

// П. 13 аудита: зона ответственности «проверка обновлений SCU».
public partial class MainViewModel
{
    // ===================== Проверка обновлений =====================

    private readonly UpdateCheckService _updateCheckService = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _isCheckingUpdates;

    // До первой ручной проверки строка не пустует: плейсхолдер-подсказка,
    // заменяется результатом проверки (или тихой фоновой проверки).
    [ObservableProperty]
    private string _updateStatusText = L.T("Нажмите «Проверить» для обновления актуальной информации");

    private bool CanCheckForUpdates() => !IsCheckingUpdates;

    // Единовременность проверок: тихая и ручная не выполняются параллельно
    // (иначе при доступном обновлении приходят два одинаковых уведомления).
    private bool _updateCheckRunning;

    // Уведомление о новой версии показывается один раз за сеанс.
    private bool _updateNotified;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateCheckRunning)
        {
            return;
        }

        IsCheckingUpdates = true;
        UpdateStatusText = L.T("Проверка наличия обновлений…");
        try
        {
            await ApplyUpdateCheckAsync(quiet: false).ConfigureAwait(true);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    // Тихая автопроверка: без статусных строк, уведомление только при новой версии.
    private async Task CheckForUpdatesQuietAsync()
    {
        // Дать окну и фоновым инициализациям завершиться — проверка не важнее UI.
        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        if (_updateCheckRunning)
        {
            return;
        }

        await ApplyUpdateCheckAsync(quiet: true).ConfigureAwait(true);
    }

    private async Task ApplyUpdateCheckAsync(bool quiet)
    {
        _updateCheckRunning = true;
        try
        {
            await ApplyUpdateCheckCoreAsync(quiet).ConfigureAwait(true);
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private async Task ApplyUpdateCheckCoreAsync(bool quiet)
    {
        var result = await _updateCheckService.CheckAsync().ConfigureAwait(true);
        if (!result.Success)
        {
            _logger.Warn("UPDATE | check failed | " + result.Error);
            if (!quiet)
            {
                UpdateStatusText = L.T("Не удалось проверить обновления: {0}", result.Error);
            }

            return;
        }

        if (result.HasUpdate)
        {
            UpdateStatusText = L.T("Доступна новая версия: SCU {0} (установлена {1}).",
                result.LatestVersion, result.CurrentVersion);
            if (!_updateNotified)
            {
                _updateNotified = true;
                AppNotificationCenter.Instance.Push(
                    L.T("Доступна новая версия SCU"),
                    L.T("Установлена {0}, доступна {1}. Откройте страницу релизов, чтобы обновиться.",
                        result.CurrentVersion, result.LatestVersion),
                    AppNotificationKind.Info,
                    result.ReleaseUrl);
            }

            _logger.Info($"UPDATE | available | {result.LatestVersion} > {result.CurrentVersion}");
            return;
        }

        _logger.Info("UPDATE | up to date | " + result.CurrentVersion);
        if (!quiet)
        {
            UpdateStatusText = L.T("Обновление не требуется: у вас актуальная версия ({0}).", result.CurrentVersion);
        }
    }
}
