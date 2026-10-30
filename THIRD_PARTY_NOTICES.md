# Third-party notices

## WPF Gallery — Microsoft WPF-Samples

The files `src/OhMySsh.Desktop/Assets/gallery-bloom-light.png` and `gallery-bloom-dark.png` were obtained from the [official WPF Gallery](https://github.com/microsoft/WPF-Samples/tree/main/Sample%20Applications/WPFGallery/Assets), with their filenames adapted for this project.

Original filenames: `win11-dashboard.light.png` and `win11-dashboard.dark.png`.

Copyright (c) 2015 Microsoft. MIT License. The full license text is provided in [LICENSE.WPF-Samples.txt](src/OhMySsh.Desktop/Assets/LICENSE.WPF-Samples.txt) and is included in the application output.

## Material Icon Theme — development file icons

The development-file icons are derived from [Material Icon Theme](https://github.com/material-extensions/vscode-material-icon-theme), pinned to revision [`23e86c06d09e92571ef027184c00cad78e8c827f`](https://github.com/material-extensions/vscode-material-icon-theme/tree/23e86c06d09e92571ef027184c00cad78e8c827f).

Copyright (c) 2025 Material Extensions. MIT License. The full, verbatim license is provided in [LICENSE.MaterialIconTheme.txt](src/OhMySsh.Desktop/Assets/LICENSE.MaterialIconTheme.txt) and is copied into the application output.

The original SVG files are retained in `src/OhMySsh.Desktop/Assets/DevelopmentIcons`. Their paths and view boxes are converted to native WPF drawings in `Themes/DevelopmentIcons.Light.xaml` and `Themes/DevelopmentIcons.Dark.xaml`, with color adaptations for readability in each theme. No SVG renderer or additional icon font is shipped.

The selected source icons are `python`, `java`, `csharp`, `markdown`, `javascript`, `typescript`, `html`, `css`, `json`, `xml`, `yaml`, `console`, `powershell`, `database`, `go`, `rust`, `php`, `ruby`, `c`, `cpp`, `visualstudio`, and `docker`. The console icon represents shell files, the database icon represents SQL files, and the Visual Studio icon represents .NET project and solution files.

## SSH.NET

The SSH transport uses [SSH.NET 2026.0.0](https://www.nuget.org/packages/SSH.NET/2026.0.0), licensed under the MIT License.

Copyright (c) Renci, Oleg Kapeljushnik, Gert Driesen and contributors. The verbatim license is provided in [LICENSE.SSH.NET.txt](src/OhMySsh.Infrastructure/Licenses/LICENSE.SSH.NET.txt).

The resolved runtime dependencies also include:

- BouncyCastle.Cryptography 2.7.0: [upstream license](src/OhMySsh.Infrastructure/Licenses/LICENSE.BouncyCastle.txt).
- Microsoft.Extensions.Logging.Abstractions 8.0.3 and Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2: [.NET MIT license](src/OhMySsh.Infrastructure/Licenses/LICENSE.Microsoft.Extensions.txt).
- Additional upstream notices shipped with Microsoft.Extensions.Logging.Abstractions: [third-party notices](src/OhMySsh.Infrastructure/Licenses/THIRD-PARTY-NOTICES.Microsoft.Extensions.Logging.txt).

These files are copied to the `Licenses` directory in build and publish output. Upstream license texts are preserved verbatim.

## SSH verification fixture

The optional local integration fixture uses [AsyncSSH](https://pypi.org/project/asyncssh/2.24.1/) 2.24.1 and its Python dependencies, installed into a project-local virtual environment. They are test tooling and are not packaged with the desktop application. See the upstream package metadata for their licenses.
