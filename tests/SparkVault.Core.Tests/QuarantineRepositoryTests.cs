using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class QuarantineRepositoryTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public void Add_ThenGetLatestQuarantinePath_ReturnsIt()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new QuarantineRepository(connectionString);

            repo.Add(jobId: 1, targetId: 1, originalRelativePath: "Test\\a.txt", quarantinePath: "_deleted\\20260824-100000\\Test\\a.txt", quarantinedAtRunId: 5, quarantinedAtUtc: DateTime.UtcNow);

            var found = repo.GetLatestQuarantinePath(1, 1, "Test\\a.txt");

            Assert.Equal("_deleted\\20260824-100000\\Test\\a.txt", found);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestQuarantinePath_MultipleEntries_ReturnsMostRecent()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new QuarantineRepository(connectionString);

            repo.Add(1, 1, "Test\\a.txt", "_deleted\\older\\Test\\a.txt", 5, DateTime.UtcNow.AddDays(-1));
            repo.Add(1, 1, "Test\\a.txt", "_deleted\\newer\\Test\\a.txt", 6, DateTime.UtcNow);

            Assert.Equal("_deleted\\newer\\Test\\a.txt", repo.GetLatestQuarantinePath(1, 1, "Test\\a.txt"));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestQuarantinePath_NoEntry_ReturnsNull()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new QuarantineRepository(connectionString);

            Assert.Null(repo.GetLatestQuarantinePath(1, 1, "Test\\a.txt"));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
