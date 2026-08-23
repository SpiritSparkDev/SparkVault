namespace SparkVault.Core;

// Pure comparison: no DB, no filesystem, no target I/O — takes a fresh scan and "what we knew
// last time" and says what to upload, what to leave alone, and what disappeared. Used both by
// BackupRunner (to decide what to actually transfer) and OnChangeJobChecker (to decide whether a
// job is due), so the two never disagree about what "changed" means.
public static class IncrementalPlanner
{
    public sealed record Plan(
        IReadOnlyList<BackupFile> ToUpload,
        IReadOnlyList<BackupFile> Unchanged,
        IReadOnlyList<ManifestEntry> ToQuarantine);

    public static Plan Compute(IReadOnlyList<BackupFile> currentFiles, IReadOnlyList<ManifestEntry> previousManifest)
    {
        var previousByPath = previousManifest.ToDictionary(m => m.RelativePath, StringComparer.Ordinal);
        var toUpload = new List<BackupFile>();
        var unchanged = new List<BackupFile>();
        var currentPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in currentFiles)
        {
            currentPaths.Add(file.RelativePath);
            if (previousByPath.TryGetValue(file.RelativePath, out var prev)
                && prev.Size == file.Size
                && prev.SourceModifiedUtc == file.LastWriteTimeUtc)
            {
                unchanged.Add(file);
            }
            else
            {
                toUpload.Add(file);
            }
        }

        var toQuarantine = previousManifest.Where(m => !currentPaths.Contains(m.RelativePath)).ToList();
        return new Plan(toUpload, unchanged, toQuarantine);
    }

    public static bool HasChanges(IReadOnlyList<BackupFile> currentFiles, IReadOnlyList<ManifestEntry> previousManifest)
    {
        var plan = Compute(currentFiles, previousManifest);
        return plan.ToUpload.Count > 0 || plan.ToQuarantine.Count > 0;
    }
}
