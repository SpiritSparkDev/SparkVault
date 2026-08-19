namespace SparkVault.Core;

public sealed class LocalTarget : IBackupTarget
{
    private const string TempSuffix = ".sparkvault-tmp";
    private readonly string _destinationRoot;

    public LocalTarget(string destinationRoot)
    {
        _destinationRoot = destinationRoot;
    }

    public Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_destinationRoot);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var finalPath = Path.Combine(_destinationRoot, file.RelativePath);
        var tempPath = finalPath + TempSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        try
        {
            await using (var source = File.OpenRead(file.FullPath))
            await using (var dest = File.Create(tempPath))
            {
                await source.CopyToAsync(dest, ct);
            }

            var copiedLength = new FileInfo(tempPath).Length;
            if (copiedLength != file.Size)
            {
                throw new IOException(
                    $"Verification failed for {file.RelativePath}: expected {file.Size} bytes, got {copiedLength}.");
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    public Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_destinationRoot))
            return Task.FromResult(Enumerable.Empty<RemoteFileInfo>());

        var files = Directory.EnumerateFiles(_destinationRoot, "*", SearchOption.AllDirectories)
            .Select(f => new RemoteFileInfo(Path.GetRelativePath(_destinationRoot, f), new FileInfo(f).Length));

        return Task.FromResult(files);
    }

    public Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        var fullPath = Path.Combine(_destinationRoot, remotePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }
}
