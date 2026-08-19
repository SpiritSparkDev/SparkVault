using Serilog;

namespace SparkVault.Core;

public sealed class BackupRunner
{
    private readonly RunRepository _runRepository;
    private readonly ILogger _logger;

    public event Action<BackupJob>? RunStarted;
    public event Action<BackupJob, RunStatus>? RunCompleted;

    public BackupRunner(RunRepository runRepository, ILogger logger)
    {
        _runRepository = runRepository;
        _logger = logger;
    }

    public async Task<BackupRun> RunAsync(BackupJob job, IBackupTarget target, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        RunStarted?.Invoke(job);
        var run = new BackupRun { JobId = job.Id, StartedAt = DateTime.UtcNow, Status = RunStatus.Failed };
        run.Id = _runRepository.Add(run);

        try
        {
            var files = FileScanner.Scan(job.SourcePath, job.ExcludePatterns);
            long totalBytes = files.Sum(f => f.Size);
            int done = 0;
            long bytesDone = 0;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                await target.UploadAsync(file, progress, ct);
                done++;
                bytesDone += file.Size;
                progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes));
            }

            run.Status = RunStatus.Success;
            run.FileCount = files.Count;
            run.TotalBytes = totalBytes;
            _logger.Information("Job {JobName} completed: {FileCount} files, {TotalBytes} bytes", job.Name, files.Count, totalBytes);
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Cancelled;
            _logger.Warning("Job {JobName} was cancelled", job.Name);
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            _logger.Error(ex, "Job {JobName} failed", job.Name);
        }
        finally
        {
            run.EndedAt = DateTime.UtcNow;
            _runRepository.Update(run);
            RunCompleted?.Invoke(job, run.Status);
        }

        return run;
    }
}
