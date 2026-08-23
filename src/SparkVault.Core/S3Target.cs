using Amazon.S3;
using Amazon.S3.Model;

namespace SparkVault.Core;

public sealed class S3Target : IBackupTarget
{
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly AmazonS3Client _client;
    private bool _bucketEnsured;

    public S3Target(BackupTarget config)
    {
        _bucket = config.Bucket ?? throw new InvalidOperationException("S3 target requires Bucket.");
        _prefix = config.RemotePath ?? "";
        var accessKey = config.AccessKey ?? throw new InvalidOperationException("S3 target requires AccessKey.");
        var region = config.Region ?? throw new InvalidOperationException("S3 target requires Region.");
        var secretKey = string.IsNullOrEmpty(config.EncryptedSecretKey)
            ? ""
            : CredentialProtector.Unprotect(config.EncryptedSecretKey);

        var s3Config = new AmazonS3Config();
        if (!string.IsNullOrEmpty(config.Endpoint))
        {
            s3Config.ServiceURL = config.Endpoint;
            s3Config.ForcePathStyle = true;
            s3Config.AuthenticationRegion = region;
        }
        else
        {
            s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }

        _client = new AmazonS3Client(accessKey, secretKey, s3Config);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await EnsureBucketAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketEnsured) return;

        if (!await BucketExistsAsync(ct))
            throw new InvalidOperationException(
                $"S3-Bucket '{_bucket}' existiert nicht oder ist mit diesen Zugangsdaten nicht sichtbar. Bitte den Bucket beim Anbieter anlegen.");

        _bucketEnsured = true;
    }

    private async Task<bool> BucketExistsAsync(CancellationToken ct)
    {
        try
        {
            await _client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = _bucket }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureBucketAsync(ct);

        var key = RemoteKey(file.RelativePath);

        await using var source = File.OpenRead(file.FullPath);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = source,
        }, ct);

        var head = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = _bucket,
            Key = key,
        }, ct);

        if (head.ContentLength != file.Size)
        {
            throw new IOException(
                $"Verifikation fehlgeschlagen für {file.RelativePath}: erwartet {file.Size} Bytes, erhalten {head.ContentLength}.");
        }
    }

    public async Task DownloadAsync(string remotePath, string localDestinationPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureBucketAsync(ct);

        var key = RemoteKey(remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localDestinationPath)!);

        using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct);
        await using var dest = File.Create(localDestinationPath);
        await response.ResponseStream.CopyToAsync(dest, ct);
    }

    public async Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct)
    {
        await EnsureBucketAsync(ct);

        var rootPrefix = _prefix.TrimEnd('/') + "/";
        var results = new List<RemoteFileInfo>();
        string? continuationToken = null;

        do
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = rootPrefix == "/" ? _prefix : rootPrefix,
                ContinuationToken = continuationToken,
            }, ct);

            results.AddRange((response.S3Objects ?? new List<S3Object>()).Select(o => new RemoteFileInfo(
                o.Key.StartsWith(rootPrefix, StringComparison.Ordinal) ? o.Key[rootPrefix.Length..] : o.Key,
                o.Size ?? 0)));

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuationToken is not null);

        return results;
    }

    public async Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        var key = RemoteKey(remotePath);
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private string RemoteKey(string relativePath) =>
        string.IsNullOrEmpty(_prefix)
            ? relativePath.Replace('\\', '/')
            : string.Concat(_prefix.TrimEnd('/'), "/", relativePath.Replace('\\', '/'));
}
