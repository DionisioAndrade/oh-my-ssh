using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;

namespace OhMySsh.Infrastructure.Connections;

/// <summary>Explicitly keeps the shell offline until a real protocol adapter is installed.</summary>
public sealed class UnavailableRemoteConnectionService : IRemoteConnectionService
{
    public Task<IRemoteConnectionSession> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IRemoteConnectionSession>(new NotSupportedException(
            "A conexão remota ainda não está disponível nesta versão. Você pode salvar a VPS para usar depois. Nenhuma conexão foi iniciada."));
    }
}
