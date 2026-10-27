using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Desktop;
using OhMySsh.Desktop.Converters;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Desktop.Views;
using OhMySsh.Infrastructure.Files;
using OhMySsh.Infrastructure.Persistence;

namespace OhMySsh.Verify;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var localRoot = Path.Combine(repositoryRoot, ".local");
            var outputParent = args.Length == 0 ? localRoot : Path.GetFullPath(args[0], repositoryRoot);
            if (!IsWithin(outputParent, localRoot))
            {
                throw new ArgumentException("A saída da verificação deve ficar dentro de .local no repositório.");
            }

            var outputDirectory = Path.Combine(outputParent, $"verify-{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDirectory);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            var runner = new VerificationRunner(application, outputDirectory);
            var execution = runner.RunAsync();
            _ = execution.ContinueWith(
                _ => application.Dispatcher.BeginInvoke(application.Shutdown),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            application.Run();
            execution.GetAwaiter().GetResult();
            Console.WriteLine($"Relatório: {Path.Combine(outputDirectory, "verification.json")}");
            return runner.FailureCount == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "OhMySsh.Desktop")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Não foi possível localizar o repositório da verificação.");
    }

    private static bool IsWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class VerificationRunner(Application application, string outputDirectory)
{
    private readonly List<CheckResult> _checks = [];
    private readonly List<string> _screenshots = [];
    private readonly BindingErrorListener _bindingErrors = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private string _workspace = string.Empty;

    public int FailureCount => _checks.Count(check => !check.Passed);

    public async Task RunAsync()
    {
        await CheckAsync("XAML: recursos compartilhados", () =>
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/OhMySsh;component/Themes/Workbench.xaml")
            });
            application.Resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
            application.Resources["InverseBoolToVisibility"] = new InverseBooleanToVisibilityConverter();
            application.Resources["ProtocolToLabel"] = new ConnectionProtocolToLabelConverter();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingErrors);
            ThemeService.Current.Initialize(application, AppTheme.Light);
            return Task.CompletedTask;
        });

        await CheckAsync("Fixtures: dados isolados de QA", CreateFixturesAsync);
        await CheckAsync("Perfis: persistência e leitura de todos os campos", VerifyProfileRoundtripAsync);
        await CheckAsync("Perfis: cadastro, edição, busca, favoritos e exclusão", VerifyProfileCrudAsync);
        await CheckAsync("VPS: cadastro, edição e favorito sobrevivem ao reinício", VerifySavedVpsLifecycleAsync);
        await CheckAsync("VPS: comando SSH copiável com argumentos e apóstrofos protegidos", VerifySshCommandCopyAsync);
        await CheckAsync("Perfis: falha ao salvar preserva os dados carregados", VerifyFailedSaveAsync);
        await CheckAsync("Perfis: JSON inválido bloqueia alterações e preserva o arquivo", VerifyFailedLoadAsync);
        await CheckAsync("Arquivos: navegação real e falha sem perda da listagem", VerifyLocalNavigationAsync);
        await CheckAsync("Arquivos: navegação simultânea não mistura resultados", VerifyConcurrentNavigationAsync);
        await CheckAsync("Conexão manual: validação, sessão sem salvar e salvamento explícito", VerifyManualConnectionAsync);
        await CheckAsync("Conexão: gate, confirmação aguardada, falha, cancelamento e desconexão", VerifyConnectionSessionStateAsync);
        await CheckAsync("XAML: central de conexão vazia e salva nos dois temas", async () =>
        {
            await CaptureConnectionsPageAsync(false);
            await CaptureConnectionsPageAsync(true);
        });
        await CheckAsync("XAML: painel e workspace somente com sessão confirmada", CaptureConnectedWorkspaceAsync);
        await CheckAsync("Layout: central de conexão e workspace no tamanho mínimo", VerifyMinimumLayoutAsync);
        await CheckAsync("XAML: editor e validação de servidor", VerifyConnectionEditorAsync);
        await CheckAsync("XAML: editor de QA nos temas claro e escuro", CaptureEditorAsync);
        await CheckAsync("Temas e navegação: preservam perfis, seleção e arquivos", VerifyThemeNavigationStateAsync);
        await CheckAsync("Bindings WPF: nenhuma falha", () =>
        {
            Require(_bindingErrors.Messages.Count == 0, string.Join(Environment.NewLine, _bindingErrors.Messages));
            return Task.CompletedTask;
        });

        PresentationTraceSources.DataBindingSource.Listeners.Remove(_bindingErrors);
        var report = new
        {
            StartedAt = _startedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            OutputDirectory = outputDirectory,
            Passed = _checks.Count(check => check.Passed),
            Failed = FailureCount,
            Checks = _checks,
            Screenshots = _screenshots,
            BindingErrors = _bindingErrors.Messages,
            FixtureNotice = "Todos os servidores são fictícios em example.invalid. Sessões controladas de QA existem apenas no verificador; nenhuma conexão de rede foi iniciada."
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "verification.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var text = new StringBuilder();
        text.AppendLine($"oh-my-ssh — verificação inicial: {report.Passed} passaram, {report.Failed} falharam");
        foreach (var check in _checks)
        {
            text.AppendLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name} ({check.DurationMs} ms)");
            if (check.Error is not null)
            {
                text.AppendLine(check.Error);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "verification.txt"), text.ToString());
        Console.Write(text);
    }

    private async Task CreateFixturesAsync()
    {
        _workspace = Path.Combine(outputDirectory, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspace, "deploy"));
        Directory.CreateDirectory(Path.Combine(_workspace, "logs"));
        Directory.CreateDirectory(Path.Combine(_workspace, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, "README.md"), "# Workspace de QA\nArquivos fictícios para validar a interface.\n");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "deploy", "release.txt"), "qa-release-0.1.0\n");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "deploy.ps1"), "# Fixture de QA; não executada.\n");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "manifest.json"), "{\"environment\":\"qa\",\"version\":\"0.1.0\"}\n");
    }

    private async Task VerifyProfileRoundtripAsync()
    {
        var store = new JsonConnectionProfileStore(Path.Combine(outputDirectory, "roundtrip", "connections.json"));
        Require((await store.LoadAsync()).Count == 0, "Um arquivo ausente deve produzir uma lista vazia.");
        ConnectionProfile[] profiles = [CreateProfile(), CreateProfile() with
        {
            Id = Guid.NewGuid(), Name = "Arquivos de QA", Host = "files.example.invalid", Protocol = ConnectionProtocol.Ftps, Port = 21,
            PrivateKeyPath = null, IsFavorite = false
        }];
        await store.SaveAsync(profiles);
        Require((await store.LoadAsync()).SequenceEqual(profiles), "O roundtrip alterou campos dos perfis.");
        var original = await File.ReadAllTextAsync(store.StoragePath);
        await ExpectExceptionAsync<InvalidDataException>(() => store.SaveAsync([profiles[0], profiles[0]]));
        Require(await File.ReadAllTextAsync(store.StoragePath) == original, "Uma gravação inválida alterou o arquivo existente.");
        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(store.StoragePath)!, "*.tmp").Any(), "Restou um arquivo temporário após a gravação.");
    }

    private async Task VerifyProfileCrudAsync()
    {
        var store = new MemoryProfileStore();
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(store, interactions);
        Require(!viewModel.AddConnectionCommand.CanExecute(null), "O cadastro foi liberado antes de carregar o store.");
        await viewModel.InitializeAsync();
        Require(viewModel.ProfilesAvailable && viewModel.AddConnectionCommand.CanExecute(null), "O cadastro não foi liberado após uma leitura válida.");
        var profile = CreateProfile();
        interactions.EditResults.Enqueue(profile);
        await viewModel.AddConnectionCommand.ExecuteAsync();
        Require(viewModel.ConnectionCount == 1 && viewModel.SelectedConnection?.Id == profile.Id, "O cadastro não selecionou o perfil salvo.");
        Require(store.Profiles.Single() == profile, "O cadastro não foi persistido.");

        var second = profile with { Id = Guid.NewGuid(), Name = "Backup de QA", Host = "backup.example.invalid", IsFavorite = false };
        interactions.EditResults.Enqueue(second);
        await viewModel.AddConnectionCommand.ExecuteAsync();
        viewModel.SearchText = "EDGE.EXAMPLE";
        Require(viewModel.FilteredConnections.Cast<ConnectionProfile>().Single().Id == profile.Id, "A busca por host deve ignorar maiúsculas.");
        viewModel.SearchText = "";
        viewModel.FilterFavoritesCommand.Execute(null);
        Require(viewModel.FilteredConnections.Cast<ConnectionProfile>().Single().IsFavorite, "O filtro de favoritos incluiu outro perfil.");
        viewModel.FilterFavoritesCommand.Execute(null);
        viewModel.SelectedConnection = profile;
        var edited = profile with { Name = "Edge QA atualizado", Port = 2222 };
        interactions.EditResults.Enqueue(edited);
        await viewModel.EditConnectionCommand.ExecuteAsync();
        Require(viewModel.SelectedConnection == edited && store.Profiles.Any(item => item == edited), "A edição não preservou a seleção e a persistência.");
        viewModel.CopyEndpointCommand.Execute(null);
        Require(interactions.ClipboardText == "deploy@edge.example.invalid:2222", "O endereço copiado está incorreto.");
        var saveCount = store.SaveCalls;
        var messageCount = interactions.Messages.Count;
        await viewModel.ConnectCommand.ExecuteAsync();
        Require(!viewModel.HasActiveSession && !viewModel.IsConnecting && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
            "O adaptador padrão marcou uma conexão como ativa ou não exibiu o erro inline.");
        Require(interactions.Messages.Count == messageCount, "Uma conexão indisponível abriu um aviso modal em vez do erro inline.");
        Require(store.SaveCalls == saveCount, "Tentar conectar alterou os perfis salvos.");
        await viewModel.DeleteConnectionCommand.ExecuteAsync();
        Require(viewModel.ConnectionCount == 1 && store.Profiles.Single().Id == second.Id, "A exclusão não foi persistida.");
    }

    private async Task VerifyFailedSaveAsync()
    {
        var original = CreateProfile();
        var store = new MemoryProfileStore([original]) { FailSave = true };
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(store, interactions);
        await viewModel.InitializeAsync();
        viewModel.SearchText = "filtro que precisa permanecer";
        viewModel.FavoritesOnly = true;
        viewModel.SelectedConnection = original;
        var savedEvents = 0;
        viewModel.ProfileSaved += (_, _) => savedEvents++;
        interactions.EditResults.Enqueue(original with { Name = "Esta alteração não pode ser salva" });
        await ExpectExceptionAsync<IOException>(() => viewModel.EditConnectionCommand.ExecuteAsync());
        Require(viewModel.Connections.Single() == original, "Uma gravação falha alterou a lista exibida.");
        Require(viewModel.SelectedConnection == original && !viewModel.IsBusy, "Uma gravação falha perdeu a seleção ou deixou a janela ocupada.");
        Require(store.Profiles.Single() == original, "A fixture alterou o store apesar da falha simulada.");
        Require(viewModel.SearchText == "filtro que precisa permanecer" && viewModel.FavoritesOnly,
            "Uma edição que falhou limpou os filtros.");

        interactions.EditResults.Enqueue(original with { Id = Guid.NewGuid(), Name = "Cadastro que falha", IsFavorite = false });
        await ExpectExceptionAsync<IOException>(() => viewModel.AddConnectionCommand.ExecuteAsync());
        Require(viewModel.Connections.Single() == original && viewModel.SelectedConnection == original,
            "Um cadastro que falhou alterou os perfis ou a seleção.");
        Require(viewModel.SearchText == "filtro que precisa permanecer" && viewModel.FavoritesOnly && savedEvents == 0,
            "Um cadastro que falhou limpou filtros ou emitiu o evento de perfil salvo.");
    }

    private async Task VerifySavedVpsLifecycleAsync()
    {
        var storagePath = Path.Combine(outputDirectory, "vps-lifecycle", "connections.json");
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(new JsonConnectionProfileStore(storagePath), interactions);
        await viewModel.InitializeAsync();
        var original = CreateProfile() with
        {
            Name = "VPS · aplicação QA", Group = "VPS", Port = 22022, IsFavorite = false,
            PrivateKeyPath = @"C:\QA\keys\vps_ed25519"
        };
        var savedEvents = 0;
        viewModel.ProfileSaved += (_, _) => savedEvents++;
        viewModel.SearchText = "não corresponde à VPS cadastrada";
        viewModel.FavoritesOnly = true;
        interactions.EditResults.Enqueue(original);
        await viewModel.AddConnectionCommand.ExecuteAsync();
        Require(viewModel.SelectedConnection == original && viewModel.FilteredConnections.Cast<ConnectionProfile>().Single() == original,
            "Salvar uma VPS não a selecionou na lista visível.");
        Require(viewModel.SearchText.Length == 0 && !viewModel.FavoritesOnly && savedEvents == 1,
            "O cadastro salvo não limpou filtros ou não notificou o shell.");

        // A new store and view model represent closing and reopening the app.
        var restarted = CreateViewModel(new JsonConnectionProfileStore(storagePath), new RecordingInteractions());
        await restarted.InitializeAsync();
        Require(restarted.Connections.Single() == original && restarted.SelectedConnection == original,
            "Reabrir o aplicativo alterou os campos da VPS salva.");

        var edited = original with
        {
            Name = "VPS · aplicação QA revisada", Host = "reviewed.example.invalid", Username = "ops-build",
            Group = "VPS de QA", Port = 2222, PrivateKeyPath = @"C:\QA\keys\reviewed_ed25519"
        };
        viewModel.SelectedConnection = null;
        viewModel.SearchText = "outro filtro sem resultado";
        viewModel.FavoritesOnly = true;
        interactions.EditResults.Enqueue(edited);
        await viewModel.EditConnectionCommand.ExecuteAsync(original);
        Require(viewModel.SelectedConnection == edited && viewModel.SearchText.Length == 0 && !viewModel.FavoritesOnly && savedEvents == 2,
            "Editar por parâmetro não preservou o perfil alvo ou não o tornou visível.");

        restarted = CreateViewModel(new JsonConnectionProfileStore(storagePath), new RecordingInteractions());
        await restarted.InitializeAsync();
        Require(restarted.Connections.Single() == edited, "A edição de todos os campos não sobreviveu ao reinício.");
        await restarted.ToggleFavoriteCommand.ExecuteAsync(edited);
        var favorite = edited with { IsFavorite = true };
        Require(restarted.SelectedConnection == favorite, "Favoritar perdeu a seleção do perfil.");

        var deleteInteractions = new RecordingInteractions { AllowDelete = false };
        var deleteViewModel = CreateViewModel(new JsonConnectionProfileStore(storagePath), deleteInteractions);
        await deleteViewModel.InitializeAsync();
        Require(deleteViewModel.Connections.Single() == favorite, "O favorito não sobreviveu ao reinício.");
        var originalFile = await File.ReadAllBytesAsync(storagePath);
        await deleteViewModel.DeleteConnectionCommand.ExecuteAsync(favorite);
        Require(deleteInteractions.ConfirmDeleteCalls == 1 && deleteViewModel.Connections.Single() == favorite,
            "Cancelar a confirmação removeu o perfil da interface.");
        Require((await File.ReadAllBytesAsync(storagePath)).SequenceEqual(originalFile),
            "Cancelar a exclusão alterou o arquivo de perfis.");

        deleteInteractions.AllowDelete = true;
        // Deny replacement of the real JSON destination to exercise an actual
        // filesystem failure, rather than an in-memory store approximation.
        await using (var lockedFile = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failedToReplace = false;
            try
            {
                await deleteViewModel.DeleteConnectionCommand.ExecuteAsync(favorite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failedToReplace = true;
            }

            Require(failedToReplace, "A fixture não conseguiu impedir a substituição do arquivo de perfis.");
            Require(deleteViewModel.Connections.Single() == favorite && deleteViewModel.SelectedConnection == favorite && !deleteViewModel.IsBusy,
                "Uma falha real de exclusão alterou a lista/seleção ou deixou o shell ocupado.");
            Require((await File.ReadAllBytesAsync(storagePath)).SequenceEqual(originalFile),
                "Uma falha real de exclusão alterou o arquivo original.");
        }

        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(storagePath)!, "*.tmp").Any(),
            "Uma falha real de gravação deixou um arquivo temporário.");
        await deleteViewModel.DeleteConnectionCommand.ExecuteAsync(favorite);
        restarted = CreateViewModel(new JsonConnectionProfileStore(storagePath), new RecordingInteractions());
        await restarted.InitializeAsync();
        Require(restarted.Connections.Count == 0, "Uma exclusão confirmada reapareceu após reiniciar.");
    }

    private async Task VerifySshCommandCopyAsync()
    {
        var profile = CreateProfile() with
        {
            Username = "ops'build", Port = 2222, PrivateKeyPath = @"C:\QA\keys\O'Brien_ed25519"
        };
        var ftp = profile with { Id = Guid.NewGuid(), Protocol = ConnectionProtocol.Ftp, Port = 21, PrivateKeyPath = null };
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(new MemoryProfileStore([profile, ftp]), interactions);
        await viewModel.InitializeAsync();
        viewModel.SelectedConnection = ftp;
        Require(viewModel.CopySshCommand.CanExecute(profile), "O comando SSH deve aceitar um perfil SSH passado como parâmetro.");
        viewModel.CopySshCommand.Execute(profile);
        const string expected = "ssh -l 'ops''build' -p 2222 -i 'C:\\QA\\keys\\O''Brien_ed25519' -- 'edge.example.invalid'";
        Require(interactions.ClipboardText == expected, "O comando SSH perdeu usuário, porta, chave ou escape de apóstrofos para PowerShell.");
        Require(viewModel.SelectedConnection == ftp, "Copiar um comando de outro cartão alterou a seleção.");
        Require(!viewModel.CopySshCommand.CanExecute(ftp), "Um perfil FTP não deve produzir um comando SSH.");
        viewModel.CopySshCommand.Execute(ftp);
        Require(interactions.ClipboardText == expected, "Um perfil FTP sobrescreveu o comando SSH copiado.");

        viewModel.SelectConnectionCommand.Execute(profile);
        Require(viewModel.SelectedConnection == profile, "Selecionar um cartão não ativou o perfil correspondente.");
        var unknown = profile with { Id = Guid.NewGuid() };
        Require(!viewModel.SelectConnectionCommand.CanExecute(unknown), "Selecionar deve recusar perfis que não pertencem à coleção.");
    }

    private async Task VerifyFailedLoadAsync()
    {
        var storagePath = Path.Combine(outputDirectory, "invalid", "connections.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        const string invalidOriginal = "[{\"id\": \"perfil-recuperavel\", esta parte precisa de recuperação";
        await File.WriteAllTextAsync(storagePath, invalidOriginal);
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(new JsonConnectionProfileStore(storagePath), interactions);
        await viewModel.InitializeAsync();
        Require(!viewModel.ProfilesAvailable && viewModel.StatusKind == "Error", "Uma falha de leitura foi escondida pelo sucesso da navegação local.");
        Require(!viewModel.AddConnectionCommand.CanExecute(null)
            && !viewModel.EditConnectionCommand.CanExecute(null)
            && !viewModel.DeleteConnectionCommand.CanExecute(null), "Uma falha de leitura não bloqueou o CRUD.");
        interactions.EditResults.Enqueue(CreateProfile());
        await viewModel.AddConnectionCommand.ExecuteAsync();
        await viewModel.EditConnectionCommand.ExecuteAsync();
        await viewModel.DeleteConnectionCommand.ExecuteAsync();
        await viewModel.NavigateToAsync(_workspace);
        Require(viewModel.StatusKind == "Error", "Abrir uma pasta ocultou o erro no arquivo de conexões.");
        Require(interactions.EditCalls == 0, "Uma operação bloqueada abriu o editor.");
        Require(await File.ReadAllTextAsync(storagePath) == invalidOriginal, "O arquivo recuperável foi sobrescrito após falha de leitura.");
    }

    private async Task VerifyLocalNavigationAsync()
    {
        var viewModel = CreateViewModel(new MemoryProfileStore([CreateProfile()]), new RecordingInteractions());
        await viewModel.InitializeAsync();
        var selected = viewModel.SelectedConnection;
        await viewModel.NavigateToAsync(_workspace);
        Require(viewModel.LocalEntries.Count == 6, "A navegação não exibiu os arquivos da fixture.");
        Require(viewModel.LocalEntries.Take(3).All(entry => entry.IsDirectory), "As pastas devem aparecer antes dos arquivos.");
        var originalEntries = viewModel.LocalEntries.ToArray();
        await viewModel.NavigateToAsync(Path.Combine(_workspace, "pasta-inexistente"));
        Require(viewModel.LocalPath == _workspace && viewModel.LocalEntries.SequenceEqual(originalEntries), "Uma pasta inexistente alterou o caminho carregado ou a listagem.");
        Require(viewModel.SelectedConnection == selected, "Uma falha de navegação alterou o perfil selecionado.");
        await viewModel.OpenLocalEntryCommand.ExecuteAsync(originalEntries.First(entry => entry.Name == "deploy"));
        Require(viewModel.LocalEntries.Single().Name == "release.txt", "Abrir uma pasta não navegou para seu conteúdo.");
        await viewModel.GoUpCommand.ExecuteAsync();
        Require(viewModel.LocalPath == _workspace, "Voltar deve utilizar o caminho carregado.");
    }

    private async Task VerifyConcurrentNavigationAsync()
    {
        var delayedPath = Path.Combine(_workspace, "slow");
        var ignoredPath = Path.Combine(_workspace, "ignored");
        var files = new DelayedLocalFileService(delayedPath);
        var viewModel = new MainViewModel(new MemoryProfileStore(), files, new RecordingInteractions());
        await viewModel.InitializeAsync();
        var first = viewModel.NavigateToAsync(delayedPath);
        Require(viewModel.IsBusy, "A navegação pendente não bloqueou os comandos.");
        await viewModel.NavigateToAsync(ignoredPath);
        files.Completion.SetResult([new FileEntry { Name = "slow-result.txt", FullPath = Path.Combine(delayedPath, "slow-result.txt") }]);
        await first;
        Require(viewModel.LocalPath == delayedPath && viewModel.LocalEntries.Single().Name == "slow-result.txt", "A navegação aplicou uma listagem fora de ordem.");
        Require(!files.RequestedPaths.Contains(ignoredPath) && !viewModel.IsBusy, "Uma navegação concorrente foi iniciada ou deixou o shell ocupado.");
    }

    private async Task VerifyManualConnectionAsync()
    {
        var store = new MemoryProfileStore();
        var interactions = new RecordingInteractions();
        var service = new QaRemoteConnectionService();
        var viewModel = CreateViewModel(store, interactions, service);
        await viewModel.InitializeAsync();
        var savedEvents = 0;
        viewModel.ProfileSaved += (_, _) => savedEvents++;

        viewModel.ManualHost = string.Empty;
        viewModel.ManualUsername = "deploy";
        await viewModel.ManualConnectCommand.ExecuteAsync();
        Require(service.Requests.Count == 0 && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
            "Uma conexão manual sem servidor não foi recusada antes de chamar o adaptador.");
        viewModel.ManualHost = "ssh://manual.example.invalid";
        await viewModel.ManualConnectCommand.ExecuteAsync();
        Require(service.Requests.Count == 0 && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
            "O formulário manual aceitou um servidor com prefixo de protocolo.");
        viewModel.ManualHost = "manual.example.invalid";
        viewModel.ManualPort = "0";
        await viewModel.ManualConnectCommand.ExecuteAsync();
        Require(service.Requests.Count == 0 && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
            "O formulário manual aceitou uma porta fora dos limites.");

        viewModel.ManualName = string.Empty;
        viewModel.ManualPort = "2222";
        viewModel.ManualUsername = "ops";
        viewModel.ManualProtocol = ConnectionProtocol.Ssh;
        viewModel.ManualPrivateKeyPath = @"C:\QA\keys\manual_ed25519";
        Require(store.SaveCalls == 0 && service.Requests.Count == 0,
            "Preencher o formulário manual iniciou conexão ou persistiu dados.");
        await viewModel.ManualConnectCommand.ExecuteAsync();
        var request = service.Requests.Single();
        Require(viewModel.HasActiveSession && viewModel.ActiveConnection == request,
            "O formulário manual não ativou a sessão confirmada pelo adaptador de QA.");
        Require(request.Name == "manual.example.invalid" && request.Host == "manual.example.invalid"
            && request.Username == "ops" && request.Port == 2222 && request.Protocol == ConnectionProtocol.Ssh
            && request.PrivateKeyPath == @"C:\QA\keys\manual_ed25519" && request.Group == "Conexão rápida",
            "A solicitação manual perdeu dados ou não usou o servidor como nome opcional.");
        Require(store.SaveCalls == 0 && viewModel.Connections.Count == 0 && savedEvents == 0,
            "Conectar manualmente salvou um perfil sem pedido explícito.");

        await viewModel.DisconnectCommand.ExecuteAsync();
        Require(!viewModel.HasActiveSession && viewModel.ManualHost == "manual.example.invalid",
            "Desconectar não encerrou a sessão ou apagou o formulário manual.");
        await viewModel.SaveManualConnectionCommand.ExecuteAsync();
        var saved = store.Profiles.Single();
        Require(saved.Name == request.Host && saved.Host == request.Host && saved.Port == request.Port
            && saved.Username == request.Username && saved.Protocol == request.Protocol
            && saved.PrivateKeyPath == request.PrivateKeyPath && saved.Group == "VPS",
            "Salvar o formulário manual perdeu campos ou não criou o perfil VPS.");
        Require(!viewModel.HasActiveSession && service.Requests.Count == 1 && savedEvents == 1,
            "Salvar manualmente iniciou outra conexão ou não notificou o shell.");
        Require(interactions.Messages.Count == 0, "A validação/conexão manual abriu avisos modais inesperados.");
    }

    private async Task VerifyConnectionSessionStateAsync()
    {
        var profile = CreateProfile();
        var store = new MemoryProfileStore([profile]);
        var interactions = new RecordingInteractions();
        var completion = new TaskCompletionSource<IRemoteConnectionSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new QaRemoteConnectionService { ConnectHandler = (_, _) => completion.Task };
        var viewModel = CreateViewModel(store, interactions, service);
        await viewModel.InitializeAsync();
        var sessionEvents = 0;
        viewModel.ActiveSessionChanged += (_, _) => sessionEvents++;
        var window = new MainWindow(viewModel, initializeOnLoad: false);
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            await SettleLayoutAsync(window);
            RequireConnectionLanding(window);
            foreach (var page in new[] { "home", "files", "terminal", "transfers", "automations" })
            {
                window.NavigateTo(page);
                await SettleLayoutAsync(window);
                RequireConnectionLanding(window);
            }

            var connection = viewModel.ConnectCommand.ExecuteAsync(profile);
            Require(viewModel.IsConnecting && !viewModel.HasActiveSession && sessionEvents == 0,
                "O shell ativou uma sessão antes da confirmação assíncrona do adaptador.");
            window.NavigateTo("files");
            await SettleLayoutAsync(window);
            RequireConnectionLanding(window);
            var session = new QaRemoteConnectionSession(profile);
            completion.SetResult(session);
            await connection;
            await SettleLayoutAsync(window);
            Require(viewModel.HasActiveSession && viewModel.ActiveConnection == profile && !viewModel.IsConnecting && sessionEvents == 1,
                "Uma sessão confirmada não atualizou o estado de conexão.");
            Require((window.FindName("HomePage") as FrameworkElement)?.IsVisible == true,
                "Uma sessão confirmada não abriu o painel da VPS.");
            window.NavigateTo("files");
            await SettleLayoutAsync(window);
            Require((window.FindName("MainWorkspaceGrid") as FrameworkElement)?.IsVisible == true,
                "O workspace continuou bloqueado depois da confirmação da sessão.");
            await viewModel.DisconnectCommand.ExecuteAsync();
            await SettleLayoutAsync(window);
            Require(!viewModel.HasActiveSession && viewModel.ActiveConnection is null && session.DisposeCalls == 1 && sessionEvents == 2,
                "Desconectar não descartou a sessão confirmada exatamente uma vez.");
            RequireConnectionLanding(window);
            Require(viewModel.Connections.Single() == profile && store.SaveCalls == 0,
                "Conectar ou desconectar alterou os perfis salvos.");

            service.ConnectHandler = (_, _) => Task.FromException<IRemoteConnectionSession>(new IOException("Falha de conexão controlada pelo QA."));
            await viewModel.ConnectCommand.ExecuteAsync(profile);
            Require(!viewModel.HasActiveSession && !viewModel.IsConnecting && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
                "Uma falha de conexão não voltou ao estado desconectado com erro inline.");
            RequireConnectionLanding(window);
            var disconnectedSession = new QaRemoteConnectionSession(profile, isConnected: false);
            service.ConnectHandler = (_, _) => Task.FromResult<IRemoteConnectionSession>(disconnectedSession);
            await viewModel.ConnectCommand.ExecuteAsync(profile);
            Require(!viewModel.HasActiveSession && disconnectedSession.DisposeCalls == 1,
                "Uma sessão não conectada foi aceita ou não foi descartada.");

            var lateCompletion = new TaskCompletionSource<IRemoteConnectionSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.ConnectHandler = (_, _) => lateCompletion.Task; // Deliberately ignores cancellation.
            var pending = viewModel.ConnectCommand.ExecuteAsync(profile);
            Require(viewModel.IsConnecting && viewModel.CancelConnectionCommand.CanExecute(null),
                "Uma tentativa pendente não habilitou o cancelamento.");
            viewModel.CancelConnectionCommand.Execute(null);
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!viewModel.IsConnecting && !viewModel.HasActiveSession,
                "Cancelar aguardou indefinidamente o adaptador ou manteve uma sessão ativa.");
            var lateSession = new QaRemoteConnectionSession(profile);
            lateCompletion.SetResult(lateSession);
            await lateSession.Disposed.WaitAsync(TimeSpan.FromSeconds(3));
            Require(lateSession.DisposeCalls == 1 && !viewModel.HasActiveSession && sessionEvents == 2,
                "Uma sessão tardia após cancelamento foi ativada ou não foi descartada.");
            RequireConnectionLanding(window);
            Require(store.SaveCalls == 0 && interactions.Messages.Count == 0,
                "Falha ou cancelamento alterou perfis ou abriu avisos modais.");
        }
        finally
        {
            await viewModel.DisconnectCommand.ExecuteAsync();
            window.Close();
        }
    }

    private static void RequireConnectionLanding(MainWindow window)
    {
        Require((window.FindName("ConnectionsPage") as FrameworkElement)?.IsVisible == true,
            "A central de conexão não está visível sem uma sessão ativa.");
        foreach (var name in new[] { "HomePage", "MainWorkspaceGrid", "TerminalPage", "TransfersPage", "AutomationsPage" })
        {
            Require((window.FindName(name) as FrameworkElement)?.IsVisible == false,
                $"A página protegida {name} ficou visível sem uma sessão confirmada.");
        }
    }

    private async Task CaptureConnectedWorkspaceAsync()
    {
        var profiles = new[] { CreateProfile(), CreateProfile() with
        {
            Id = Guid.NewGuid(), Name = "Arquivos de QA", Host = "files.example.invalid", Username = "backup", Group = "Arquivos",
            Protocol = ConnectionProtocol.Ftps, Port = 21, IsFavorite = false, PrivateKeyPath = null
        } };
        var viewModel = CreateViewModel(new MemoryProfileStore(profiles), new RecordingInteractions(), new QaRemoteConnectionService());
        await viewModel.InitializeAsync();
        await viewModel.NavigateToAsync(_workspace);
        viewModel.SelectedConnection = viewModel.Connections.Single(profile => profile.Host == "edge.example.invalid");

        ThemeService.Current.Apply(AppTheme.Light);
        var window = new MainWindow(viewModel, initializeOnLoad: false);
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            await viewModel.ConnectCommand.ExecuteAsync();
            Require(viewModel.HasActiveSession, "A captura conectada não recebeu uma sessão de QA confirmada.");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                window.NavigateTo("home");
                await SettleLayoutAsync(window);
                var themeName = theme == AppTheme.Light ? "light" : "dark";
                Capture(window, $"connected-home-{themeName}.png");
                window.NavigateTo("files");
                await SettleLayoutAsync(window);
                Capture(window, $"connected-workspace-{themeName}.png");
                var files = window.FindName("LocalFilesGrid") as DataGrid
                    ?? throw new InvalidOperationException("A grade de arquivos locais não foi encontrada.");
                Require(!files.AutoGenerateColumns && files.Columns.Count == 3,
                    $"A troca para {theme} alterou as três colunas de arquivos locais.");
            }
        }
        finally
        {
            await viewModel.DisconnectCommand.ExecuteAsync();
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
        }
    }

    private async Task VerifyConnectionEditorAsync()
    {
        var editor = new ConnectionEditorWindow();
        PrepareHiddenWindow(editor);
        Exception? validationFailure = null;
        editor.Loaded += (_, _) => application.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var name = (TextBox)editor.FindName("NameTextBox");
                var host = (TextBox)editor.FindName("HostTextBox");
                var username = (TextBox)editor.FindName("UsernameTextBox");
                var save = (Button)editor.FindName("SaveButton");
                var validation = (FrameworkElement)editor.FindName("ValidationPanel");
                name.Text = "Perfil de validação QA";
                host.Text = "ssh://edge.example.invalid";
                username.Text = "deploy";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(editor.Profile is null && validation.Visibility == Visibility.Visible, "O editor aceitou um servidor com protocolo/caminho.");
                host.Text = "edge.example.invalid";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception exception)
            {
                validationFailure = exception;
                editor.Close();
            }
        });
        var result = editor.ShowDialog();
        if (validationFailure is not null)
        {
            throw validationFailure;
        }

        Require(result == true && editor.Profile?.Host == "edge.example.invalid", "O editor não retornou o perfil validado.");
        Require(editor.Profile?.Group == "VPS", "Uma VPS nova não recebeu o grupo padrão VPS.");
        await Task.CompletedTask;
    }

    private async Task CaptureConnectionsPageAsync(bool withProfiles)
    {
        var storagePath = Path.Combine(outputDirectory, withProfiles ? "vps-saved" : "vps-empty", "connections.json");
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(new JsonConnectionProfileStore(storagePath), interactions);
        await viewModel.InitializeAsync();
        await viewModel.NavigateToAsync(_workspace);
        ThemeService.Current.Apply(AppTheme.Light);
        var window = new MainWindow(viewModel, initializeOnLoad: false);
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            if (withProfiles)
            {
                // Save through the same command the home/editor flow uses;
                // the shell should reveal the saved VPS page on ProfileSaved.
                window.NavigateTo("home");
                interactions.EditResults.Enqueue(CreateProfile() with { Group = "VPS" });
                await viewModel.AddConnectionCommand.ExecuteAsync();
                interactions.EditResults.Enqueue(CreateProfile() with
                {
                    Id = Guid.NewGuid(), Name = "Backup · VPS de QA", Host = "backup.example.invalid",
                    Username = "backup", Group = "VPS", Port = 2222, IsFavorite = false, PrivateKeyPath = null
                });
                await viewModel.AddConnectionCommand.ExecuteAsync();
            }
            else
            {
                window.NavigateTo("connections");
            }

            var connectionsPage = window.FindName("ConnectionsPage") as FrameworkElement
                ?? throw new InvalidOperationException("A página de VPS salvas não foi encontrada.");
            var profilesList = window.FindName("SavedProfilesList") as ListBox
                ?? throw new InvalidOperationException("A lista de VPS salvas não foi encontrada.");
            var workspace = window.FindName("MainWorkspaceGrid") as FrameworkElement
                ?? throw new InvalidOperationException("O workspace de arquivos não foi encontrado.");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                await SettleLayoutAsync(window);
                Require(connectionsPage.IsVisible && !workspace.IsVisible,
                    "A página de VPS não ficou visível após navegar ou salvar o perfil.");
                Require(profilesList.Items.Count == (withProfiles ? 2 : 0),
                    "A lista de VPS não corresponde aos perfis reais carregados pela fixture.");
                var themeName = theme == AppTheme.Light ? "light" : "dark";
                Capture(window, $"connection-center-{(withProfiles ? "saved" : "empty")}-{themeName}.png");
            }

            if (!withProfiles && FindVisualChild<Expander>(window, item => item.Name == "QuickOptionsExpander") is { } advanced)
            {
                advanced.IsExpanded = true;
                // Let the native Fluent expansion animation reveal its content
                // before the optional visual snapshot is rendered.
                await Task.Delay(250);
                await SettleLayoutAsync(window);
                Capture(window, "connection-center-advanced-dark.png");
                advanced.IsExpanded = false;
            }
        }
        finally
        {
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
        }
    }

    private async Task VerifyMinimumLayoutAsync()
    {
        ThemeService.Current.Apply(AppTheme.Light);
        var viewModel = CreateViewModel(new MemoryProfileStore([CreateProfile()]), new RecordingInteractions(), new QaRemoteConnectionService());
        await viewModel.InitializeAsync();
        await viewModel.NavigateToAsync(_workspace);
        var window = new MainWindow(viewModel, initializeOnLoad: false)
        {
            Width = 1180,
            Height = 800
        };
        try
        {
            PrepareHiddenWindow(window);
            var navigation = window.FindName("NavigationLayout") as Grid
                ?? throw new InvalidOperationException("O layout de navegação não foi encontrado.");
            navigation.ColumnDefinitions[0].Width = new GridLength(300);
            window.Show();
            window.NavigateTo("connections");
            await SettleLayoutAsync(window);
            Capture(window, "minimum-connection-center.png");
            var hostBox = FindVisualChild<TextBox>(window, item => item.Name == "QuickHostBox")
                ?? throw new InvalidOperationException("O campo de conexão manual não foi encontrado.");
            RequireVisibleWithinWindow(window, hostBox, "servidor da conexão manual");
            foreach (var name in new[] { "QuickSaveButton", "QuickConnectButton" })
            {
                var action = FindVisualChild<Button>(window, item => item.Name == name)
                    ?? throw new InvalidOperationException($"A ação manual {name} não foi encontrada.");
                RequireVisibleWithinWindow(window, action, name);
            }

            await viewModel.ConnectCommand.ExecuteAsync();
            window.NavigateTo("files");
            await SettleLayoutAsync(window);
            Capture(window, "minimum-connected-workspace.png");
            foreach (var name in new[] { "RemoteFilesPanel", "TransferQueuePanel" })
            {
                var panel = window.FindName(name) as FrameworkElement
                    ?? throw new InvalidOperationException($"O painel {name} não foi encontrado.");
                RequireVisibleWithinWindow(window, panel, name);
            }
        }
        finally
        {
            await viewModel.DisconnectCommand.ExecuteAsync();
            window.Close();
        }
    }

    private async Task CaptureEditorAsync()
    {
        ThemeService.Current.Apply(AppTheme.Light);
        var editor = new ConnectionEditorWindow(CreateProfile());
        try
        {
            PrepareHiddenWindow(editor);
            editor.Show();
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                await SettleLayoutAsync(editor);
                Capture(editor, theme == AppTheme.Light ? "connection-editor-light.png" : "connection-editor-dark.png");
            }
        }
        finally
        {
            editor.Close();
            ThemeService.Current.Apply(AppTheme.Light);
        }
    }

    private async Task VerifyThemeNavigationStateAsync()
    {
        var profile = CreateProfile();
        var second = profile with { Id = Guid.NewGuid(), Name = "Backup de QA", Host = "backup.example.invalid", IsFavorite = false };
        var store = new MemoryProfileStore([profile, second]);
        var viewModel = CreateViewModel(store, new RecordingInteractions(), new QaRemoteConnectionService());
        await viewModel.InitializeAsync();
        await viewModel.NavigateToAsync(_workspace);
        await viewModel.ConnectCommand.ExecuteAsync(profile);
        viewModel.SelectedConnection = viewModel.Connections.Single(item => item.Id == second.Id);
        var profilesBefore = viewModel.Connections.ToArray();
        var filesBefore = viewModel.LocalEntries.ToArray();
        var selectedBefore = viewModel.SelectedConnection;
        var pathBefore = viewModel.LocalPath;
        var paletteColors = new List<Color>();
        ThemeService.Current.Apply(AppTheme.Light);
        var window = new MainWindow(viewModel, initializeOnLoad: false);
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            var workspace = window.FindName("MainWorkspaceGrid") as FrameworkElement
                ?? throw new InvalidOperationException("O workspace não foi encontrado.");
            var connectionsPage = window.FindName("ConnectionsPage") as FrameworkElement
                ?? throw new InvalidOperationException("A página de VPS salvas não foi encontrada.");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                Require(ThemeService.Current.Preference == theme && ThemeService.Current.IsDark == (theme == AppTheme.Dark),
                    "O tema aplicado não corresponde à preferência selecionada.");
                var background = application.TryFindResource("AppBackgroundBrush") as SolidColorBrush
                    ?? throw new InvalidOperationException("A paleta não definiu o fundo do aplicativo.");
                paletteColors.Add(background.Color);
                foreach (var page in new[] { "home", "connections", "files", "terminal", "transfers", "automations", "settings" })
                {
                    window.NavigateTo(page);
                    await SettleLayoutAsync(window);
                    Require(ThemeService.Current.Preference == theme,
                        $"A página {page} alterou a preferência de tema {theme}.");
                    Require(ReferenceEquals(window.DataContext, viewModel), "Navegar ou trocar o tema substituiu o view model.");
                    Require(viewModel.Connections.SequenceEqual(profilesBefore) && viewModel.SelectedConnection == selectedBefore,
                        $"A página {page} alterou os perfis ou a seleção no tema {theme}.");
                    Require(viewModel.LocalPath == pathBefore && viewModel.LocalEntries.SequenceEqual(filesBefore),
                        $"A página {page} alterou o caminho ou os arquivos no tema {theme}.");
                    Require(workspace.IsVisible == (page == "files"), $"A navegação para {page} não alternou a visibilidade do workspace.");
                    Require(connectionsPage.IsVisible == (page == "connections"), $"A navegação para {page} não alternou a visibilidade das VPS salvas.");

                    if (page == "settings")
                    {
                        Capture(window, theme == AppTheme.Light ? "main-settings-light.png" : "main-settings-dark.png");
                    }
                }
            }

            Require(paletteColors.Distinct().Count() == 2, "Os temas claro e escuro usaram a mesma cor de fundo.");
            Require(store.SaveCalls == 0, "Navegar ou trocar o tema gravou dados de conexões.");
        }
        finally
        {
            await viewModel.DisconnectCommand.ExecuteAsync();
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
        }
    }

    private MainViewModel CreateViewModel(IConnectionProfileStore store, RecordingInteractions interactions,
        IRemoteConnectionService? remoteConnections = null)
        => new(store, new RestrictedLocalFileService(outputDirectory), interactions, remoteConnections);

    private static ConnectionProfile CreateProfile() => new()
    {
        Name = "Edge · homologação QA", Host = "edge.example.invalid", Username = "deploy", Group = "Homologação",
        Protocol = ConnectionProtocol.Ssh, Port = 22, IsFavorite = true, PrivateKeyPath = @"C:\QA\keys\deploy_ed25519"
    };

    private async Task CheckAsync(string name, Func<Task> check)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await check();
            _checks.Add(new CheckResult(name, true, stopwatch.ElapsedMilliseconds, null));
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _checks.Add(new CheckResult(name, false, stopwatch.ElapsedMilliseconds, exception.ToString()));
            Console.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    private static void PrepareHiddenWindow(Window window)
    {
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
    }

    private static async Task SettleLayoutAsync(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Predicate<T> match) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T candidate && match(candidate))
            {
                return candidate;
            }

            if (FindVisualChild(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void RequireVisibleWithinWindow(Window window, FrameworkElement element, string name)
    {
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        Require(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0,
            $"O elemento {name} não está visível no tamanho mínimo.");
        Require(bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1,
            $"O elemento {name} ultrapassa os limites da janela mínima: {bounds}.");
    }

    private void Capture(Window window, string filename)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        Require(width > 0 && height > 0, "A janela não recebeu um tamanho válido.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(outputDirectory, filename);
        using var stream = File.Create(path);
        encoder.Save(stream);
        _screenshots.Add(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ExpectExceptionAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Era esperada uma exceção {typeof(TException).Name}.");
    }
}

internal sealed record CheckResult(string Name, bool Passed, long DurationMs, string? Error);

internal sealed class BindingErrorListener : TraceListener
{
    public List<string> Messages { get; } = [];
    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Messages.Add(message);
        }
    }

    public override void WriteLine(string? message) => Write(message);
}

internal sealed class MemoryProfileStore(IEnumerable<ConnectionProfile>? profiles = null) : IConnectionProfileStore
{
    public List<ConnectionProfile> Profiles { get; private set; } = profiles?.ToList() ?? [];
    public bool FailSave { get; set; }
    public int SaveCalls { get; private set; }
    public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ConnectionProfile>>(Profiles.ToArray());

    public Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profilesToSave, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        if (FailSave)
        {
            return Task.FromException(new IOException("Falha de gravação simulada pela verificação."));
        }

        Profiles = profilesToSave.ToList();
        return Task.CompletedTask;
    }
}

/// <summary>A QA-only transport seam. It never opens sockets or starts a process.</summary>
internal sealed class QaRemoteConnectionService : IRemoteConnectionService
{
    public List<ConnectionProfile> Requests { get; } = [];
    public Func<ConnectionProfile, CancellationToken, Task<IRemoteConnectionSession>>? ConnectHandler { get; set; }

    public Task<IRemoteConnectionSession> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        Requests.Add(profile);
        cancellationToken.ThrowIfCancellationRequested();
        return ConnectHandler?.Invoke(profile, cancellationToken)
            ?? Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(profile));
    }
}

/// <summary>Records session ownership and disposal without any remote resource.</summary>
internal sealed class QaRemoteConnectionSession(ConnectionProfile profile, bool isConnected = true) : IRemoteConnectionSession
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _isConnected = isConnected ? 1 : 0;
    private int _disposeCalls;

    public ConnectionProfile Profile { get; } = profile;
    public bool IsConnected => Volatile.Read(ref _isConnected) == 1;
    public int DisposeCalls => Volatile.Read(ref _disposeCalls);
    public Task Disposed => _disposed.Task;

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCalls);
        Interlocked.Exchange(ref _isConnected, 0);
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingInteractions : IDesktopInteractionService
{
    public Queue<ConnectionProfile?> EditResults { get; } = new();
    public List<string> Messages { get; } = [];
    public string? ClipboardText { get; private set; }
    public int EditCalls { get; private set; }
    public bool AllowDelete { get; set; } = true;
    public int ConfirmDeleteCalls { get; private set; }
    public ConnectionProfile? EditConnection(ConnectionProfile? existing)
    {
        EditCalls++;
        return EditResults.TryDequeue(out var profile) ? profile : null;
    }

    public bool ConfirmDelete(ConnectionProfile profile)
    {
        ConfirmDeleteCalls++;
        return AllowDelete;
    }
    public string? PickLocalFolder(string initialPath) => null;
    public void ShowInformation(string title, string message) => Messages.Add($"{title}: {message}");
    public void CopyToClipboard(string text) => ClipboardText = text;
}

/// <summary>Prevents even the initial listing from inspecting the user's folders.</summary>
internal sealed class RestrictedLocalFileService(string allowedRoot) : ILocalFileService
{
    private readonly LocalFileService _inner = new();
    public Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string path, CancellationToken cancellationToken = default)
        => Path.GetFullPath(path).StartsWith(Path.GetFullPath(allowedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? _inner.GetEntriesAsync(path, cancellationToken)
            : Task.FromResult<IReadOnlyList<FileEntry>>([]);
}

internal sealed class DelayedLocalFileService(string delayedPath) : ILocalFileService
{
    public TaskCompletionSource<IReadOnlyList<FileEntry>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> RequestedPaths { get; } = [];
    public Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string path, CancellationToken cancellationToken = default)
    {
        RequestedPaths.Add(path);
        return path == delayedPath ? Completion.Task : Task.FromResult<IReadOnlyList<FileEntry>>([]);
    }
}
