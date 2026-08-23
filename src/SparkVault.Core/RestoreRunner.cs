using Serilog;

namespace SparkVault.Core;

public sealed class RestoreRunner
{
    private readonly RunFileRepository _runFileRepository;
    private readonly ILogger _logger;

    public RestoreRunner(RunFileRepository runFileRepository, ILogger logger)
    {
        _runFileRepository = runFileRepository;
        _logger = logger;
    }

    public async Task RestoreAsync(
        BackupJob job, BackupTarget targetConfig, int runId,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var files = _runFileRepository.GetByRunId(runId);

        await using var target = TargetFactory.Create(targetConfig);

        if (!await target.TestConnectionAsync(ct))
            throw new IOException($"Ziel nicht erreichbar: {targetConfig.Describe()}");

        var jobFolderPrefix = BackupRunner.SanitizeForPath(job.Name) + "\\";
        long totalBytes = files.Sum(f => f.Size);
        int done = 0;
        long bytesDone = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));

            var originalRelative = file.RelativePath.StartsWith(jobFolderPrefix, StringComparison.Ordinal)
                ? file.RelativePath[jobFolderPrefix.Length..]
                : file.RelativePath;
            var localDestination = Path.Combine(job.SourcePath, originalRelative);

            await target.DownloadAsync(file.RelativePath, localDestination, ct);
            done++;
            bytesDone += file.Size;
            progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));
        }

        _logger.Information("Restore für Job {JobName} von Lauf {RunId} abgeschlossen: {FileCount} Dateien",
            job.Name, runId, done);
    }
}
