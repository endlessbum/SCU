using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SCU.Common;
using SCU.Services;

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
    private int _sectionNumber = -1;

    public UserScriptsStripViewModel(MainViewModel main)
    {
        _main = main;
        main.UserScriptsChanged += () => Refresh(_sectionNumber);
    }

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
        if (_main.UserScripts.FirstOrDefault(item => item.Id == card.Id) is not { } liveCard)
        {
            return;
        }

        await _main.RunUserScriptAsync(liveCard).ConfigureAwait(true);
        card.ResultText = liveCard.ResultText;
        card.IsRunning = liveCard.IsRunning;
    }
}
