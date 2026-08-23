using Serilog;

namespace SparkVault.Core;

public sealed class RestoreRunner
{
    private readonly RunFileRepository _runFileRepository;
    private readonly QuarantineRepository _quarantineRepository;
    private readonly SemaphoreSlim _runLock;
    private readonly ILogger _logger;

    public RestoreRunner(RunFileRepository runFileRepository, QuarantineRepository quarantineRepository, SemaphoreSlim runLock, ILogger logger)
    {
        _runFileRepository = runFileRepository;
        _quarantineRepository = quarantineRepository;
        _runLock = runLock;
        _logger = logger;
    }

    public async Task RestoreAsync(
        BackupJob job, BackupTarget targetConfig, int runId,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var files = _runFileRepository.GetByRunId(runId);

        if (files.Count == 0)
            throw new IOException($"Keine Dateien für Lauf {runId} gefunden — Wiederherstellung nicht möglich.");

        var lockHeld = false;
        try
        {
            await _runLock.WaitAsync(ct);
            lockHeld = true;

            await using var target = TargetFactory.Create(targetConfig);

            if (!await target.TestConnectionAsync(ct))
                throw new IOException($"Ziel nicht erreichbar: {targetConfig.Describe()}");

            long totalBytes = files.Sum(f => f.Size);
            int done = 0;
            long bytesDone = 0;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));

                var separatorIndex = file.RelativePath.IndexOf('\\');
                var originalRelative = separatorIndex >= 0 ? file.RelativePath[(separatorIndex + 1)..] : file.RelativePath;
                var localDestination = Path.Combine(job.SourcePath, originalRelative);

                var tempDestination = localDestination + ".sparkvault-tmp";
                try
                {
                    try
                    {
                        await target.DownloadAsync(file.RelativePath, tempDestination, ct);
                    }
                    catch (Exception primaryEx) when (primaryEx is not OperationCanceledException)
                    {
                        var quarantinePath = _quarantineRepository.GetLatestQuarantinePath(job.Id, targetConfig.Id, file.RelativePath);
                        if (quarantinePath is null) throw;

                        try
                        {
                            await target.DownloadAsync(quarantinePath, tempDestination, ct);
                        }
                        catch
                        {
                            throw primaryEx;
                        }
                    }
                    File.Move(tempDestination, localDestination, overwrite: true);
                }
                catch
                {
                    if (File.Exists(tempDestination))
                        File.Delete(tempDestination);
                    throw;
                }

                done++;
                bytesDone += file.Size;
                progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));
            }

            _logger.Information("Restore für Job {JobName} von Lauf {RunId} abgeschlossen: {FileCount} Dateien",
                job.Name, runId, done);
        }
        finally
        {
            if (lockHeld)
                _runLock.Release();
        }
    }
}
