using OhMySsh.Core.Models;

namespace OhMySsh.Core.Abstractions;

/// <summary>Reads immediate children of a local directory without blocking the UI thread.</summary>
public interface ILocalFileService
{
    Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string path, CancellationToken cancellationToken = default);
}
