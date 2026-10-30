namespace OhMySsh.Core.Localization;

/// <summary>Refreshes derived display properties when the interface language changes.</summary>
public interface IUiLanguageObserver
{
    void OnLanguageChanged();
}
