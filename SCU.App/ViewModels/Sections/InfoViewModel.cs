using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Models;

namespace SCU.ViewModels.Sections;

public partial class InfoViewModel : ObservableObject, IDisposable
{
    private readonly Logger _logger;
    private readonly SystemInfoService _systemInfoService;

    [ObservableProperty]
    private SystemInfo? _systemInfo;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public InfoViewModel(Logger logger, SystemInfoService systemInfoService)
    {
        _logger = logger;
        _systemInfoService = systemInfoService;
    }

	[RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (IsLoading)
            return;

        IsLoading = true;
        ErrorMessage = null;
        // Старые данные не сбрасываем: при быстрой отмене/возврате на раздел
        // пользователь не увидит мигание «пусто → данные».

        try
        {
            var result = await _systemInfoService.GetAsync(ct);

            if (result.IsSuccess)
            {
                SystemInfo = result.Value;
            }
            else
            {
                // Реальную причину сбоя сервис присылает в result.Message — показываем её,
                // общий текст только если сообщение пустое.
                ErrorMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? L.T("Неизвестная ошибка при получении информации о системе.")
                    : L.T(result.Message);
                _logger.Error("InfoViewModel: Ошибка загрузки SystemInfo");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Raw("InfoViewModel: Загрузка системной информации отменена.");
        }
        catch (Exception ex)
        {
            ErrorMessage = L.T("Внутренняя ошибка: {0}", ex.Message);
            _logger.Error($"InfoViewModel: Исключение при загрузке: {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        // Очистка ресурсов
    }
}