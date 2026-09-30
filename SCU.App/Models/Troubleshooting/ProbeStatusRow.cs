using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SCU.Models;

public enum ProbeState
{
    Pending,
    Running,
    Ok,
    Failed
}

/// <summary>
/// Строка прогресса одной проверки в UI: «Проверка сети… ✓».
/// Ошибка одного probe не останавливает весь запуск.
/// </summary>
public partial class ProbeStatusRow : ObservableObject
{
    [ObservableProperty]
    private string title;

    [ObservableProperty]
    private ProbeState state = ProbeState.Pending;

    public ProbeStatusRow(string title) => Title = title;

    public string StateText => State switch
    {
        ProbeState.Pending => "…",
        ProbeState.Running => "▸",
        ProbeState.Ok => "✓",
        _ => "✗"
    };

    public Brush StateBrush => State switch
    {
        ProbeState.Ok => new SolidColorBrush(Color.FromRgb(0x37, 0x9E, 0x37)),
        ProbeState.Failed => new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
        _ => new SolidColorBrush(Colors.Gray)
    };

    partial void OnStateChanged(ProbeState value) => OnPropertyChanged(nameof(StateText));
}
