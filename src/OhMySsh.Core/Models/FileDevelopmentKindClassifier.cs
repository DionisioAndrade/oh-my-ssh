using System.Collections.Frozen;

namespace OhMySsh.Core.Models;

/// <summary>Identifies development-file icon hints from a single filename, without reading its contents.</summary>
public static class FileDevelopmentKindClassifier
{
    private static readonly FrozenDictionary<string, FileDevelopmentKind> NamedKinds = CreateKindMap(
        (FileDevelopmentKind.Docker, ["Dockerfile", "Containerfile", ".dockerignore", "docker-compose.yml",
            "docker-compose.yaml", "compose.yml", "compose.yaml"]),
        (FileDevelopmentKind.Shell, [".bashrc", ".bash_profile", ".bash_logout", ".profile", ".zshrc",
            ".zprofile", ".zshenv", ".zlogin", ".zlogout", ".envrc"]),
        (FileDevelopmentKind.Ruby, ["Gemfile", "Rakefile", ".irbrc"]));

    private static readonly FrozenDictionary<string, FileDevelopmentKind> ExtensionKinds = CreateKindMap(
        (FileDevelopmentKind.Python, [".py", ".pyw", ".pyi", ".ipynb"]),
        (FileDevelopmentKind.Java, [".java"]),
        (FileDevelopmentKind.CSharp, [".cs", ".csx"]),
        (FileDevelopmentKind.Markdown, [".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdx"]),
        (FileDevelopmentKind.JavaScript, [".js", ".jsx", ".mjs", ".cjs"]),
        (FileDevelopmentKind.TypeScript, [".ts", ".tsx", ".cts"]),
        (FileDevelopmentKind.Html, [".html", ".htm", ".xhtml"]),
        (FileDevelopmentKind.Css, [".css", ".scss", ".sass", ".less"]),
        (FileDevelopmentKind.Json, [".json", ".jsonc", ".json5"]),
        (FileDevelopmentKind.Xml, [".xml", ".xaml", ".xsd", ".xsl", ".xslt", ".resx", ".props",
            ".targets", ".manifest", ".ps1xml", ".vcxproj", ".vcproj"]),
        (FileDevelopmentKind.Yaml, [".yaml", ".yml"]),
        (FileDevelopmentKind.Shell, [".sh", ".bash", ".zsh", ".fish"]),
        (FileDevelopmentKind.PowerShell, [".ps1", ".psm1", ".psd1"]),
        (FileDevelopmentKind.Sql, [".sql"]),
        (FileDevelopmentKind.Go, [".go"]),
        (FileDevelopmentKind.Rust, [".rs"]),
        (FileDevelopmentKind.Php, [".php", ".phtml"]),
        (FileDevelopmentKind.Ruby, [".rb"]),
        (FileDevelopmentKind.C, [".c", ".h"]),
        (FileDevelopmentKind.CPlusPlus, [".cc", ".cpp", ".cxx", ".c++", ".hh", ".hpp", ".hxx",
            ".h++", ".ipp", ".tpp", ".ixx", ".cppm"]),
        (FileDevelopmentKind.DotNetProject, [".csproj", ".fsproj", ".vbproj", ".sln", ".slnx"]),
        (FileDevelopmentKind.Docker, [".dockerfile", ".containerfile"]));

    public static FileDevelopmentKind Classify(string? name, bool isDirectory = false)
    {
        if (isDirectory || string.IsNullOrEmpty(name)) return FileDevelopmentKind.None;
        if (NamedKinds.TryGetValue(name, out var namedKind)) return namedKind;
        if (name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Containerfile.", StringComparison.OrdinalIgnoreCase)) return FileDevelopmentKind.Docker;

        // Treat the name as one component, including literal backslashes from remote servers.
        var separator = name.LastIndexOf('.');
        return separator >= 0 && ExtensionKinds.TryGetValue(name[separator..], out var extensionKind)
            ? extensionKind
            : FileDevelopmentKind.None;
    }

    private static FrozenDictionary<string, FileDevelopmentKind> CreateKindMap(
        params (FileDevelopmentKind Kind, string[] Names)[] groups)
    {
        var kinds = new Dictionary<string, FileDevelopmentKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, names) in groups)
            foreach (var name in names)
                kinds.Add(name, kind);
        return kinds.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
