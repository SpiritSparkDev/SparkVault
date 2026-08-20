using FluentFTP;

namespace SparkVault.Core;

public sealed class FtpTarget : IBackupTarget
{
    private const string TempSuffix = ".sparkvault-tmp";
    private readonly BackupTarget _config;
    private readonly AsyncFtpClient _client;

    public FtpTarget(BackupTarget config)
    {
        _config = config;
        var ftpConfig = new FtpConfig
        {
            EncryptionMode = config.EncryptionMode switch
            {
                FtpEncryption.Explicit => FluentFTP.FtpEncryptionMode.Explicit,
                FtpEncryption.Implicit => FluentFTP.FtpEncryptionMode.Implicit,
                _ => FluentFTP.FtpEncryptionMode.None,
            },
        };
        var password = string.IsNullOrEmpty(config.EncryptedPassword)
            ? ""
            : CredentialProtector.Unprotect(config.EncryptedPassword);
        _client = new AsyncFtpClient(config.Host, config.Username, password, config.Port ?? 21, ftpConfig);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!_client.IsConnected)
            await _client.Connect(ct);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct);
            if (!await _client.DirectoryExists(_config.RemotePath, ct))
                await _client.CreateDirectory(_config.RemotePath, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureConnectedAsync(ct);

        var finalPath = RemotePath(file.RelativePath);
        var tempPath = finalPath + TempSuffix;

        try
        {
            await using var source = File.OpenRead(file.FullPath);
            var status = await _client.UploadStream(source, tempPath, FtpRemoteExists.Overwrite, createRemoteDir: true, token: ct);
            if (status != FtpStatus.Success)
                throw new IOException($"FTP-Upload fehlgeschlagen für {file.RelativePath}: {status}");

            var info = await _client.GetObjectInfo(tempPath, token: ct);
            if (info is null || info.Size != file.Size)
                throw new IOException(
                    $"Verifikation fehlgeschlagen für {file.RelativePath}: erwartet {file.Size} Bytes, erhalten {info?.Size ?? -1}.");

            if (await _client.FileExists(finalPath, ct))
                await _client.DeleteFile(finalPath, ct);
            await _client.Rename(tempPath, finalPath, ct);
        }
        catch
        {
            try { await _client.DeleteFile(tempPath, ct); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    public async Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        if (!await _client.DirectoryExists(_config.RemotePath, ct))
            return Enumerable.Empty<RemoteFileInfo>();

        var items = await _client.GetListing(_config.RemotePath, FtpListOption.Recursive, ct);
        var rootPrefix = _config.RemotePath.TrimEnd('/') + "/";
        return items
            .Where(i => i.Type == FtpObjectType.File)
            .Select(i => new RemoteFileInfo(
                i.FullName.StartsWith(rootPrefix, StringComparison.Ordinal) ? i.FullName[rootPrefix.Length..] : i.FullName,
                i.Size));
    }

    public async Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var full = RemotePath(remotePath);
        if (await _client.FileExists(full, ct))
            await _client.DeleteFile(full, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
            await _client.Disconnect();
        _client.Dispose();
    }

    // Remote paths are always forward-slash, regardless of RelativePath's Windows-style
    // backslashes — mixing separators here is the same class of bug the exclusion matcher
    // hit with backslash-as-escape; plain string concatenation avoids Path.* entirely.
    private string RemotePath(string relativePath) =>
        string.Concat(_config.RemotePath.TrimEnd('/'), "/", relativePath.Replace('\\', '/'));
}
