using FluentFTP;

namespace SparkVault.Core;

public sealed class FtpTarget : IBackupTarget
{
    private const string TempSuffix = ".sparkvault-tmp";
    private readonly string _remotePath;
    private readonly AsyncFtpClient _client;

    public FtpTarget(BackupTarget config)
    {
        _remotePath = config.RemotePath ?? throw new InvalidOperationException("FTP target requires RemotePath.");
        var host = config.Host ?? throw new InvalidOperationException("FTP target requires Host.");
        var username = config.Username ?? throw new InvalidOperationException("FTP target requires Username.");

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
        _client = new AsyncFtpClient(host, username, password, config.Port ?? 21, ftpConfig);
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
            if (!await _client.DirectoryExists(_remotePath, ct))
                await _client.CreateDirectory(_remotePath, ct);
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

            // ponytail: FTP has no atomic replace-on-rename (unlike SFTP's isPosix rename). A kill
            // between DeleteFile and Rename below leaves no file at finalPath — narrow window,
            // self-healing on the next run (the file just gets re-uploaded). Revisit if FluentFTP
            // or the target server ever exposes an atomic alternative.
            if (await _client.FileExists(finalPath, ct))
                await _client.DeleteFile(finalPath, ct);
            await _client.Rename(tempPath, finalPath, ct);
        }
        catch
        {
            try { await _client.DeleteFile(tempPath, CancellationToken.None); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    public async Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        if (!await _client.DirectoryExists(_remotePath, ct))
            return Enumerable.Empty<RemoteFileInfo>();

        var items = await _client.GetListing(_remotePath, FtpListOption.Recursive, ct);
        var rootPrefix = _remotePath.TrimEnd('/') + "/";
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
        string.Concat(_remotePath.TrimEnd('/'), "/", relativePath.Replace('\\', '/'));
}
