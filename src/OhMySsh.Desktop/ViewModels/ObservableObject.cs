using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OhMySsh.Core.Localization;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Small binding foundation for the desktop shell.</summary>
public abstract class ObservableObject : INotifyPropertyChanged, IUiLanguageObserver
{
    protected ObservableObject() => UiText.Current.RegisterObserver(this);

    public event PropertyChangedEventHandler? PropertyChanged;

    public virtual void OnLanguageChanged() => OnPropertyChanged(string.Empty);

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
