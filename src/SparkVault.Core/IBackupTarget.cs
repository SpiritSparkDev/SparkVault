namespace SparkVault.Core;

public sealed record TransferProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

public sealed record RemoteFileInfo(string Path, long Size);

public interface IBackupTarget : IAsyncDisposable
{
    Task<bool> TestConnectionAsync(CancellationToken ct);
    Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct);
    Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct);
    Task DeleteAsync(string remotePath, CancellationToken ct);
}
