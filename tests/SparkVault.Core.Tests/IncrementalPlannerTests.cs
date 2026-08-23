using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class IncrementalPlannerTests
{
    private static readonly DateTime T0 = new(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Compute_EmptyPreviousManifest_EverythingIsToUpload()
    {
        var current = new[] { new BackupFile(@"C:\a.txt", "a.txt", 100, T0) };

        var plan = IncrementalPlanner.Compute(current, Array.Empty<ManifestEntry>());

        Assert.Single(plan.ToUpload);
        Assert.Empty(plan.Unchanged);
        Assert.Empty(plan.ToQuarantine);
    }

    [Fact]
    public void Compute_SameSizeAndTimestamp_IsUnchanged()
    {
        var current = new[] { new BackupFile(@"C:\a.txt", "a.txt", 100, T0) };
        var previous = new[] { new ManifestEntry("a.txt", 100, T0) };

        var plan = IncrementalPlanner.Compute(current, previous);

        Assert.Empty(plan.ToUpload);
        Assert.Single(plan.Unchanged);
        Assert.Empty(plan.ToQuarantine);
    }

    [Fact]
    public void Compute_SameSizeDifferentTimestamp_IsToUpload()
    {
        var current = new[] { new BackupFile(@"C:\a.txt", "a.txt", 100, T0.AddMinutes(1)) };
        var previous = new[] { new ManifestEntry("a.txt", 100, T0) };

        var plan = IncrementalPlanner.Compute(current, previous);

        Assert.Single(plan.ToUpload);
        Assert.Empty(plan.Unchanged);
    }

    [Fact]
    public void Compute_DifferentSizeSameTimestamp_IsToUpload()
    {
        var current = new[] { new BackupFile(@"C:\a.txt", "a.txt", 200, T0) };
        var previous = new[] { new ManifestEntry("a.txt", 100, T0) };

        var plan = IncrementalPlanner.Compute(current, previous);

        Assert.Single(plan.ToUpload);
        Assert.Empty(plan.Unchanged);
    }

    [Fact]
    public void Compute_FileMissingFromCurrentScan_IsToQuarantine()
    {
        var current = Array.Empty<BackupFile>();
        var previous = new[] { new ManifestEntry("a.txt", 100, T0) };

        var plan = IncrementalPlanner.Compute(current, previous);

        Assert.Empty(plan.ToUpload);
        Assert.Empty(plan.Unchanged);
        Assert.Single(plan.ToQuarantine);
        Assert.Equal("a.txt", plan.ToQuarantine[0].RelativePath);
    }

    [Fact]
    public void Compute_PreviousEntryWithNullTimestamp_NeverMatchesAndIsReUploaded()
    {
        // Legacy RunFiles rows written before this feature have SourceModifiedUtc == null.
        var current = new[] { new BackupFile(@"C:\a.txt", "a.txt", 100, T0) };
        var previous = new[] { new ManifestEntry("a.txt", 100, null) };

        var plan = IncrementalPlanner.Compute(current, previous);

        Assert.Single(plan.ToUpload);
        Assert.Empty(plan.Unchanged);
    }

    [Fact]
    public void Compute_PathDiffersOnlyInCasing_IsUnchanged()
    {
        // Windows filesystems are case-insensitive: "a.txt" and "A.TXT" are the same file. Treating
        // them as a separate upload plus deletion made the run quarantine the file it had just
        // uploaded to the very same physical path.
        var current = new[] { new BackupFile(@"C:\a.txt", "sub\\a.txt", 100, T0) };
        var previous = new[] { new ManifestEntry("SUB\\A.TXT", 100, T0) };

        var plan = IncrementalPlanner.Compute(current, previous);

        Assert.Empty(plan.ToUpload);
        Assert.Single(plan.Unchanged);
        Assert.Empty(plan.ToQuarantine);
    }

    [Fact]
    public void HasChanges_NothingDiffers_ReturnsFalse()
    {
        var current = new[] { new BackupFile(@"C:\a.txt", "a.txt", 100, T0) };
        var previous = new[] { new ManifestEntry("a.txt", 100, T0) };

        Assert.False(IncrementalPlanner.HasChanges(current, previous));
    }

    [Fact]
    public void HasChanges_NewFile_ReturnsTrue()
    {
        var current = new[] { new BackupFile(@"C:\a.txt", "a.txt", 100, T0) };

        Assert.True(IncrementalPlanner.HasChanges(current, Array.Empty<ManifestEntry>()));
    }

    [Fact]
    public void HasChanges_OnlyDeletion_ReturnsTrue()
    {
        var previous = new[] { new ManifestEntry("a.txt", 100, T0) };

        Assert.True(IncrementalPlanner.HasChanges(Array.Empty<BackupFile>(), previous));
    }
}
