using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;

namespace OhMySsh.Infrastructure.Files;

/// <summary>Enumerates a single directory on a worker thread.</summary>
public sealed class LocalFileService : ILocalFileService
{
    public Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run<IReadOnlyList<FileEntry>>(() => ReadEntries(path, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<FileEntry> ReadEntries(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        var entries = new List<FileEntry>();

        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                entries.Add(new FileEntry
                {
                    Name = item.Name,
                    FullPath = item.FullName,
                    IsDirectory = isDirectory,
                    Size = !isDirectory && item is FileInfo file ? file.Length : 0,
                    LastModified = item.LastWriteTime
                });
            }
            catch (FileNotFoundException)
            {
                // Items may disappear between enumeration and metadata access.
            }
            catch (DirectoryNotFoundException)
            {
                // A concurrent move should not fail the entire listing.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return entries.OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
