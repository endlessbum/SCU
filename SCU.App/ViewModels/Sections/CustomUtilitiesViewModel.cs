using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.Common;
using SCU.ViewModels.Sections;

namespace SCU.ViewModels.Sections;

// Содержимое пользовательской вкладки меню (разделы ≥ 100, «Редактирование
// меню»): список назначенных утилит с кнопкой запуска и целевым состоянием
// тумблеров. Строки — те же BatchUtilityRow, что в списке «Главной».
public sealed partial class CustomUtilitiesViewModel : ObservableObject
{
    private readonly Logger _logger;

    public CustomUtilitiesViewModel(int sectionNumber, Logger logger)
    {
        SectionNumber = sectionNumber;
        _logger = logger;
    }

    public int SectionNumber { get; }

    public string Title =>
        Application.Current?.TryFindResource("S_SectionTitle" + SectionNumber) as string
        ?? string.Empty;

    public ObservableCollection<BatchUtilityRow> Rows { get; } = [];

    // Пересборка содержимого после изменений в «Редактировании меню».
    // utils — упорядоченный список id из модели меню.
    public void Rebuild(IReadOnlyList<string> utils, Func<string, BatchUtility?> resolve)
    {
        Rows.Clear();
        foreach (var id in utils)
        {
            if (resolve(id) is { } utility)
            {
                Rows.Add(new BatchUtilityRow(utility, isIncluded: false));
            }
        }
    }

    [RelayCommand]
    private async Task RunRowAsync(BatchUtilityRow? row)
    {
        if (row is null || row.Utility.Run is null)
        {
            return;
        }

        try
        {
            var status = await row.Utility.Run().ConfigureAwait(true);
            row.ResultText = status ?? string.Empty;
            _logger.Info("CUSTOM | run | " + row.Utility.Id + " | " + (status ?? ""));
        }
        catch (Exception exception)
        {
            row.ResultText = L.T("Ошибка: {0}", exception.Message);
            _logger.Error("CUSTOM | run failed | " + row.Utility.Id + " | " + exception);
        }
    }
}
