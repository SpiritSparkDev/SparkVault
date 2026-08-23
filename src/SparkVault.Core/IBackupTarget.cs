namespace SparkVault.Core;

public sealed record TransferProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile, string CurrentTarget);

public sealed record RemoteFileInfo(string Path, long Size);

public interface IBackupTarget : IAsyncDisposable
{
    Task<bool> TestConnectionAsync(CancellationToken ct);
    Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct);
    Task DownloadAsync(string remotePath, string localDestinationPath, CancellationToken ct);
    Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct);
    Task DeleteAsync(string remotePath, CancellationToken ct);
    // true if a file was actually moved, false if the source no longer existed (no-op).
    Task<bool> MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct);
}
