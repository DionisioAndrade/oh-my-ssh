using OhMySsh.Core.Localization;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Retains resource keys so an existing message follows the selected UI language.</summary>
internal sealed record UiMessage(string Key, params object?[] Arguments)
{
    public string Text => UiText.Format(Key, ResolveArguments(english: false));
    public string English => UiText.English(Key, ResolveArguments(english: true));

    private object?[] ResolveArguments(bool english) => Arguments.Select(argument => argument is UiMessage message
        ? english ? message.English : message.Text
        : argument).ToArray();
}
