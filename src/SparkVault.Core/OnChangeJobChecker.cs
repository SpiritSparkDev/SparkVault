using Serilog;

namespace SparkVault.Core;

// Checked exactly once, at app startup (never by BackgroundScheduler's periodic tick — see
// ScheduleCalculator.IsDue). Purely local (FileScanner + DB reads only, no target network calls)
// so it costs nothing extra in traffic just to decide whether a run is warranted.
public static class OnChangeJobChecker
{
    public static async Task RunDueJobsAsync(
        JobRepository jobRepository, RunRepository runRepository, RunFileRepository runFileRepository,
        BackupRunner runner, ILogger logger, CancellationToken ct)
    {
        foreach (var job in jobRepository.GetAll())
        {
            if (job.ScheduleType != ScheduleType.OnChange || job.Targets.Count == 0) continue;

            try
            {
                if (HasAnyTargetChanged(job, runRepository, runFileRepository))
                    await runner.RunAsync(job, progress: null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Error(ex, "OnChange-Prüfung fehlgeschlagen für Job {JobId}", job.Id);
            }
        }
    }

    private static bool HasAnyTargetChanged(BackupJob job, RunRepository runRepository, RunFileRepository runFileRepository)
    {
        IReadOnlyList<BackupFile> currentFiles;
        try
        {
            currentFiles = JobFileScanner.Scan(job);
        }
        catch
        {
            // Source unreadable: no forced, doomed-to-fail run here — a manually or time-triggered
            // run (if any) will surface the real error instead.
            return false;
        }

        foreach (var target in job.Targets)
        {
            var lastSuccessful = runRepository.GetLatestSuccessfulRun(job.Id, target.Id);
            IReadOnlyList<ManifestEntry> previousManifest = lastSuccessful is null
                ? Array.Empty<ManifestEntry>()
                : runFileRepository.GetByRunId(lastSuccessful.Id)
                    .Select(f => new ManifestEntry(f.RelativePath, f.Size, f.SourceModifiedUtc))
                    .ToList();

            if (IncrementalPlanner.HasChanges(currentFiles, previousManifest))
                return true;
        }
        return false;
    }
}
