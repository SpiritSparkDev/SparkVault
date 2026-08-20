using Renci.SshNet;

namespace SparkVault.Core;

public sealed class SftpTarget : IBackupTarget
{
    private const string TempSuffix = ".sparkvault-tmp";
    private readonly string _remotePath;
    private readonly SftpClient _client;

    public SftpTarget(BackupTarget config)
    {
        _remotePath = config.RemotePath ?? throw new InvalidOperationException("SFTP target requires RemotePath.");
        var host = config.Host ?? throw new InvalidOperationException("SFTP target requires Host.");
        var username = config.Username ?? throw new InvalidOperationException("SFTP target requires Username.");

        var authMethods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(config.EncryptedPassword))
            authMethods.Add(new PasswordAuthenticationMethod(username, CredentialProtector.Unprotect(config.EncryptedPassword)));
        if (!string.IsNullOrEmpty(config.PrivateKeyPath))
        {
            var passphrase = string.IsNullOrEmpty(config.EncryptedKeyPassphrase)
                ? null
                : CredentialProtector.Unprotect(config.EncryptedKeyPassphrase);
            authMethods.Add(new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(config.PrivateKeyPath, passphrase)));
        }

        var connectionInfo = new ConnectionInfo(host, config.Port ?? 22, username, authMethods.ToArray());
        _client = new SftpClient(connectionInfo);
    }

    private Task EnsureConnectedAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!_client.IsConnected)
            _client.Connect();
    }, ct);

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct);
            await Task.Run(() =>
            {
                if (!_client.Exists(_remotePath))
                    CreateDirectoryRecursive(_remotePath);
            }, ct);
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
            await Task.Run(() =>
            {
                var remoteDir = finalPath[..finalPath.LastIndexOf('/')];
                if (!_client.Exists(remoteDir))
                    CreateDirectoryRecursive(remoteDir);

                using var source = File.OpenRead(file.FullPath);
                _client.UploadFile(source, tempPath, canOverride: true);

                var attrs = _client.GetAttributes(tempPath);
                if (attrs.Size != file.Size)
                    throw new IOException(
                        $"Verifikation fehlgeschlagen für {file.RelativePath}: erwartet {file.Size} Bytes, erhalten {attrs.Size}.");

                if (_client.Exists(finalPath))
                    _client.DeleteFile(finalPath);
                _client.RenameFile(tempPath, finalPath);
            }, ct);
        }
        catch
        {
            // Best-effort cleanup must not be skipped just because `ct` is the reason the
            // operation above failed — never reuse a possibly-cancelled token here.
            try { await Task.Run(() => { if (_client.Exists(tempPath)) _client.DeleteFile(tempPath); }, CancellationToken.None); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    public async Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        return await Task.Run(() =>
        {
            if (!_client.Exists(_remotePath))
                return Enumerable.Empty<RemoteFileInfo>();

            var results = new List<RemoteFileInfo>();
            WalkDirectory(_remotePath, results);
            return (IEnumerable<RemoteFileInfo>)results;
        }, ct);
    }

    public async Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var full = RemotePath(remotePath);
        await Task.Run(() =>
        {
            if (_client.Exists(full))
                _client.DeleteFile(full);
        }, ct);
    }

    public ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
            _client.Disconnect();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private void CreateDirectoryRecursive(string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!_client.Exists(current))
                _client.CreateDirectory(current);
        }
    }

    private void WalkDirectory(string path, List<RemoteFileInfo> results)
    {
        var rootPrefix = _remotePath.TrimEnd('/') + "/";
        foreach (var entry in _client.ListDirectory(path))
        {
            if (entry.Name is "." or "..") continue;

            if (entry.IsDirectory)
            {
                WalkDirectory(entry.FullName, results);
            }
            else
            {
                var relative = entry.FullName.StartsWith(rootPrefix, StringComparison.Ordinal)
                    ? entry.FullName[rootPrefix.Length..]
                    : entry.FullName;
                results.Add(new RemoteFileInfo(relative, entry.Length));
            }
        }
    }

    // Same reasoning as FtpTarget: plain string concatenation on forward slashes, never Path.*.
    private string RemotePath(string relativePath) =>
        string.Concat(_remotePath.TrimEnd('/'), "/", relativePath.Replace('\\', '/'));
}
