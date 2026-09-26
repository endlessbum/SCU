using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SCU.Models;

public class ComponentItem : INotifyPropertyChanged
{
    private bool _isInstalled;
    private bool _isInstalling;
    private bool _isUninstalling;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // Компонент имеет тихое удаление (DirectX End-User Runtime — нет).
    public bool CanUninstall { get; set; }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled == value)
            {
                return;
            }

            _isInstalled = value;
            OnPropertyChanged();
        }
    }

    // П.22: установка выполняется — кнопка скрыта, виден круглый индикатор;
    // по завершении (успех → галочка, отмена/ошибка → кнопка возвращается).
    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (_isInstalling == value)
            {
                return;
            }

            _isInstalling = value;
            OnPropertyChanged();
        }
    }

    // Аналогично установке: во время тихого удаления кнопка «Удалить» скрыта,
    // виден круглый индикатор.
    public bool IsUninstalling
    {
        get => _isUninstalling;
        set
        {
            if (_isUninstalling == value)
            {
                return;
            }

            _isUninstalling = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
