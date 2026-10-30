using System.IO;
using OhMySsh.Core.Models;
using OhMySsh.Core.Localization;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Only allowlisted text derived from error types and codes may leave the transfer boundary.</summary>
internal sealed record TransferFailureDetails(string Key, string DiagnosticMessage)
{
    public string Summary => UiText.Get($"Vm.TransferFailure.{Key}.Summary");
    public string Reason => UiText.Get($"Vm.TransferFailure.{Key}.Reason");
    public string SuggestedAction => UiText.Get($"Vm.TransferFailure.{Key}.Action");
    internal static readonly TransferFailureDetails Generic = new(
        "Generic",
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
        "PermissionDenied",
        "Transfer access was denied. Check source and destination permissions before retrying.");

    private static readonly TransferFailureDetails NotFound = new(
        "NotFound",
        "A transfer source or destination folder was not found. Refresh the listing and check the paths.");

    private static readonly TransferFailureDetails AlreadyExists = new(
        "AlreadyExists",
        "The transfer destination already exists. Confirm replacement or choose another filename.");

    private static readonly TransferFailureDetails ConnectionLost = new(
        "ConnectionLost",
        "The transfer connection was lost. Reconnect the same VPS and inspect the destination before retrying.");

    private static readonly TransferFailureDetails Unavailable = new(
        "Unavailable",
        "The SFTP subsystem is unavailable. Check server and account configuration before reconnecting.");

    private static readonly TransferFailureDetails ChannelVerification = new(
        "ChannelVerification",
        "The associated file channel could not be verified. Disconnect and verify the server identity and configuration before reconnecting.");

    private static readonly TransferFailureDetails UnsafePath = new(
        "UnsafePath",
        "The transfer path or filename is invalid or unsafe. Choose a valid individual file path.");

    private static readonly TransferFailureDetails UnsupportedReplacement = new(
        "UnsupportedReplacement",
        "The server cannot atomically replace the destination. The previous file was preserved; choose another filename.");

    private static readonly TransferFailureDetails Timeout = new(
        "Timeout",
        "The transfer timed out. Check the network, reconnect, and inspect the destination before retrying.");

    private static readonly TransferFailureDetails Authentication = new(
        "Authentication",
        "Transfer authentication failed. Reconnect and supply credentials through the authentication dialog.");

    private static readonly TransferFailureDetails DiskFull = new(
        "DiskFull",
        "The local destination has insufficient free space. Free space or select another destination before retrying.");

    private static readonly TransferFailureDetails LocalIo = new(
        "LocalIo",
        "The transfer could not read or write a file. Check space, permissions, and file locks before retrying.");
}
