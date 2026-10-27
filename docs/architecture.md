# Estrutura inicial

## Dependências

`Desktop → Core` e `Desktop → Infrastructure → Core`.

O Core contém os modelos imutáveis de perfil/arquivo e os contratos de armazenamento e navegação. Não referencia WPF. A infraestrutura implementa esses contratos. A aplicação compõe as dependências no `App` e injeta o `MainViewModel` na janela principal.

## Perfis de VPS

`ConnectionsPage` é a tela de entrada: `ManualConnectionView` aparece acima da biblioteca persistida de servidores. `MainWorkspaceGrid` é a página de arquivos. Salvar um perfil abre explicitamente a biblioteca por meio do evento `ProfileSaved`. A inicialização carrega os dados sem disparar esse evento.

Comandos dos cartões recebem um perfil e resolvem seu ID contra a coleção atual, evitando que uma ação em um cartão altere outro servidor selecionado. Cadastro, edição, exclusão e favorito persistem o snapshot antes de atualizar a interface. Cadastro/edição removem filtros somente após o sucesso, para que o perfil salvo fique visível. Os testes verificam a persistência em arquivo real e o recarregamento em uma nova instância.

`SshCommandBuilder` gera um comando de OpenSSH para PowerShell com argumentos delimitados e sem execução de processos. Essa ação só está disponível para perfis SSH/SFTP. A montagem do comando não representa uma sessão remota nem modifica verificações de identidade do servidor.

## Estado da sessão

`IRemoteConnectionService` é o contrato de conexão; `IRemoteConnectionSession` representa a sessão confirmada e sua liberação assíncrona. `SelectedConnection` indica somente o perfil selecionado; `ActiveConnection` vem da sessão conectada e mantém seu próprio snapshot, mesmo se um perfil salvo for editado.

O ViewModel só publica `HasActiveSession` depois de aguardar o serviço e confirmar `IsConnected`. `ActiveSessionChanged` faz o shell abrir o painel da VPS após sucesso ou retornar à tela de conexão após desconectar. A navegação bloqueia páginas de trabalho sem sessão, inclusive chamadas programáticas e histórico. O serviço padrão indisponível não retorna sessões fictícias; sessões de QA existem exclusivamente no projeto Verify.

O formulário manual fornece um perfil temporário para conexão. Seu salvamento usa um comando explícito e o mesmo armazenamento de perfis; a tentativa de conexão, falha ou cancelamento não altera a biblioteca salva.

O MVVM mantém busca, seleção, persistência e navegação fora do XAML. O code-behind trata interações específicas de janela: foco, teclado, duplo clique, redimensionamento e bandeja. Os recursos visuais ficam em `Themes/Workbench.xaml`.

## Aparência Fluent

O aplicativo usa o `Application.ThemeMode` do WPF .NET 10 e preserva os templates dos controles em `PresentationFramework.Fluent`. Os estilos personalizados são nomeados e derivados dos estilos nativos. `ThemeService` mantém as cores próprias em `Colors.Light.xaml` e `Colors.Dark.xaml`, acessadas por `DynamicResource`, e observa mudanças do Windows quando a opção Sistema está ativa.

Não se define `Window.ThemeMode` individualmente nas janelas comuns: isso introduziria estilos locais com precedência sobre os estilos da aplicação. A navegação alterna as páginas WPF sem substituir o ViewModel ou perder a listagem de arquivos e os perfis. Os fundos da galeria são recursos locais incluídos na aplicação; nenhuma página ou conteúdo web é carregado em execução.

## Persistência

`JsonConnectionProfileStore` é uma implementação inicial sem dependências externas. Cada gravação é feita em um arquivo temporário no mesmo diretório e substitui o destino após a serialização e o flush. Falhas de leitura bloqueiam alterações na interface para evitar sobrescrever um arquivo recuperável com uma lista vazia. Uma única instância da aplicação por sessão evita escritores concorrentes no uso normal.

SQLite deverá ser introduzido quando histórico de transferências e execuções exigir consultas e transações entre entidades. A migração deve importar os perfis JSON, preservar o original e verificar os IDs. Credenciais não pertencem ao arquivo de perfis nem ao banco de histórico; a integração deverá usar o cofre do Windows.

## Próximas integrações

1. Adaptador SSH.NET para autenticação, execução de comandos, SFTP e túneis. A verificação da chave do host deve acontecer antes de aceitar uma sessão. Senhas e passphrases devem ser solicitadas e guardadas separadamente dos perfis.
2. Adaptador FluentFTP para FTP/FTPS, mantendo o protocolo e suas capacidades explícitos. FTPS usa TLS explícito na porta 21 como padrão do cadastro.
3. Componente dedicado de emulação VT/ANSI. O transporte de shell não substitui um emulador de terminal. Validar Unicode, PTY, `vim`, `tmux`, redimensionamento e múltiplas sessões antes de escolher o componente.
4. Fila de transferências com cancelamento, progresso e tratamento de arquivos parciais.
5. Executor de automações independente de abas e janelas, com etapas persistidas, timeout, histórico e política de repetição adequada a cada operação.

Não há adaptadores fictícios ou estados de conexão simulados no aplicativo. Os perfis utilizados nas capturas e verificações são fixtures do verificador, nunca são inseridos no armazenamento do usuário.
