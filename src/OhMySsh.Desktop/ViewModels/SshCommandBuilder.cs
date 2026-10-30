using System.Globalization;
using System.Text;
using OhMySsh.Core.Models;
using OhMySsh.Core.Localization;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Creates a clipboard command without starting a process or an SSH session.</summary>
public static class SshCommandBuilder
{
    public static string BuildPowerShell(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Protocol is not (ConnectionProtocol.Ssh or ConnectionProtocol.Sftp))
        {
            throw new ArgumentException(UiText.Get("Vm.Validation.SshProtocol"), nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.Username)
            || profile.Port is < 1 or > 65535)
        {
            throw new ArgumentException(UiText.Get("Vm.Validation.SshProfile"), nameof(profile));
        }

        // -l keeps the username separate from the destination, including IPv6.
        var host = profile.Host.Trim();
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        var command = new StringBuilder("ssh -l ")
            .Append(QuoteLiteral(profile.Username))
            .Append(" -p ")
            .Append(profile.Port.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            command.Append(" -i ").Append(QuoteLiteral(profile.PrivateKeyPath));
        }

        return command.Append(" -- ").Append(QuoteLiteral(host)).ToString();
    }

    private static string QuoteLiteral(string value)
    {
        var quoted = new StringBuilder("'");
        foreach (var character in value)
        {
            // Windows PowerShell 5.1 has legacy native-argument parsing for
            // embedded double quotes. Reject ambiguous values rather than
            // producing a command whose arguments could change when pasted.
            if (character == '"' || char.IsControl(character)
                || char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                throw new ArgumentException(UiText.Get("Vm.Validation.SshQuotes"));
            }

            // PowerShell also recognizes these typographic apostrophes as
            // single-quote delimiters, so duplicate them just like ASCII '.
            if (character is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
            {
                quoted.Append(character);
            }

            quoted.Append(character);
        }

        return quoted.Append('\'').ToString();
    }
}
