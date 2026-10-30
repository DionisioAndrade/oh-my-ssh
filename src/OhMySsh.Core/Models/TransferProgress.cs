namespace OhMySsh.Core.Models;

/// <summary>
/// Byte progress remains cancellable even when all bytes have been copied. IsFinalizing becomes true
/// only after streams have closed and the last cancellation check has passed, immediately before the
/// noncancellable atomic commit. Final success is represented by completion of the transfer task.
/// </summary>
public sealed record TransferProgress(long BytesTransferred, long TotalBytes, bool IsFinalizing = false);
