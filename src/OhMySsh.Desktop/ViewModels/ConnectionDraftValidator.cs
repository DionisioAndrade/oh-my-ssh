using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Validates the connection form without saving credentials or starting I/O.</summary>
internal static class ConnectionDraftValidator
{
    public static bool TryCreate(
        string? name,
        string? host,
        string? portText,
        string? username,
        string? privateKeyPath,
        ConnectionProtocol protocol,
        string group,
        out ConnectionProfile? profile,
        out string error,
        string? timeoutText = null)
    {
        profile = null;
        error = string.Empty;
        var normalizedHost = host?.Trim() ?? string.Empty;
        var normalizedUsername = username?.Trim() ?? string.Empty;
        var normalizedKeyPath = privateKeyPath?.Trim() ?? string.Empty;

        if (normalizedHost.Length == 0)
        {
            error = "Enter the server hostname or IP address.";
            return false;
        }

        if (normalizedHost.Length > 255 || normalizedHost.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || normalizedHost.Contains("://", StringComparison.Ordinal) || normalizedHost.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0)
        {
            error = "Enter only the hostname or IP address, without a protocol, username, or path.";
            return false;
        }

        if (normalizedHost.StartsWith('[') || normalizedHost.EndsWith(']'))
        {
            if (!normalizedHost.StartsWith('[') || !normalizedHost.EndsWith(']')
                || !IPAddress.TryParse(normalizedHost[1..^1], out var bracketedAddress)
                || bracketedAddress.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "Use brackets only around a valid IPv6 address, without the port.";
                return false;
            }

            normalizedHost = normalizedHost[1..^1];
        }

        if (normalizedHost.Contains(':') && !IPAddress.TryParse(normalizedHost, out _))
        {
            error = "Enter the port separately from the server address.";
            return false;
        }

        if (!IPAddress.TryParse(normalizedHost, out _) && Uri.CheckHostName(normalizedHost) != UriHostNameType.Dns)
        {
            error = "Enter a valid IP address or hostname, such as server.example.com.";
            return false;
        }

        if (!int.TryParse(portText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            error = "Enter a port between 1 and 65535.";
            return false;
        }

        if (normalizedUsername.Length == 0 || normalizedUsername.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            error = "Enter the connection username without spaces or line breaks.";
            return false;
        }

        var normalizedName = string.IsNullOrWhiteSpace(name) ? normalizedHost : name.Trim();
        if (normalizedName.Length > 100 || normalizedName.Any(char.IsControl))
        {
            error = "Use a VPS name of up to 100 characters without line breaks.";
            return false;
        }

        if (!Enum.IsDefined(protocol))
        {
            error = "Choose a valid connection protocol.";
            return false;
        }

        if (normalizedKeyPath.Any(char.IsControl))
        {
            error = "The private-key path must not contain line breaks or control characters.";
            return false;
        }

        var timeout = 30;
        if (timeoutText is not null && (!int.TryParse(timeoutText.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out timeout) || timeout is < 1 or > 300))
        {
            error = "Enter a connection timeout between 1 and 300 seconds.";
            return false;
        }

        profile = new ConnectionProfile
        {
            Name = normalizedName,
            Host = normalizedHost,
            Port = port,
            ConnectionTimeoutSeconds = timeout,
            Username = normalizedUsername,
            Group = group,
            Protocol = protocol,
            PrivateKeyPath = protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp && normalizedKeyPath.Length > 0
                ? normalizedKeyPath
                : null
        };
        return true;
    }
}
