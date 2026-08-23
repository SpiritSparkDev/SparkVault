namespace SparkVault.Core;

// The scan-then-prefix-with-job-folder step BackupRunner and the OnChange startup check
// (OnChangeJobChecker) both need identically — extracted so there is exactly one implementation.
public static class JobFileScanner
{
    public static IReadOnlyList<BackupFile> Scan(BackupJob job)
    {
        var scanned = FileScanner.Scan(job.SourcePath, job.ExcludePatterns);
        var jobFolder = SanitizeForPath(job.Name);
        return scanned.Select(f => f with { RelativePath = $"{jobFolder}\\{f.RelativePath}" }).ToList();
    }

    // Every file lands under a job-named subfolder on every target type, so multiple jobs
    // sharing the same physical destination (same FTP account, same S3 bucket, etc.) never
    // collide. Path.GetInvalidFileNameChars() also covers '/' and '\', so a job name can't
    // sneak in extra path segments.
    internal static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
