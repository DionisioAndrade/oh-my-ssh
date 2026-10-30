using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OhMySsh.Core.Models;
using OhMySsh.Core.Localization;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Validates the connection form without saving credentials or starting I/O.</summary>
internal static class ConnectionDraftValidator
{
    public static bool TryCreate(
        string? name, string? host, string? portText, string? username, string? privateKeyPath,
        ConnectionProtocol protocol, string group, out ConnectionProfile? profile, out string error,
        string? timeoutText = null)
    {
        var valid = TryCreateWithErrorKey(name, host, portText, username, privateKeyPath, protocol, group,
            out profile, out var errorKey, timeoutText);
        error = valid ? string.Empty : UiText.Get(errorKey);
        return valid;
    }

    public static bool TryCreateWithErrorKey(
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
            error = "Vm.Validation.HostRequired";
            return false;
        }

        if (normalizedHost.Length > 255 || normalizedHost.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || normalizedHost.Contains("://", StringComparison.Ordinal) || normalizedHost.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0)
        {
            error = "Vm.Validation.HostOnly";
            return false;
        }

        if (normalizedHost.StartsWith('[') || normalizedHost.EndsWith(']'))
        {
            if (!normalizedHost.StartsWith('[') || !normalizedHost.EndsWith(']')
                || !IPAddress.TryParse(normalizedHost[1..^1], out var bracketedAddress)
                || bracketedAddress.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "Vm.Validation.Ipv6";
                return false;
            }

            normalizedHost = normalizedHost[1..^1];
        }

        if (normalizedHost.Contains(':') && !IPAddress.TryParse(normalizedHost, out _))
        {
            error = "Vm.Validation.SeparatePort";
            return false;
        }

        if (!IPAddress.TryParse(normalizedHost, out _) && Uri.CheckHostName(normalizedHost) != UriHostNameType.Dns)
        {
            error = "Vm.Validation.ValidHost";
            return false;
        }

        if (!int.TryParse(portText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            error = "Vm.Validation.Port";
            return false;
        }

        if (normalizedUsername.Length == 0 || normalizedUsername.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            error = "Vm.Validation.Username";
            return false;
        }

        var normalizedName = string.IsNullOrWhiteSpace(name) ? normalizedHost : name.Trim();
        if (normalizedName.Length > 100 || normalizedName.Any(char.IsControl))
        {
            error = "Vm.Validation.Name";
            return false;
        }

        if (!Enum.IsDefined(protocol))
        {
            error = "Vm.Validation.Protocol";
            return false;
        }

        if (normalizedKeyPath.Any(char.IsControl))
        {
            error = "Vm.Validation.KeyPath";
            return false;
        }

        var timeout = 30;
        if (timeoutText is not null && (!int.TryParse(timeoutText.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out timeout) || timeout is < 1 or > 300))
        {
            error = "Vm.Validation.Timeout";
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
