using Serilog;

namespace SparkVault.Core;

public sealed class BackupRunner
{
    private readonly RunRepository _runRepository;
    private readonly ILogger _logger;

    // ponytail: single global lock serializes all jobs, not just the same job — fine for
    // MVP's single-user desktop scale; move to a per-job SemaphoreSlim keyed by job.Id if
    // running multiple jobs truly concurrently ever becomes a real requirement.
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public event Action<BackupJob>? RunStarted;
    public event Action<BackupJob, RunStatus>? RunCompleted;

    public BackupRunner(RunRepository runRepository, ILogger logger)
    {
        _runRepository = runRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BackupRun>> RunAsync(BackupJob job, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        RunStarted?.Invoke(job);
        var runGroupId = Guid.NewGuid();
        var results = new List<BackupRun>();
        var lockHeld = false;
        var scanFailed = false;

        try
        {
            await _runLock.WaitAsync(ct);
            lockHeld = true;

            IReadOnlyList<BackupFile> files = Array.Empty<BackupFile>();
            try
            {
                files = FileScanner.Scan(job.SourcePath, job.ExcludePatterns);
            }
            catch (Exception ex)
            {
                // Source itself unreadable: still record one failed run per target, so the
                // log shows every target was attempted-and-failed rather than silently empty.
                foreach (var targetConfig in job.Targets)
                    results.Add(RecordImmediateFailure(job, targetConfig, runGroupId, ex.Message));
                scanFailed = true;
            }

            if (!scanFailed)
            {
                foreach (var targetConfig in job.Targets)
                {
                    // Already cancelled: this target never got its turn, so it is Cancelled — not
                    // a connection failure, which is what running it through RunForTargetAsync
                    // would log (TestConnectionAsync swallows the cancellation and returns false).
                    if (ct.IsCancellationRequested)
                    {
                        results.Add(RecordCancelled(job, targetConfig, runGroupId));
                        continue;
                    }

                    results.Add(await RunForTargetAsync(job, targetConfig, runGroupId, files, progress, ct));
                }
            }
        }
        finally
        {
            if (lockHeld)
                _runLock.Release();
        }

        var overallStatus = results.Count > 0 && results.All(r => r.Status == RunStatus.Success)
            ? RunStatus.Success
            : RunStatus.Failed;
        RunCompleted?.Invoke(job, overallStatus);

        return results;
    }

    private async Task<BackupRun> RunForTargetAsync(
        BackupJob job, BackupTarget targetConfig, Guid runGroupId, IReadOnlyList<BackupFile> files,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var run = new BackupRun
        {
            JobId = job.Id,
            TargetId = targetConfig.Id,
            RunGroupId = runGroupId,
            StartedAt = DateTime.UtcNow,
            Status = RunStatus.Failed,
        };

        int done = 0;
        long bytesDone = 0;

        try
        {
            run.Id = _runRepository.Add(run);

            await using var target = TargetFactory.Create(targetConfig);

            if (!await target.TestConnectionAsync(ct))
                throw new IOException($"Ziel nicht erreichbar: {targetConfig.Describe()}");

            long totalBytes = files.Sum(f => f.Size);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                await target.UploadAsync(file, progress, ct);
                done++;
                bytesDone += file.Size;
                progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes));
            }

            run.Status = RunStatus.Success;
            _logger.Information("Job {JobName} -> {Target} completed: {FileCount} files, {TotalBytes} bytes",
                job.Name, targetConfig.Describe(), done, bytesDone);
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Cancelled;
            _logger.Warning("Job {JobName} -> {Target} was cancelled", job.Name, targetConfig.Describe());
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            _logger.Error(ex, "Job {JobName} -> {Target} failed", job.Name, targetConfig.Describe());
        }
        finally
        {
            run.EndedAt = DateTime.UtcNow;
            run.FileCount = done;
            run.TotalBytes = bytesDone;
            if (run.Id != 0)
                _runRepository.Update(run);
        }

        return run;
    }

    private BackupRun RecordImmediateFailure(BackupJob job, BackupTarget targetConfig, Guid runGroupId, string errorMessage)
    {
        var run = new BackupRun
        {
            JobId = job.Id,
            TargetId = targetConfig.Id,
            RunGroupId = runGroupId,
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow,
            Status = RunStatus.Failed,
            ErrorMessage = errorMessage,
        };
        run.Id = _runRepository.Add(run);
        _logger.Error("Job {JobName} -> {Target} failed: {Error}", job.Name, targetConfig.Describe(), errorMessage);
        return run;
    }

    private BackupRun RecordCancelled(BackupJob job, BackupTarget targetConfig, Guid runGroupId)
    {
        var run = new BackupRun
        {
            JobId = job.Id,
            TargetId = targetConfig.Id,
            RunGroupId = runGroupId,
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow,
            Status = RunStatus.Cancelled,
        };
        run.Id = _runRepository.Add(run);
        _logger.Warning("Job {JobName} -> {Target} was cancelled before it started", job.Name, targetConfig.Describe());
        return run;
    }
}
