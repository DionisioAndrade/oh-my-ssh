using OhMySsh.Core.Models;

namespace OhMySsh.Core.Abstractions;

/// <summary>Session-owned single-file operations. Remote paths use POSIX separators.</summary>
/// <remarks>
/// A successful transfer task means staging has been finalized. Reported bytes alone do not indicate
/// completion. IsFinalizing marks the final commit boundary after stream flush/close and the final
/// cancellation check. Cancellation is honored before finalization; a commit already in progress is allowed
/// to finish, so callers must treat task success as authoritative even if their token was just canceled.
/// </remarks>
public interface IRemoteFileService
{
    Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string remotePath, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken = default);
    Task UploadAsync(string localPath, string remoteDestination, bool overwrite,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task DownloadAsync(string remoteFile, string localDestination, bool overwrite,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
}
