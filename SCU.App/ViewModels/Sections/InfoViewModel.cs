using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.Models;
using SCU.Services;

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
                // Используем общий текст, чтобы не зависеть от свойств класса Result
                ErrorMessage = "Неизвестная ошибка при получении информации о системе.";
                _logger.Error("InfoViewModel: Ошибка загрузки SystemInfo");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Raw("InfoViewModel: Загрузка системной информации отменена.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Внутренняя ошибка: {ex.Message}";
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