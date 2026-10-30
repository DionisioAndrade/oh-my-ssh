using System.IO;
using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Only allowlisted text derived from error types and codes may leave the transfer boundary.</summary>
internal sealed record TransferFailureDetails(string Summary, string Reason, string SuggestedAction, string DiagnosticMessage)
{
    internal static readonly TransferFailureDetails Generic = new(
        "Não foi possível concluir a transferência",
        "O servidor ou o sistema local não concluiu a operação.",
        "Verifique a conexão, o espaço livre e as permissões. Confira o destino antes de tentar novamente.",
        "The transfer failed. Check the connection, available space, and file permissions before retrying.");

    internal static TransferFailureDetails FromException(Exception exception) => exception switch
    {
        RemoteFileException { Code: RemoteFileErrorCode.PermissionDenied } or UnauthorizedAccessException => PermissionDenied,
        RemoteFileException { Code: RemoteFileErrorCode.NotFound } or FileNotFoundException or DirectoryNotFoundException => NotFound,
        RemoteFileException { Code: RemoteFileErrorCode.AlreadyExists } => AlreadyExists,
        RemoteFileException { Code: RemoteFileErrorCode.ConnectionLost } => ConnectionLost,
        RemoteFileException { Code: RemoteFileErrorCode.Unavailable, RequiresReconnect: true } => ChannelVerification,
        RemoteFileException { Code: RemoteFileErrorCode.Unavailable } => Unavailable,
        RemoteFileException { Code: RemoteFileErrorCode.UnsafePath } or ArgumentException or PathTooLongException => UnsafePath,
        RemoteFileException { Code: RemoteFileErrorCode.UnsupportedReplacement } => UnsupportedReplacement,
        RemoteFileException { RequiresReconnect: true } => ConnectionLost,
        RemoteConnectionException { Code: RemoteConnectionErrorCode.Timeout } or TimeoutException => Timeout,
        RemoteConnectionException { Code: RemoteConnectionErrorCode.HostKeyChanged or RemoteConnectionErrorCode.HostKeyRejected } => ChannelVerification,
        RemoteConnectionException { Code: RemoteConnectionErrorCode.AuthenticationFailed or RemoteConnectionErrorCode.PrivateKeyInvalid } => Authentication,
        RemoteConnectionException => ConnectionLost,
        IOException io when (io.HResult & 0xffff) is 0x27 or 0x70 => DiskFull,
        IOException => LocalIo,
        _ => Generic
    };

    private static readonly TransferFailureDetails PermissionDenied = new(
        "Acesso negado",
        "A conta não tem permissão para ler a origem ou gravar no destino.",
        "Verifique as permissões do arquivo e das pastas, ou escolha outro destino antes de tentar novamente.",
        "Transfer access was denied. Check source and destination permissions before retrying.");

    private static readonly TransferFailureDetails NotFound = new(
        "Arquivo ou pasta não encontrado",
        "A origem ou a pasta de destino foi movida, removida ou não está disponível.",
        "Atualize as pastas e confirme os caminhos. Se necessário, selecione o arquivo novamente.",
        "A transfer source or destination folder was not found. Refresh the listing and check the paths.");

    private static readonly TransferFailureDetails AlreadyExists = new(
        "O destino já existe",
        "Um arquivo com esse nome já está no destino e sua substituição não foi autorizada.",
        "Confira o arquivo existente. Tente novamente e confirme a substituição, ou use outro nome.",
        "The transfer destination already exists. Confirm replacement or choose another filename.");

    private static readonly TransferFailureDetails ConnectionLost = new(
        "Conexão interrompida",
        "O canal de arquivos perdeu a conexão com a VPS ou precisa ser reaberto.",
        "Reconecte à mesma VPS. Confira o destino antes de repetir: uma finalização sem resposta pode ter sido aplicada.",
        "The transfer connection was lost. Reconnect the same VPS and inspect the destination before retrying.");

    private static readonly TransferFailureDetails Unavailable = new(
        "SFTP indisponível",
        "A conexão SSH está ativa, mas o servidor não disponibilizou o serviço de arquivos para esta conta.",
        "Verifique se o SFTP está habilitado no servidor e liberado para a conta. Depois, reconecte à VPS.",
        "The SFTP subsystem is unavailable. Check server and account configuration before reconnecting.");

    private static readonly TransferFailureDetails ChannelVerification = new(
        "Canal SFTP não confirmado",
        "Não foi possível confirmar a identidade ou abrir o canal de arquivos associado à sessão ativa.",
        "Desconecte e confira a identidade e a configuração do servidor antes de reconectar à mesma VPS.",
        "The associated file channel could not be verified. Disconnect and verify the server identity and configuration before reconnecting.");

    private static readonly TransferFailureDetails UnsafePath = new(
        "Nome ou caminho inválido",
        "O caminho não pôde ser usado com segurança nesta operação ou é longo demais.",
        "Escolha um nome válido e um caminho mais curto. Para downloads, use um nome compatível com o Windows.",
        "The transfer path or filename is invalid or unsafe. Choose a valid individual file path.");

    private static readonly TransferFailureDetails UnsupportedReplacement = new(
        "Substituição segura não suportada",
        "O servidor não oferece a renomeação atômica necessária para substituir o arquivo existente com segurança.",
        "Use outro nome de arquivo ou habilite o suporte à renomeação atômica no servidor. O arquivo anterior foi preservado.",
        "The server cannot atomically replace the destination. The previous file was preserved; choose another filename.");

    private static readonly TransferFailureDetails Timeout = new(
        "Tempo de espera excedido",
        "O servidor não respondeu dentro do limite de tempo da operação.",
        "Verifique a rede e a VPS. Reconecte e confira o destino antes de tentar novamente.",
        "The transfer timed out. Check the network, reconnect, and inspect the destination before retrying.");

    private static readonly TransferFailureDetails Authentication = new(
        "Autenticação não concluída",
        "O servidor não aceitou a autenticação necessária para o canal de arquivos.",
        "Reconecte à VPS e informe as credenciais na janela de autenticação. Não inclua senhas nos caminhos ou logs.",
        "Transfer authentication failed. Reconnect and supply credentials through the authentication dialog.");

    private static readonly TransferFailureDetails DiskFull = new(
        "Espaço local insuficiente",
        "O sistema local informou que não há espaço suficiente para concluir a gravação.",
        "Libere espaço na unidade de destino ou escolha outra pasta, e tente novamente.",
        "The local destination has insufficient free space. Free space or select another destination before retrying.");

    private static readonly TransferFailureDetails LocalIo = new(
        "Falha de leitura ou gravação",
        "Não foi possível ler ou gravar um arquivo durante a transferência.",
        "Verifique o espaço livre, as permissões e se outro programa está usando o arquivo. Confira o destino antes de tentar novamente.",
        "The transfer could not read or write a file. Check space, permissions, and file locks before retrying.");
}
