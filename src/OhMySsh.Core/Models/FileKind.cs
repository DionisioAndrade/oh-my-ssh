namespace OhMySsh.Core.Models;

/// <summary>A filename-based hint for file icons, without inspecting file contents.</summary>
public enum FileKind
{
    Folder,
    Generic,
    Code,
    Configuration,
    Text,
    Spreadsheet,
    Presentation,
    Pdf,
    Archive,
    Image,
    Audio,
    Video,
    Executable,
    Database,
    Font,
    Key
}
