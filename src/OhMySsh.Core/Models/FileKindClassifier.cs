using System.Collections.Frozen;

namespace OhMySsh.Core.Models;

/// <summary>Classifies a single filename for icon selection, without accessing a file system.</summary>
public static class FileKindClassifier
{
    private static readonly FrozenDictionary<string, FileKind> NamedKinds = CreateKindMap(
        (FileKind.Code, ["Dockerfile", "Containerfile", "Makefile", "GNUmakefile", "CMakeLists.txt",
            "Jenkinsfile", "Justfile", "Vagrantfile", "Gemfile", "Rakefile"]),
        (FileKind.Configuration, [".env", ".envrc", ".gitignore", ".gitattributes", ".gitmodules",
            ".gitconfig", ".editorconfig", ".dockerignore", ".npmrc", ".yarnrc", ".nvmrc",
            ".babelrc", ".eslintrc", ".prettierrc", ".profile", ".bashrc", ".bash_profile",
            ".zshrc", ".zprofile", "Procfile"]),
        (FileKind.Text, ["README", "LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS",
            "CHANGELOG", "CHANGES", "INSTALL"]),
        (FileKind.Key, ["id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "id_xmss", "id_ecdsa_sk",
            "id_ed25519_sk", "authorized_keys", "known_hosts"]));

    private static readonly FrozenDictionary<string, FileKind> ExtensionKinds = CreateKindMap(
        (FileKind.Code, [".c", ".h", ".cc", ".cpp", ".cxx", ".c++", ".hh", ".hpp", ".hxx",
            ".h++", ".ipp", ".tpp", ".ixx", ".cppm", ".cs", ".csx", ".fs",
            ".fsx", ".vb", ".java", ".kt", ".kts", ".scala", ".groovy", ".go", ".rs",
            ".swift", ".m", ".mm", ".py", ".pyw", ".pyi", ".rb", ".php", ".phtml", ".pl", ".pm", ".lua",
            ".r", ".jl", ".dart", ".ex", ".exs", ".erl", ".hrl", ".clj", ".cljs", ".cljc",
            ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".cts", ".vue", ".svelte", ".astro",
            ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".sql", ".graphql",
            ".gql", ".proto", ".xaml", ".razor", ".cshtml", ".ipynb", ".sh", ".bash", ".zsh",
            ".fish", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".vbs", ".awk", ".sed"]),
        (FileKind.Configuration, [".json", ".jsonc", ".json5", ".yaml", ".yml", ".toml", ".ini",
            ".cfg", ".conf", ".config", ".properties", ".props", ".targets", ".resx", ".settings",
            ".manifest", ".xml", ".xsd", ".xsl", ".xslt", ".ps1xml", ".lock", ".service", ".desktop", ".env", ".edn", ".sln",
            ".slnx", ".csproj", ".fsproj", ".vbproj", ".vcxproj", ".vcproj", ".pubxml"]),
        (FileKind.Text, [".txt", ".text", ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdx", ".rst", ".adoc",
            ".asciidoc", ".rtf", ".log", ".tex", ".bib", ".doc", ".docx",
            ".docm", ".dot", ".dotx", ".odt", ".ott"]),
        (FileKind.Spreadsheet, [".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".ods", ".ots",
            ".csv", ".tsv"]),
        (FileKind.Presentation, [".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".pot", ".potx", ".odp", ".otp"]),
        (FileKind.Pdf, [".pdf"]),
        (FileKind.Archive, [".zip", ".7z", ".rar", ".tar", ".gz", ".gzip", ".tgz", ".bz",
            ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".zst", ".zstd", ".tzst", ".lz",
            ".lz4", ".lzma", ".z", ".br", ".cab", ".iso", ".jar", ".war", ".ear", ".nupkg"]),
        (FileKind.Image, [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".svgz",
            ".ico", ".avif", ".heic", ".heif", ".tif", ".tiff", ".jxl", ".psd", ".xcf",
            ".dng", ".cr2", ".nef"]),
        (FileKind.Audio, [".mp3", ".wav", ".flac", ".aac", ".ogg", ".oga", ".opus", ".m4a",
            ".aiff", ".aif", ".wma", ".mid", ".midi"]),
        (FileKind.Video, [".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".mpeg",
            ".mpg", ".ogv", ".3gp", ".mts", ".m2ts"]),
        (FileKind.Executable, [".exe", ".com", ".dll", ".msi", ".msix", ".msixbundle", ".appx",
            ".appxbundle", ".appimage", ".wasm"]),
        (FileKind.Database, [".db", ".db3", ".sqlite", ".sqlite3", ".mdb", ".accdb"]),
        (FileKind.Font, [".ttf", ".otf", ".woff", ".woff2", ".eot"]),
        (FileKind.Key, [".pem", ".key", ".pub", ".pfx", ".p12", ".ppk", ".der", ".crt", ".cer",
            ".csr"]));

    public static FileKind Classify(string? name, bool isDirectory = false)
    {
        if (isDirectory) return FileKind.Folder;
        if (string.IsNullOrEmpty(name)) return FileKind.Generic;
        if (NamedKinds.TryGetValue(name, out var namedKind)) return namedKind;
        if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) return FileKind.Configuration;
        if (name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Containerfile.", StringComparison.OrdinalIgnoreCase)) return FileKind.Code;

        // A remote name may contain a literal backslash, so do not apply local path rules.
        // The last suffix also recognizes compressed archives such as .tar.gz and .tar.bz2.
        var separator = name.LastIndexOf('.');
        return separator >= 0 && ExtensionKinds.TryGetValue(name[separator..], out var extensionKind)
            ? extensionKind
            : FileKind.Generic;
    }

    private static FrozenDictionary<string, FileKind> CreateKindMap(params (FileKind Kind, string[] Names)[] groups)
    {
        var kinds = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, names) in groups)
            foreach (var name in names)
                kinds.Add(name, kind);
        return kinds.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
