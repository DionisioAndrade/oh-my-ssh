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
        out string error)
    {
        profile = null;
        error = string.Empty;
        var normalizedHost = host?.Trim() ?? string.Empty;
        var normalizedUsername = username?.Trim() ?? string.Empty;
        var normalizedKeyPath = privateKeyPath?.Trim() ?? string.Empty;

        if (normalizedHost.Length == 0)
        {
            error = "Informe o nome ou endereço IP do servidor.";
            return false;
        }

        if (normalizedHost.Length > 255 || normalizedHost.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || normalizedHost.Contains("://", StringComparison.Ordinal) || normalizedHost.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0)
        {
            error = "Use apenas o nome do servidor ou endereço IP, sem protocolo, usuário ou caminho.";
            return false;
        }

        if (normalizedHost.StartsWith('[') || normalizedHost.EndsWith(']'))
        {
            if (!normalizedHost.StartsWith('[') || !normalizedHost.EndsWith(']')
                || !IPAddress.TryParse(normalizedHost[1..^1], out var bracketedAddress)
                || bracketedAddress.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "Use colchetes apenas ao redor de um endereço IPv6 válido, sem a porta.";
                return false;
            }

            normalizedHost = normalizedHost[1..^1];
        }

        if (normalizedHost.Contains(':') && !IPAddress.TryParse(normalizedHost, out _))
        {
            error = "Informe a porta no campo Porta, separada do endereço do servidor.";
            return false;
        }

        if (!IPAddress.TryParse(normalizedHost, out _) && Uri.CheckHostName(normalizedHost) != UriHostNameType.Dns)
        {
            error = "Informe um endereço IP ou domínio válido, como servidor.exemplo.com.";
            return false;
        }

        if (!int.TryParse(portText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            error = "Informe uma porta entre 1 e 65535.";
            return false;
        }

        if (normalizedUsername.Length == 0 || normalizedUsername.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            error = "Informe o usuário da conexão, sem espaços ou quebras de linha.";
            return false;
        }

        var normalizedName = string.IsNullOrWhiteSpace(name) ? normalizedHost : name.Trim();
        if (normalizedName.Length > 100 || normalizedName.Any(char.IsControl))
        {
            error = "Use um nome para a VPS com até 100 caracteres, sem quebras de linha.";
            return false;
        }

        if (!Enum.IsDefined(protocol))
        {
            error = "Escolha um protocolo de conexão válido.";
            return false;
        }

        if (normalizedKeyPath.Any(char.IsControl))
        {
            error = "O caminho da chave privada não pode conter quebras de linha ou caracteres de controle.";
            return false;
        }

        profile = new ConnectionProfile
        {
            Name = normalizedName,
            Host = normalizedHost,
            Port = port,
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
