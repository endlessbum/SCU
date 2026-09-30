using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SCU.Common;

namespace SCU.ViewModels.Sections;

// Карточка пользовательского скрипта (визуально как встроенные карточки SCU):
// заголовок, комментарий, необязательный информер ⓘ и кнопка запуска.
public sealed partial class UserScriptCardViewModel : ObservableObject
{
    public UserScriptCardViewModel(UserScriptData data)
    {
        Id = data.Id;
        Title = data.Title;
        Comment = data.Comment;
        Tooltip = data.Tooltip;
        _data = data;
    }

    private readonly UserScriptData _data;

    public string Id { get; }

    public string Title { get; }

    public string Comment { get; }

    public string Tooltip { get; }

    public bool HasTooltip => !string.IsNullOrEmpty(Tooltip);

    public UserScriptData Data => _data;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _resultText;
}

// Полоса пользовательских скриптов в открытом разделе: показывает карточки,
// назначенные этому разделу в мастере добавления.
public sealed class UserScriptsStripViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Action _refreshHandler;
    private int _sectionNumber = -1;

    public UserScriptsStripViewModel(MainViewModel main)
    {
        _main = main;
        _refreshHandler = () => Refresh(_sectionNumber);
    }

    // Подписка живёт только пока контрол в дереве (Attach/Detach из UserScriptsStrip):
    // view пересоздаётся при каждом применении меню, и постоянная подписка текла бы.
    public void Attach() => _main.UserScriptsChanged += _refreshHandler;

    public void Detach() => _main.UserScriptsChanged -= _refreshHandler;

    public ObservableCollection<UserScriptCardViewModel> Cards { get; } = [];

    public void Refresh(int sectionNumber)
    {
        _sectionNumber = sectionNumber;
        Cards.Clear();
        foreach (var card in _main.UserScripts.Where(card => card.SectionNumber == sectionNumber))
        {
            Cards.Add(new UserScriptCardViewModel(card.Data));
        }
    }

    public async void Run(UserScriptCardViewModel card)
    {
        // async void: исключение здесь уходит в DispatcherUnhandledException и роняет
        // приложение, поэтому гасим его локально тостом.
        try
        {
            if (_main.UserScripts.FirstOrDefault(item => item.Id == card.Id) is not { } liveCard)
            {
                return;
            }

            await _main.RunUserScriptAsync(liveCard).ConfigureAwait(true);
            card.ResultText = liveCard.ResultText;
            card.IsRunning = liveCard.IsRunning;
        }
        catch (Exception exception)
        {
            card.IsRunning = false;
            card.ResultText = L.T("Ошибка: {0}", exception.Message);
            AppNotificationCenter.Instance.Push(
                L.T("Скрипт не запущен"),
                L.T("Ошибка: {0}", exception.Message),
                AppNotificationKind.Danger);
        }
    }
}
