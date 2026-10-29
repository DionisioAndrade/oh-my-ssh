using System.Text.Json.Serialization;

namespace OhMySsh.Core.Models;

public enum RemoteAuthenticationMethod
{
    Password,
    PrivateKey
}

/// <summary>Temporary authentication material. Never serialize or log this object.</summary>
public sealed class RemoteConnectionCredentials : IDisposable
{
    private string? _secret;

    public RemoteConnectionCredentials(RemoteAuthenticationMethod method, string secret, bool remember = false)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        ArgumentNullException.ThrowIfNull(secret);
        Method = method;
        _secret = secret;
        Remember = remember;
    }

    public RemoteAuthenticationMethod Method { get; }
    [JsonIgnore]
    public string Secret => _secret ?? throw new ObjectDisposedException(nameof(RemoteConnectionCredentials));
    public bool Remember { get; }

    // String copies made by native dialogs and SSH.NET cannot be cleared by this class.
    public void Dispose() => _secret = null;
    public override string ToString() => $"{nameof(RemoteConnectionCredentials)} ({Method}, redacted)";
}
