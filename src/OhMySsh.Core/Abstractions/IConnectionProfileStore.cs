using OhMySsh.Core.Models;

namespace OhMySsh.Core.Abstractions;

/// <summary>Persists connection settings, excluding credentials.</summary>
public interface IConnectionProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default);
}
