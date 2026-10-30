using System.IO;
using OhMySsh.Infrastructure.Persistence;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private async Task VerifyProfileReplacementAsync()
    {
        var directory = Path.Combine(outputDirectory, "profile-replacement");
        var storagePath = Path.Combine(directory, "connections.json");
        var store = new JsonConnectionProfileStore(storagePath);
        var original = CreateProfile();
        var edited = original with { Name = "Updated QA profile", IsFavorite = !original.IsFavorite };
        await store.SaveAsync([original]);
        var originalBytes = await File.ReadAllBytesAsync(storagePath);

        // This reader deliberately withholds delete sharing from the destination.
        // Its short lifetime exercises recovery from a real replacement denial.
        Task pendingSave;
        await using (var lockedFile = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            pendingSave = store.SaveAsync([edited]);
            await Task.Delay(100);
            if (pendingSave.IsCompleted)
            {
                // Preserve the actual filesystem error in the report if retries are absent.
                await pendingSave;
            }

            Require(!pendingSave.IsCompleted, "A destination lock did not keep the save pending.");
            Require((await File.ReadAllBytesAsync(storagePath)).SequenceEqual(originalBytes),
                "A pending replacement changed the original profile file.");
        }

        await pendingSave.WaitAsync(TimeSpan.FromSeconds(5));
        Require((await new JsonConnectionProfileStore(storagePath).LoadAsync()).Single() == edited,
            "A save did not recover after the destination lock was released.");
        RequireNoProfileTemporaryFiles(directory);
        var editedBytes = await File.ReadAllBytesAsync(storagePath);

        // A lock that outlives the retry budget must remain an observable failure.
        await using (var lockedFile = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await ExpectProfileStorageFailureAsync(() => store.SaveAsync([original]).WaitAsync(TimeSpan.FromSeconds(5)));
            Require((await File.ReadAllBytesAsync(storagePath)).SequenceEqual(editedBytes),
                "An exhausted replacement retry changed the existing profile file.");
            RequireNoProfileTemporaryFiles(directory);
        }

        using (var cancellation = new CancellationTokenSource())
        {
            await using (var lockedFile = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                pendingSave = store.SaveAsync([original], cancellation.Token);
                await Task.Delay(100);
                if (pendingSave.IsCompleted)
                {
                    await pendingSave;
                }

                Require(!pendingSave.IsCompleted, "The save stopped retrying before cancellation was requested.");
                cancellation.Cancel();
                await ExpectExceptionAsync<OperationCanceledException>(() => pendingSave.WaitAsync(TimeSpan.FromSeconds(5)));
                Require((await File.ReadAllBytesAsync(storagePath)).SequenceEqual(editedBytes),
                    "Canceling replacement changed the original profile file.");
                RequireNoProfileTemporaryFiles(directory);
            }
        }

        await store.SaveAsync([original]);
        Require((await new JsonConnectionProfileStore(storagePath).LoadAsync()).Single() == original,
            "A canceled replacement left the store unusable.");
        originalBytes = await File.ReadAllBytesAsync(storagePath);

        var attributes = File.GetAttributes(storagePath);
        try
        {
            File.SetAttributes(storagePath, attributes | FileAttributes.ReadOnly);
            await ExpectProfileStorageFailureAsync(() => store.SaveAsync([edited]).WaitAsync(TimeSpan.FromSeconds(5)));
            Require((await File.ReadAllBytesAsync(storagePath)).SequenceEqual(originalBytes)
                && File.GetAttributes(storagePath).HasFlag(FileAttributes.ReadOnly),
                "A save bypassed a read-only destination or changed its contents.");
            RequireNoProfileTemporaryFiles(directory);
        }
        finally
        {
            File.SetAttributes(storagePath, attributes);
        }

        var directoryDestination = Path.Combine(directory, "directory-destination");
        Directory.CreateDirectory(directoryDestination);
        var invalidStore = new JsonConnectionProfileStore(directoryDestination);
        await ExpectProfileStorageFailureAsync(() => invalidStore.SaveAsync([edited]).WaitAsync(TimeSpan.FromSeconds(5)));
        Require(Directory.Exists(directoryDestination) && !Directory.EnumerateFileSystemEntries(directoryDestination).Any(),
            "A save changed a directory used as the destination.");
        RequireNoProfileTemporaryFiles(directory);

        await ExpectExceptionAsync<ArgumentException>(() =>
            new JsonConnectionProfileStore(Path.Combine(directory, "invalid\0destination.json")).SaveAsync([edited]));
        Require((await new JsonConnectionProfileStore(storagePath).LoadAsync()).Single() == original,
            "A permanent storage failure changed a previously saved profile.");
    }

    private static async Task ExpectProfileStorageFailureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        throw new InvalidOperationException("Expected a filesystem persistence error.");
    }

    private static void RequireNoProfileTemporaryFiles(string directory)
        => Require(!Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "A completed, failed, or canceled profile save left a temporary file.");
}
