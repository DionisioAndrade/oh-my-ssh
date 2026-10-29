namespace OhMySsh.Core.Models;

/// <summary>POSIX remote paths and strict single-component Windows download names.</summary>
public static class RemotePath
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Contains('\0'))
            throw UnsafePath();
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    public static string Combine(string directory, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
            throw UnsafePath();
        return Normalize(directory).TrimEnd('/') + "/" + name;
    }

    public static string GetParent(string path)
    {
        var normalized = Normalize(path);
        var separator = normalized.LastIndexOf('/');
        return separator <= 0 ? "/" : normalized[..separator];
    }

    public static string GetFileName(string path)
    {
        var normalized = Normalize(path);
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    public static string GetSafeLocalFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
            throw UnsafePath();
        var baseName = name.Split('.')[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (baseName.Length == 4 && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && baseName[3] is >= '1' and <= '9'))
            throw UnsafePath();
        return name;
    }

    private static RemoteFileException UnsafePath() => new(RemoteFileErrorCode.UnsafePath,
        "The path or filename is not safe for this operation. Choose a valid remote path or local filename.");
}
