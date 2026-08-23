using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class RunFileRepositoryTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public void AddRangeThenGetByRunId_RoundTripsAllFiles()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new RunFileRepository(connectionString);

            var modifiedA = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
            var files = new List<RunFileRecord>
            {
                new("Test\\a.txt", 100, modifiedA),
                new("Test\\sub\\b.txt", 250, null),
            };
            repo.AddRange(runId: 42, files);

            var loaded = repo.GetByRunId(42);

            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, f => f.RelativePath == "Test\\a.txt" && f.Size == 100 && f.SourceModifiedUtc == modifiedA);
            Assert.Contains(loaded, f => f.RelativePath == "Test\\sub\\b.txt" && f.Size == 250 && f.SourceModifiedUtc == null);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetByRunId_UnknownRun_ReturnsEmpty()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new RunFileRepository(connectionString);

            var loaded = repo.GetByRunId(999);

            Assert.Empty(loaded);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
