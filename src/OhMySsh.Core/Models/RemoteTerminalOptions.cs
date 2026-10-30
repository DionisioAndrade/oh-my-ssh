namespace OhMySsh.Core.Models;

/// <summary>The terminal identity and cell dimensions requested before the shell starts.</summary>
public sealed record RemoteTerminalOptions(string TerminalType = "xterm-256color", int Columns = 80, int Rows = 24)
{
    public const int MinimumColumns = 2;
    public const int MaximumColumns = 500;
    public const int MinimumRows = 2;
    public const int MaximumRows = 300;

    public void Validate()
    {
        // TERM is a protocol identifier, not server-controlled display text or shell input.
        if (string.IsNullOrEmpty(TerminalType) || TerminalType.Length > 64
            || TerminalType.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new RemoteTerminalException(RemoteTerminalErrorCode.InvalidSettings, "The terminal type is invalid.");
        }

        ValidateSize(Columns, Rows);
    }

    public static void ValidateSize(int columns, int rows)
    {
        if (columns is < MinimumColumns or > MaximumColumns || rows is < MinimumRows or > MaximumRows)
        {
            throw new RemoteTerminalException(RemoteTerminalErrorCode.InvalidSettings, "The terminal dimensions are invalid.");
        }
    }
}
