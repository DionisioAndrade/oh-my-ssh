# Third-party notices

## WPF Gallery — Microsoft WPF-Samples

The files `src/OhMySsh.Desktop/Assets/gallery-bloom-light.png` and `gallery-bloom-dark.png` were obtained from the [official WPF Gallery](https://github.com/microsoft/WPF-Samples/tree/main/Sample%20Applications/WPFGallery/Assets), with their filenames adapted for this project.

Original filenames: `win11-dashboard.light.png` and `win11-dashboard.dark.png`.

Copyright (c) 2015 Microsoft. MIT License. The full license text is provided in [LICENSE.WPF-Samples.txt](src/OhMySsh.Desktop/Assets/LICENSE.WPF-Samples.txt) and is included in the application output.

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
