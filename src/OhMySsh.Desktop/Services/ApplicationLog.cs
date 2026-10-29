using System.IO;

namespace OhMySsh.Desktop.Services;

/// <summary>Writes English application events; callers must never pass credentials or raw exceptions.</summary>
internal static class ApplicationLog
{
    private static readonly object Gate = new();

    public static void Write(string category, string message)
    {
        try
        {
            var safeMessage = new string(message.Take(2048).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMySsh", "logs");
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "application.log"), $"{DateTimeOffset.UtcNow:O} [{category}] {safeMessage}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must not prevent recovery or expose the original failure.
        }
    }

    public static void WriteFailure(string operation, Exception exception)
        => Write("Error", $"{operation} failed ({exception.GetType().Name}).");
}
