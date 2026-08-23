# Inkrementelles Backup & "Bei veränderten Daten" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Skip re-uploading unchanged files (size+mtime comparison against each target's last
successful catalog), quarantine deleted-from-source files on the target instead of leaving or
deleting them, add a restore-side fallback so older runs still resolve quarantined files, add a
per-job "verify target before run" opt-in, and add a new `OnChange` schedule type that triggers
once at app startup if anything changed.

**Architecture:** A pure, DB/filesystem-free `IncrementalPlanner.Compute` function is the single
source of truth for "what changed" — both `BackupRunner` (to decide what to actually transfer) and
the new startup-only `OnChangeJobChecker` (to decide whether a job is due) call it. The "last known
state" comes from the existing `RunFiles` catalog (extended with a source-modified timestamp), not
a new table — the most recent successful `Run` per (Job, Target) already **is** that catalog.
Deleted-from-source files are moved server-side (`IBackupTarget.MoveAsync`, no local
download/re-upload) into a per-run timestamped quarantine folder; a new small `QuarantinedFiles`
table lets `RestoreRunner` fall back to the quarantine location when an older run's catalog points
at a path that a later run has since moved away.

**Tech Stack:** .NET 8, SQLite (Microsoft.Data.Sqlite), FluentFTP, SSH.NET, AWSSDK.S3, xUnit.

**Spec:** [2026-08-24-incremental-backup-design.md](../specs/2026-08-24-incremental-backup-design.md)

## Global Constraints

- Change detection compares source file **size and `LastWriteTimeUtc`** against the value recorded
  at the last successful upload to that specific target — never content hashing.
- Every run's `RunFiles` catalog stays a **complete** snapshot (unchanged files are included, not
  just newly-transferred ones) — `RestoreRunner` must keep working unmodified for the "download by
  catalog" part of its logic.
- Quarantine moves are **non-fatal**: a failed quarantine move logs a warning and does not fail the
  run.
- `ScheduleType.OnChange` is checked **only once, at app startup** — never by the periodic
  `BackgroundScheduler` tick (`ScheduleCalculator.IsDue` returns `false` for it unconditionally).
- New enum/combo values are **appended at the end** — never renumber existing `ScheduleType` values
  or existing `SettingsScheduleTypeCombo` indices.
- All new SQLite columns/tables go through the existing `SparkVaultDatabase.EnsureCreated` +
  `EnsureColumn` migration mechanism (`CREATE TABLE IF NOT EXISTS` alone never upgrades an
  existing installed database).
- Every task must leave `dotnet build` and `dotnet test` green before being marked complete.

---

### Task 1: `BackupFile` gains `LastWriteTimeUtc`

**Files:**
- Modify: `src/SparkVault.Core/Models.cs`
- Modify: `src/SparkVault.Core/FileScanner.cs`
- Modify: `tests/SparkVault.Core.Tests/FileScannerTests.cs`
- Modify: `tests/SparkVault.Core.Tests/LocalTargetTests.cs`
- Modify: `tests/SparkVault.Core.Tests/FtpTargetTests.cs`
- Modify: `tests/SparkVault.Core.Tests/SftpTargetTests.cs`
- Modify: `tests/SparkVault.Core.Tests/S3TargetTests.cs`

**Interfaces:**
- Produces: `public sealed record BackupFile(string FullPath, string RelativePath, long Size, DateTime LastWriteTimeUtc);` — every later task that constructs a `BackupFile` uses this 4-arg form.

- [ ] **Step 1: Extend the `BackupFile` record**

In `src/SparkVault.Core/Models.cs`, change:
```csharp
public sealed record BackupFile(string FullPath, string RelativePath, long Size);
```
to:
```csharp
public sealed record BackupFile(string FullPath, string RelativePath, long Size, DateTime LastWriteTimeUtc);
```

- [ ] **Step 2: `FileScanner.Scan` reads the timestamp**

In `src/SparkVault.Core/FileScanner.cs`, change:
```csharp
            result.Add(new BackupFile(fullPath, relativePath, new FileInfo(fullPath).Length));
```
to:
```csharp
            var info = new FileInfo(fullPath);
            result.Add(new BackupFile(fullPath, relativePath, info.Length, info.LastWriteTimeUtc));
```

- [ ] **Step 3: Update every existing `new BackupFile(...)` call site to pass the timestamp**

Each of the following currently reads `new BackupFile(filePath, "<relpath>", new FileInfo(filePath).Length)` (or the `data.LongLength` variant). Add a fourth argument `new FileInfo(filePath).LastWriteTimeUtc` (or, since some sites already hold a `FileInfo`-derived length, just append `, new FileInfo(filePath).LastWriteTimeUtc`) in every occurrence:

- `tests/SparkVault.Core.Tests/LocalTargetTests.cs` — lines 17, 43, 76, 140 (line 76 uses `data.LongLength`, not `new FileInfo(filePath).Length` — leave the size argument as-is, only append the timestamp argument, e.g. `var file = new BackupFile(filePath, "big.bin", data.LongLength, new FileInfo(filePath).LastWriteTimeUtc);`)
- `tests/SparkVault.Core.Tests/FtpTargetTests.cs` — lines 37, 71, 93, 117, 159
- `tests/SparkVault.Core.Tests/SftpTargetTests.cs` — lines 34, 64, 88, 120, 124, 149
- `tests/SparkVault.Core.Tests/S3TargetTests.cs` — lines 87, 120, 146, 206

Example transform (line 17 in `LocalTargetTests.cs`):
```csharp
// before
var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);
// after
var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);
```

- [ ] **Step 4: Add timestamp coverage to `FileScannerTests`**

In `tests/SparkVault.Core.Tests/FileScannerTests.cs`, add an assertion to the existing
`ScansFilesAndAppliesExclusions` test, right after the existing `Assert.Contains(result, f => f.RelativePath == "keep.txt" && f.Size == 5);` line:
```csharp
            var keepFile = result.Single(f => f.RelativePath == "keep.txt");
            Assert.True(DateTime.UtcNow - keepFile.LastWriteTimeUtc < TimeSpan.FromMinutes(1));
```

- [ ] **Step 5: Build and test**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds, all existing tests still pass (no behavior change yet, only a widened record + one new assertion).

- [ ] **Step 6: Commit**

```bash
git add src/SparkVault.Core/Models.cs src/SparkVault.Core/FileScanner.cs tests/SparkVault.Core.Tests/FileScannerTests.cs tests/SparkVault.Core.Tests/LocalTargetTests.cs tests/SparkVault.Core.Tests/FtpTargetTests.cs tests/SparkVault.Core.Tests/SftpTargetTests.cs tests/SparkVault.Core.Tests/S3TargetTests.cs
git commit -m "Core: BackupFile carries the source file's LastWriteTimeUtc"
```

---

### Task 2: `RunFileRecord` gains `SourceModifiedUtc`, `RunFiles` schema migration

**Files:**
- Modify: `src/SparkVault.Core/Models.cs`
- Modify: `src/SparkVault.Core/SparkVaultDatabase.cs`
- Modify: `src/SparkVault.Core/RunFileRepository.cs`
- Modify: `src/SparkVault.Core/BackupRunner.cs`
- Modify: `tests/SparkVault.Core.Tests/RunFileRepositoryTests.cs`

**Interfaces:**
- Consumes: `BackupFile.LastWriteTimeUtc` (Task 1).
- Produces: `public sealed record RunFileRecord(string RelativePath, long Size, DateTime? SourceModifiedUtc);` and `public sealed record ManifestEntry(string RelativePath, long Size, DateTime? SourceModifiedUtc);` — both used by Tasks 3, 5, 6, 7, 8.

- [ ] **Step 1: Extend `RunFileRecord`, add `ManifestEntry`**

In `src/SparkVault.Core/Models.cs`, change:
```csharp
public sealed record RunFileRecord(string RelativePath, long Size);
```
to:
```csharp
public sealed record RunFileRecord(string RelativePath, long Size, DateTime? SourceModifiedUtc);

// Shape of one entry in "the last known catalog for a target" — identical fields to
// RunFileRecord, kept as its own type so IncrementalPlanner (Task 3) has no dependency on the
// DB-record type and stays a pure, DB-free function.
public sealed record ManifestEntry(string RelativePath, long Size, DateTime? SourceModifiedUtc);
```

- [ ] **Step 2: Schema migration for `RunFiles.SourceModifiedUtc`**

In `src/SparkVault.Core/SparkVaultDatabase.cs`, add `SourceModifiedUtc TEXT NULL` to the
`CREATE TABLE IF NOT EXISTS RunFiles` statement:
```sql
            CREATE TABLE IF NOT EXISTS RunFiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                RelativePath TEXT NOT NULL,
                Size INTEGER NOT NULL,
                SourceModifiedUtc TEXT NULL
            );
```
and add an `EnsureColumn` call next to the existing two (for databases created before this task):
```csharp
        EnsureColumn(connection, "Jobs", "WeeklyDay", "TEXT NULL");
        EnsureColumn(connection, "Jobs", "MonthlyDay", "INTEGER NULL");
        EnsureColumn(connection, "RunFiles", "SourceModifiedUtc", "TEXT NULL");
```

- [ ] **Step 3: `RunFileRepository` reads/writes the new column**

In `src/SparkVault.Core/RunFileRepository.cs`, `AddRange`: add the column to the INSERT and bind it
(nullable — use `DBNull.Value` when the record's value is `null`; format with `"O"` round-trip,
matching `RunRepository.BindRunParameters`'s treatment of `StartedAt`/`EndedAt`):
```csharp
        command.CommandText = """
            INSERT INTO RunFiles (RunId, RelativePath, Size, SourceModifiedUtc)
            VALUES ($runId, $relativePath, $size, $sourceModifiedUtc);
            """;
        var runIdParam = command.Parameters.Add("$runId", SqliteType.Integer);
        var relativePathParam = command.Parameters.Add("$relativePath", SqliteType.Text);
        var sizeParam = command.Parameters.Add("$size", SqliteType.Integer);
        var sourceModifiedUtcParam = command.Parameters.Add("$sourceModifiedUtc", SqliteType.Text);

        foreach (var file in files)
        {
            runIdParam.Value = runId;
            relativePathParam.Value = file.RelativePath;
            sizeParam.Value = file.Size;
            sourceModifiedUtcParam.Value = (object?)file.SourceModifiedUtc?.ToString("O") ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
```
`GetByRunId`: add the column to the SELECT and the constructed record:
```csharp
        command.CommandText = "SELECT RelativePath, Size, SourceModifiedUtc FROM RunFiles WHERE RunId = $runId;";
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        var results = new List<RunFileRecord>();
        while (reader.Read())
            results.Add(new RunFileRecord(
                reader.GetString(reader.GetOrdinal("RelativePath")),
                reader.GetInt64(reader.GetOrdinal("Size")),
                reader.IsDBNull(reader.GetOrdinal("SourceModifiedUtc"))
                    ? null
                    : DateTime.Parse(reader.GetString(reader.GetOrdinal("SourceModifiedUtc")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));
```

- [ ] **Step 4: `BackupRunner`'s one `RunFileRecord` construction site**

In `src/SparkVault.Core/BackupRunner.cs`, change:
```csharp
                uploaded.Add(new RunFileRecord(file.RelativePath, file.Size));
```
to:
```csharp
                uploaded.Add(new RunFileRecord(file.RelativePath, file.Size, file.LastWriteTimeUtc));
```

- [ ] **Step 5: Update `RunFileRepositoryTests`**

In `tests/SparkVault.Core.Tests/RunFileRepositoryTests.cs`, `AddRangeThenGetByRunId_RoundTripsAllFiles`:
change the two record literals to include a timestamp, and assert it round-trips:
```csharp
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
```

- [ ] **Step 6: Build and test**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/SparkVault.Core/Models.cs src/SparkVault.Core/SparkVaultDatabase.cs src/SparkVault.Core/RunFileRepository.cs src/SparkVault.Core/BackupRunner.cs tests/SparkVault.Core.Tests/RunFileRepositoryTests.cs
git commit -m "Core: RunFiles catalog records the source file's modification time"
```

---

### Task 3: `JobFileScanner` + `IncrementalPlanner`

**Files:**
- Create: `src/SparkVault.Core/JobFileScanner.cs`
- Create: `src/SparkVault.Core/IncrementalPlanner.cs`
- Modify: `src/SparkVault.Core/BackupRunner.cs`
- Create: `tests/SparkVault.Core.Tests/IncrementalPlannerTests.cs`

**Interfaces:**
- Consumes: `BackupFile` (Task 1), `ManifestEntry` (Task 2).
- Produces: `JobFileScanner.Scan(BackupJob job) -> IReadOnlyList<BackupFile>` (used by Task 6's `BackupRunner` and Task 8's `OnChangeJobChecker`). `IncrementalPlanner.Compute(IReadOnlyList<BackupFile>, IReadOnlyList<ManifestEntry>) -> IncrementalPlanner.Plan { ToUpload, Unchanged, ToQuarantine }` and `IncrementalPlanner.HasChanges(...) -> bool` (used by Task 6 and Task 8).

- [ ] **Step 1: Create `JobFileScanner`**

Create `src/SparkVault.Core/JobFileScanner.cs`:
```csharp
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
```

- [ ] **Step 2: `BackupRunner` uses `JobFileScanner` instead of its own inline copy**

In `src/SparkVault.Core/BackupRunner.cs`, change:
```csharp
                var scanned = FileScanner.Scan(job.SourcePath, job.ExcludePatterns);
                var jobFolder = SanitizeForPath(job.Name);
                files = scanned.Select(f => f with { RelativePath = $"{jobFolder}\\{f.RelativePath}" }).ToList();
```
to:
```csharp
                files = JobFileScanner.Scan(job);
```
Then delete `BackupRunner`'s now-unused private `SanitizeForPath` method entirely (the one with the
"Every file lands under a job-named subfolder..." comment — it has moved to `JobFileScanner`
verbatim in Step 1).

- [ ] **Step 3: Create `IncrementalPlanner`**

Create `src/SparkVault.Core/IncrementalPlanner.cs`:
```csharp
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
```

- [ ] **Step 4: Write `IncrementalPlannerTests`**

Create `tests/SparkVault.Core.Tests/IncrementalPlannerTests.cs`:
```csharp
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
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test --filter FullyQualifiedName~IncrementalPlannerTests`
Expected: 9 passed.

- [ ] **Step 6: Build and test everything**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/SparkVault.Core/JobFileScanner.cs src/SparkVault.Core/IncrementalPlanner.cs src/SparkVault.Core/BackupRunner.cs tests/SparkVault.Core.Tests/IncrementalPlannerTests.cs
git commit -m "Core: extract JobFileScanner, add IncrementalPlanner for change detection"
```

---

### Task 4: `VerifyTargetBeforeRun`, `QuarantinedFiles` table, `QuarantineRepository`, `RunRepository.GetLatestSuccessfulRun`

**Files:**
- Modify: `src/SparkVault.Core/Models.cs`
- Modify: `src/SparkVault.Core/SparkVaultDatabase.cs`
- Modify: `src/SparkVault.Core/JobRepository.cs`
- Modify: `src/SparkVault.Core/RunRepository.cs`
- Create: `src/SparkVault.Core/QuarantineRepository.cs`
- Modify: `tests/SparkVault.Core.Tests/JobRepositoryTests.cs`
- Modify: `tests/SparkVault.Core.Tests/RunRepositoryTests.cs`
- Create: `tests/SparkVault.Core.Tests/QuarantineRepositoryTests.cs`

**Interfaces:**
- Produces: `BackupJob.VerifyTargetBeforeRun` (bool), `RunRepository.GetLatestSuccessfulRun(int jobId, int targetId) -> BackupRun?`, `QuarantineRepository.Add(...)`/`GetLatestQuarantinePath(...)` — all consumed by Tasks 6 and 7.

- [ ] **Step 1: `BackupJob.VerifyTargetBeforeRun`**

In `src/SparkVault.Core/Models.cs`, add to `BackupJob`:
```csharp
    public bool VerifyTargetBeforeRun { get; set; }
```

- [ ] **Step 2: Schema — `Jobs.VerifyTargetBeforeRun` and `QuarantinedFiles`**

In `src/SparkVault.Core/SparkVaultDatabase.cs`, add `VerifyTargetBeforeRun INTEGER NOT NULL DEFAULT 0`
to the `CREATE TABLE IF NOT EXISTS Jobs` statement (after `MonthlyDay INTEGER NULL`):
```sql
            CREATE TABLE IF NOT EXISTS Jobs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                ExcludePatterns TEXT NOT NULL,
                ScheduleType TEXT NOT NULL,
                IntervalHours INTEGER NULL,
                DailyAtTime TEXT NULL,
                WeeklyDay TEXT NULL,
                MonthlyDay INTEGER NULL,
                VerifyTargetBeforeRun INTEGER NOT NULL DEFAULT 0
            );
```
Add the matching `EnsureColumn` call next to the existing ones:
```csharp
        EnsureColumn(connection, "RunFiles", "SourceModifiedUtc", "TEXT NULL");
        EnsureColumn(connection, "Jobs", "VerifyTargetBeforeRun", "INTEGER NOT NULL DEFAULT 0");
```
Add the new table (goes in the same multi-statement `command.CommandText` block, after the
`CREATE INDEX IF NOT EXISTS IX_RunFiles_RunId` line, before the closing `"""`):
```sql
            CREATE TABLE IF NOT EXISTS QuarantinedFiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                TargetId INTEGER NOT NULL,
                OriginalRelativePath TEXT NOT NULL,
                QuarantinePath TEXT NOT NULL,
                QuarantinedAtRunId INTEGER NOT NULL,
                QuarantinedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_Lookup ON QuarantinedFiles(JobId, TargetId, OriginalRelativePath);
```

- [ ] **Step 3: `JobRepository` reads/writes `VerifyTargetBeforeRun`**

In `src/SparkVault.Core/JobRepository.cs`:
- `Add`'s INSERT: add `VerifyTargetBeforeRun` to the column list and `$verifyTargetBeforeRun` to
  the VALUES list.
- `Update`'s SQL: add `, VerifyTargetBeforeRun = $verifyTargetBeforeRun` to the SET clause.
- `BindJobParameters`: add
  `command.Parameters.AddWithValue("$verifyTargetBeforeRun", job.VerifyTargetBeforeRun ? 1 : 0);`
- `ReadJob`: add
  `VerifyTargetBeforeRun = reader.GetInt32(reader.GetOrdinal("VerifyTargetBeforeRun")) != 0,`

- [ ] **Step 4: `RunRepository.GetLatestSuccessfulRun`**

In `src/SparkVault.Core/RunRepository.cs`, add a new public method (place it after
`GetLatestByJobId`):
```csharp
    public BackupRun? GetLatestSuccessfulRun(int jobId, int targetId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Runs WHERE JobId = $jobId AND TargetId = $targetId AND Status = 'Success' ORDER BY StartedAt DESC LIMIT 1;";
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$targetId", targetId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }
```

- [ ] **Step 5: Create `QuarantineRepository`**

Create `src/SparkVault.Core/QuarantineRepository.cs`:
```csharp
using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class QuarantineRepository
{
    private readonly string _connectionString;

    public QuarantineRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
    }

    public void Add(int jobId, int targetId, string originalRelativePath, string quarantinePath, int quarantinedAtRunId, DateTime quarantinedAtUtc)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO QuarantinedFiles (JobId, TargetId, OriginalRelativePath, QuarantinePath, QuarantinedAtRunId, QuarantinedAtUtc)
            VALUES ($jobId, $targetId, $originalRelativePath, $quarantinePath, $quarantinedAtRunId, $quarantinedAtUtc);
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$originalRelativePath", originalRelativePath);
        command.Parameters.AddWithValue("$quarantinePath", quarantinePath);
        command.Parameters.AddWithValue("$quarantinedAtRunId", quarantinedAtRunId);
        command.Parameters.AddWithValue("$quarantinedAtUtc", quarantinedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    // Most recently quarantined location wins — the realistic case is a file quarantined at
    // most once; if the same relative path was quarantined more than once over time (created,
    // deleted, recreated, deleted again), the newest move is the one still findable on the target.
    public string? GetLatestQuarantinePath(int jobId, int targetId, string originalRelativePath)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT QuarantinePath FROM QuarantinedFiles
            WHERE JobId = $jobId AND TargetId = $targetId AND OriginalRelativePath = $originalRelativePath
            ORDER BY QuarantinedAtUtc DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$originalRelativePath", originalRelativePath);

        var result = command.ExecuteScalar();
        return result as string;
    }
}
```

- [ ] **Step 6: `JobRepositoryTests` — roundtrip `VerifyTargetBeforeRun`**

In `tests/SparkVault.Core.Tests/JobRepositoryTests.cs`, add a new test after
`AddThenGetById_RoundTripsAllFieldsAndTargets`:
```csharp
    [Fact]
    public void AddThenGetById_RoundTripsVerifyTargetBeforeRun()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);

            var id = repo.Add(new BackupJob
            {
                Name = "Verified",
                SourcePath = "C:\\a",
                VerifyTargetBeforeRun = true,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\a1" } },
            });

            Assert.True(repo.GetById(id)!.VerifyTargetBeforeRun);

            var job = repo.GetById(id)!;
            job.VerifyTargetBeforeRun = false;
            repo.Update(job);

            Assert.False(repo.GetById(id)!.VerifyTargetBeforeRun);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
```

- [ ] **Step 7: `RunRepositoryTests` — `GetLatestSuccessfulRun`**

In `tests/SparkVault.Core.Tests/RunRepositoryTests.cs`, add a new test after
`GetLatestByJobId_ReturnsMostRecentStart`:
```csharp
    [Fact]
    public void GetLatestSuccessfulRun_IgnoresFailedRunsAndOtherTargets()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "A",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = "D:\\a" },
                    new() { Type = TargetType.Local, DestinationPath = "D:\\b" },
                },
            });
            var targetIds = jobRepo.GetById(jobId)!.Targets.Select(t => t.Id).ToList();

            var runRepo = new RunRepository(connectionString);
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetIds[0], RunGroupId = Guid.NewGuid(), StartedAt = DateTime.UtcNow.AddHours(-2), Status = RunStatus.Success });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetIds[0], RunGroupId = Guid.NewGuid(), StartedAt = DateTime.UtcNow.AddHours(-1), Status = RunStatus.Failed });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetIds[1], RunGroupId = Guid.NewGuid(), StartedAt = DateTime.UtcNow, Status = RunStatus.Success });

            var latest = runRepo.GetLatestSuccessfulRun(jobId, targetIds[0]);

            Assert.NotNull(latest);
            Assert.Equal(RunStatus.Success, latest!.Status);
            Assert.Equal(targetIds[0], latest.TargetId);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestSuccessfulRun_NoSuccessfulRuns_ReturnsNull()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var runRepo = new RunRepository(connectionString);

            Assert.Null(runRepo.GetLatestSuccessfulRun(999, 999));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
```

- [ ] **Step 8: Create `QuarantineRepositoryTests`**

Create `tests/SparkVault.Core.Tests/QuarantineRepositoryTests.cs`:
```csharp
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
```

- [ ] **Step 9: Build and test**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds, all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/SparkVault.Core/Models.cs src/SparkVault.Core/SparkVaultDatabase.cs src/SparkVault.Core/JobRepository.cs src/SparkVault.Core/RunRepository.cs src/SparkVault.Core/QuarantineRepository.cs tests/SparkVault.Core.Tests/JobRepositoryTests.cs tests/SparkVault.Core.Tests/RunRepositoryTests.cs tests/SparkVault.Core.Tests/QuarantineRepositoryTests.cs
git commit -m "Core: add VerifyTargetBeforeRun, QuarantinedFiles table, and their repositories"
```

---

### Task 5: `IBackupTarget.MoveAsync` — four implementations

**Files:**
- Modify: `src/SparkVault.Core/IBackupTarget.cs`
- Modify: `src/SparkVault.Core/LocalTarget.cs`
- Modify: `src/SparkVault.Core/SftpTarget.cs`
- Modify: `src/SparkVault.Core/FtpTarget.cs`
- Modify: `src/SparkVault.Core/S3Target.cs`
- Modify: `tests/SparkVault.Core.Tests/LocalTargetTests.cs`
- Modify: `tests/SparkVault.Core.Tests/SftpTargetTests.cs`
- Modify: `tests/SparkVault.Core.Tests/FtpTargetTests.cs`
- Modify: `tests/SparkVault.Core.Tests/S3TargetTests.cs`

**Interfaces:**
- Produces: `IBackupTarget.MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct) -> Task`, implemented by all four targets — consumed by Task 6 (`BackupRunner`'s quarantine step).

- [ ] **Step 1: Add `MoveAsync` to the interface**

In `src/SparkVault.Core/IBackupTarget.cs`, add after `DeleteAsync`:
```csharp
    Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct);
```

- [ ] **Step 2: `LocalTarget.MoveAsync`**

In `src/SparkVault.Core/LocalTarget.cs`, add after `DeleteAsync`:
```csharp
    public Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fromPath = Path.Combine(_destinationRoot, fromRelativePath);
        if (!File.Exists(fromPath)) return Task.CompletedTask;
        var toPath = Path.Combine(_destinationRoot, toRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(toPath)!);
        File.Move(fromPath, toPath, overwrite: true);
        return Task.CompletedTask;
    }
```

- [ ] **Step 3: `SftpTarget.MoveAsync`**

In `src/SparkVault.Core/SftpTarget.cs`, add after `DeleteAsync`:
```csharp
    public async Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureConnectedAsync(ct);
        var fromPath = RemotePath(fromRelativePath);
        var toPath = RemotePath(toRelativePath);
        await Task.Run(() =>
        {
            if (!_client.Exists(fromPath)) return;
            var remoteDir = toPath[..toPath.LastIndexOf('/')];
            if (remoteDir.Length == 0) remoteDir = "/";
            if (!_client.Exists(remoteDir)) CreateDirectoryRecursive(remoteDir);
            _client.RenameFile(fromPath, toPath, isPosix: true);
        }, ct);
    }
```

- [ ] **Step 4: `FtpTarget.MoveAsync`**

In `src/SparkVault.Core/FtpTarget.cs`, add after `DeleteAsync`:
```csharp
    public async Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureConnectedAsync(ct);
        var fromPath = RemotePath(fromRelativePath);
        var toPath = RemotePath(toRelativePath);
        if (!await _client.FileExists(fromPath, ct)) return;
        var remoteDir = toPath[..toPath.LastIndexOf('/')];
        if (!await _client.DirectoryExists(remoteDir, ct))
            await _client.CreateDirectory(remoteDir, ct);
        await _client.Rename(fromPath, toPath, ct);
    }
```
**Verify against the real FTP test container** (see Step 8 below) whether `CreateDirectory`
creates missing *intermediate* segments too (e.g. `_deleted/20260824-100000` when neither
`_deleted` nor its child existed). If the nested-subfolder `MoveAsync` test from Step 8 fails
because an intermediate segment is missing, replace the single `CreateDirectory` call with a
segment-by-segment loop mirroring `SftpTarget.CreateDirectoryRecursive`:
```csharp
        var parts = remoteDir.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!await _client.DirectoryExists(current, ct))
                await _client.CreateDirectory(current, ct);
        }
```

- [ ] **Step 5: `S3Target.MoveAsync`**

In `src/SparkVault.Core/S3Target.cs`, add after `DeleteAsync`:
```csharp
    public async Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureBucketAsync(ct);
        var fromKey = RemoteKey(fromRelativePath);
        var toKey = RemoteKey(toRelativePath);
        try
        {
            await _client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = fromKey,
                DestinationBucket = _bucket,
                DestinationKey = toKey,
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return; // source no longer exists — nothing to move
        }
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = fromKey }, ct);
    }
```

- [ ] **Step 6: `LocalTargetTests` — `MoveAsync` test**

In `tests/SparkVault.Core.Tests/LocalTargetTests.cs`, add after `DownloadAsync_UploadedFile_WritesIdenticalContentToDestination`:
```csharp
    [Fact]
    public async Task MoveAsync_UploadedFile_MovesToNewRelativePathWithinDestination()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-local-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-local-dest-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "move me");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            var target = new LocalTarget(destDir.FullName);
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            await target.MoveAsync("a.txt", "_deleted\\20260824-100000\\a.txt", CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(destDir.FullName, "a.txt")));
            var movedPath = Path.Combine(destDir.FullName, "_deleted", "20260824-100000", "a.txt");
            Assert.True(File.Exists(movedPath));
            Assert.Equal("move me", await File.ReadAllTextAsync(movedPath));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 7: `SftpTargetTests` — `MoveAsync` test**

In `tests/SparkVault.Core.Tests/SftpTargetTests.cs`, add after `DownloadAsync_UploadedFile_WritesIdenticalContentToDestination`:
```csharp
    [Fact]
    public async Task MoveAsync_UploadedFile_MovesToNewRelativePath()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "move me sftp");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new SftpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            await target.MoveAsync("a.txt", "_deleted/20260824-100000/a.txt", CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "a.txt");
            Assert.Contains(listed, f => f.Path == "_deleted/20260824-100000/a.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 8: `FtpTargetTests` — `MoveAsync` test**

In `tests/SparkVault.Core.Tests/FtpTargetTests.cs`, add after `DownloadAsync_UploadedFile_WritesIdenticalContentToDestination`:
```csharp
    [Fact]
    public async Task MoveAsync_UploadedFile_MovesToNewRelativePath()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "move me ftp");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new FtpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            await target.MoveAsync("a.txt", "_deleted/20260824-100000/a.txt", CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "a.txt");
            Assert.Contains(listed, f => f.Path == "_deleted/20260824-100000/a.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }
```
Run this test against the real Docker container (`docker compose -f docker/docker-compose.test.yml up -d`
if not already running) as the verification called for in Step 4.

- [ ] **Step 9: `S3TargetTests` — `MoveAsync` test**

In `tests/SparkVault.Core.Tests/S3TargetTests.cs`, add after `DownloadAsync_UploadedFile_WritesIdenticalContentToDestination`:
```csharp
    [Fact]
    public async Task MoveAsync_UploadedFile_MovesToNewKeyServerSide()
    {
        if (!DockerTestHelper.IsReachable("127.0.0.1", Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-s3-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "move me s3");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            var config = NewTestConfig();
            await CreateBucketAsync(config.Bucket!);
            await using var target = new S3Target(config);
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            await target.MoveAsync("a.txt", "_deleted/20260824-100000/a.txt", CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "a.txt");
            Assert.Contains(listed, f => f.Path == "_deleted/20260824-100000/a.txt" && f.Size == file.Size);
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MoveAsync_SourceKeyDoesNotExist_DoesNotThrow()
    {
        if (!DockerTestHelper.IsReachable("127.0.0.1", Port)) return;

        var config = NewTestConfig();
        await CreateBucketAsync(config.Bucket!);
        await using var target = new S3Target(config);

        await target.MoveAsync("never-uploaded.txt", "_deleted/x/never-uploaded.txt", CancellationToken.None);
        // No exception is the assertion — nothing to move is not an error condition.
    }
```
This is the MinIO round-trip verification the spec (section 7) flags as not yet independently
confirmed — run it against the real Docker container before moving on.

- [ ] **Step 10: Build and test**

Run: `docker compose -f docker/docker-compose.test.yml up -d` (if not already running), then
`dotnet build` and `dotnet test`.
Expected: build succeeds, all tests pass including the four new `MoveAsync` tests (Docker-gated
ones only run if the containers are reachable — confirm they actually ran, not silently skipped,
by checking they appear in the test output with a real pass, not just "0 failed" on an empty
subset).

- [ ] **Step 11: Commit**

```bash
git add src/SparkVault.Core/IBackupTarget.cs src/SparkVault.Core/LocalTarget.cs src/SparkVault.Core/SftpTarget.cs src/SparkVault.Core/FtpTarget.cs src/SparkVault.Core/S3Target.cs tests/SparkVault.Core.Tests/LocalTargetTests.cs tests/SparkVault.Core.Tests/SftpTargetTests.cs tests/SparkVault.Core.Tests/FtpTargetTests.cs tests/SparkVault.Core.Tests/S3TargetTests.cs
git commit -m "Core: add IBackupTarget.MoveAsync for server-side quarantine moves"
```

---

### Task 6: `BackupRunner` — skip unchanged uploads, quarantine deletions

**Files:**
- Modify: `src/SparkVault.Core/BackupRunner.cs`
- Modify: `src/SparkVault.App/App.xaml.cs`
- Modify: `tests/SparkVault.Core.Tests/BackupRunnerTests.cs`
- Modify: `tests/SparkVault.Core.Tests/BackgroundSchedulerTests.cs`
- Modify: `tests/SparkVault.Core.Tests/RestoreRunnerTests.cs`

**Interfaces:**
- Consumes: `IncrementalPlanner` (Task 3), `RunRepository.GetLatestSuccessfulRun` (Task 4), `QuarantineRepository` (Task 4), `IBackupTarget.MoveAsync` (Task 5), `BackupJob.VerifyTargetBeforeRun` (Task 4).
- Produces: `BackupRunner(RunRepository, RunFileRepository, QuarantineRepository, ILogger)` — the new 4-arg constructor every call site below (and Task 7/9) must use.

- [ ] **Step 1: `BackupRunner` constructor takes `QuarantineRepository`**

In `src/SparkVault.Core/BackupRunner.cs`, add a field and constructor parameter:
```csharp
    private readonly QuarantineRepository _quarantineRepository;
```
```csharp
    public BackupRunner(RunRepository runRepository, RunFileRepository runFileRepository, QuarantineRepository quarantineRepository, ILogger logger)
    {
        _runRepository = runRepository;
        _runFileRepository = runFileRepository;
        _quarantineRepository = quarantineRepository;
        _logger = logger;
    }
```

- [ ] **Step 2: Rewrite `RunForTargetAsync`'s file-transfer body**

In `src/SparkVault.Core/BackupRunner.cs`, `RunForTargetAsync` currently (after the successful
`TestConnectionAsync` check) does:
```csharp
            long totalBytes = files.Sum(f => f.Size);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (pauseToken is not null) await pauseToken.WaitIfPausedAsync(ct);
                progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));
                await target.UploadAsync(file, progress, ct);
                done++;
                bytesDone += file.Size;
                uploaded.Add(new RunFileRecord(file.RelativePath, file.Size, file.LastWriteTimeUtc));
                progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));
            }

            _runFileRepository.AddRange(run.Id, uploaded);
            run.Status = RunStatus.Success;
            _logger.Information("Job {JobName} -> {Target} completed: {FileCount} files, {TotalBytes} bytes",
                job.Name, targetConfig.Describe(), done, bytesDone);
```
Replace it with:
```csharp
            var previousManifest = GetPreviousManifest(job.Id, targetConfig.Id);

            if (job.VerifyTargetBeforeRun)
            {
                var remotePaths = (await target.ListExistingAsync(ct)).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
                previousManifest = previousManifest.Where(m => remotePaths.Contains(m.RelativePath)).ToList();
            }

            var plan = IncrementalPlanner.Compute(files, previousManifest);
            long totalBytes = plan.ToUpload.Sum(f => f.Size);

            foreach (var file in plan.Unchanged)
                uploaded.Add(new RunFileRecord(file.RelativePath, file.Size, file.LastWriteTimeUtc));

            foreach (var file in plan.ToUpload)
            {
                ct.ThrowIfCancellationRequested();
                if (pauseToken is not null) await pauseToken.WaitIfPausedAsync(ct);
                progress?.Report(new TransferProgress(done, plan.ToUpload.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));
                await target.UploadAsync(file, progress, ct);
                done++;
                bytesDone += file.Size;
                uploaded.Add(new RunFileRecord(file.RelativePath, file.Size, file.LastWriteTimeUtc));
                progress?.Report(new TransferProgress(done, plan.ToUpload.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));
            }

            var quarantinedCount = 0;
            foreach (var entry in plan.ToQuarantine)
            {
                var quarantinePath = $"_deleted\\{run.StartedAt:yyyyMMdd-HHmmss}\\{entry.RelativePath}";
                try
                {
                    await target.MoveAsync(entry.RelativePath, quarantinePath, ct);
                    _quarantineRepository.Add(job.Id, targetConfig.Id, entry.RelativePath, quarantinePath, run.Id, DateTime.UtcNow);
                    quarantinedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Quarantäne fehlgeschlagen für {Path} ({JobName} -> {Target})",
                        entry.RelativePath, job.Name, targetConfig.Describe());
                }
            }

            _runFileRepository.AddRange(run.Id, uploaded);
            run.Status = RunStatus.Success;
            _logger.Information(
                "Job {JobName} -> {Target} completed: {NewOrChanged} neu/geändert, {Unchanged} unverändert übersprungen, {Quarantined} in Quarantäne, {TotalBytes} Bytes übertragen",
                job.Name, targetConfig.Describe(), plan.ToUpload.Count, plan.Unchanged.Count, quarantinedCount, bytesDone);
```
Note: `done`/`bytesDone` (used by the `finally` block for `run.FileCount`/`run.TotalBytes`, unchanged)
now only ever count `plan.ToUpload` — this is intentional (see spec section 6): these fields keep
meaning "what was actually transferred in this run".

- [ ] **Step 3: Add the `GetPreviousManifest` helper**

In `src/SparkVault.Core/BackupRunner.cs`, add a new private method (near `SanitizeForPath`'s old
location, now removed — put it near the other private helpers at the bottom of the class):
```csharp
    private IReadOnlyList<ManifestEntry> GetPreviousManifest(int jobId, int targetId)
    {
        var lastSuccessful = _runRepository.GetLatestSuccessfulRun(jobId, targetId);
        if (lastSuccessful is null) return Array.Empty<ManifestEntry>();
        return _runFileRepository.GetByRunId(lastSuccessful.Id)
            .Select(f => new ManifestEntry(f.RelativePath, f.Size, f.SourceModifiedUtc))
            .ToList();
    }
```

- [ ] **Step 4: `App.xaml.cs` — construct `QuarantineRepository`, update `Runner` construction**

In `src/SparkVault.App/App.xaml.cs`:
- Add a new static property next to the existing repository properties:
  `public static QuarantineRepository QuarantineRepository { get; private set; } = null!;`
- After the existing `RunFileRepository = new RunFileRepository(connectionString);` line, add:
  `QuarantineRepository = new QuarantineRepository(connectionString);`
- Change `Runner = new BackupRunner(RunRepository, RunFileRepository, Log.Logger);` to:
  `Runner = new BackupRunner(RunRepository, RunFileRepository, QuarantineRepository, Log.Logger);`

- [ ] **Step 5: Update every `new BackupRunner(...)` test call site to the 4-arg constructor**

Every occurrence below currently reads `new BackupRunner(runRepo, runFileRepo, Log.Logger)`.
Insert a `QuarantineRepository` instance as the third argument. Each of these files already
constructs a `connectionString` in scope — add `var quarantineRepo = new QuarantineRepository(connectionString);`
immediately before the `new BackupRunner(...)` call, then change the call to
`new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger)`.

- `tests/SparkVault.Core.Tests/BackupRunnerTests.cs` — lines 38, 84, 133, 169, 211, 273, 337, 388, 427, 478, 518 (11 occurrences)
- `tests/SparkVault.Core.Tests/BackgroundSchedulerTests.cs` — line 38 (1 occurrence)
- `tests/SparkVault.Core.Tests/RestoreRunnerTests.cs` — lines 39, 84, 148, 207 (4 occurrences; these
  construct a `BackupRunner` only as setup before testing `RestoreRunner` — same mechanical change)

- [ ] **Step 6: `BackupRunnerTests` — incremental-specific tests**

In `tests/SparkVault.Core.Tests/BackupRunnerTests.cs`, add three new tests after
`RunAsync_FailedRun_WritesNoRunFilesManifest`:
```csharp
    [Fact]
    public async Task RunAsync_SecondRunWithNoChanges_TransfersNothingButKeepsFullManifest()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(srcDir.FullName, "b.txt"), "world!");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            var firstResults = await runner.RunAsync(job, progress: null, CancellationToken.None);
            var secondResults = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(0, secondResults[0].FileCount);
            Assert.Equal(0, secondResults[0].TotalBytes);
            var manifest = runFileRepo.GetByRunId(secondResults[0].Id);
            Assert.Equal(2, manifest.Count);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_ChangedFile_IsReUploaded()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await runner.RunAsync(job, progress: null, CancellationToken.None);

            // Ensure a strictly later, distinguishable LastWriteTimeUtc than the first write.
            await Task.Delay(50);
            File.WriteAllText(filePath, "hello there, much longer content now");

            var secondResults = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(1, secondResults[0].FileCount);
            Assert.Equal("hello there, much longer content now".Length, secondResults[0].TotalBytes);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_DeletedSourceFile_IsQuarantinedAndDroppedFromNewManifest()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await runner.RunAsync(job, progress: null, CancellationToken.None);
            File.Delete(filePath);

            var secondResults = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Empty(runFileRepo.GetByRunId(secondResults[0].Id));
            Assert.False(File.Exists(Path.Combine(destDir.FullName, "Test", "a.txt")));
            Assert.True(Directory.Exists(Path.Combine(destDir.FullName, "_deleted")));
            var quarantinePath = quarantineRepo.GetLatestQuarantinePath(jobId, job.Targets[0].Id, "Test\\a.txt");
            Assert.NotNull(quarantinePath);
            Assert.True(File.Exists(Path.Combine(destDir.FullName, quarantinePath!)));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
```

- [ ] **Step 7: Build and test**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds, all tests pass (including the 3 new tests and the untouched
pre-existing `BackupRunnerTests`/`BackgroundSchedulerTests`/`RestoreRunnerTests` suites, now
compiling against the 4-arg constructor).

- [ ] **Step 8: Commit**

```bash
git add src/SparkVault.Core/BackupRunner.cs src/SparkVault.App/App.xaml.cs tests/SparkVault.Core.Tests/BackupRunnerTests.cs tests/SparkVault.Core.Tests/BackgroundSchedulerTests.cs tests/SparkVault.Core.Tests/RestoreRunnerTests.cs
git commit -m "Core: BackupRunner skips unchanged files and quarantines deletions"
```

---

### Task 7: `RestoreRunner` — quarantine fallback

**Files:**
- Modify: `src/SparkVault.Core/RestoreRunner.cs`
- Modify: `src/SparkVault.App/MainWindow.xaml.cs`
- Modify: `tests/SparkVault.Core.Tests/RestoreRunnerTests.cs`

**Interfaces:**
- Consumes: `QuarantineRepository.GetLatestQuarantinePath` (Task 4).
- Produces: `RestoreRunner(RunFileRepository, QuarantineRepository, SemaphoreSlim, ILogger)` — the new constructor shape every call site below must use.

- [ ] **Step 1: `RestoreRunner` constructor takes `QuarantineRepository`**

In `src/SparkVault.Core/RestoreRunner.cs`, add a field and constructor parameter (matching the
existing parameter order style — insert `quarantineRepository` right after `runFileRepository`):
```csharp
    private readonly QuarantineRepository _quarantineRepository;
```
```csharp
    public RestoreRunner(RunFileRepository runFileRepository, QuarantineRepository quarantineRepository, SemaphoreSlim runLock, ILogger logger)
    {
        _runFileRepository = runFileRepository;
        _quarantineRepository = quarantineRepository;
        _runLock = runLock;
        _logger = logger;
    }
```

- [ ] **Step 2: Add the quarantine fallback around the download call**

In `src/SparkVault.Core/RestoreRunner.cs`, `RestoreAsync`'s file loop currently has:
```csharp
                var tempDestination = localDestination + ".sparkvault-tmp";
                try
                {
                    await target.DownloadAsync(file.RelativePath, tempDestination, ct);
                    File.Move(tempDestination, localDestination, overwrite: true);
                }
                catch
                {
                    if (File.Exists(tempDestination))
                        File.Delete(tempDestination);
                    throw;
                }
```
Change it to:
```csharp
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
```

- [ ] **Step 3: `MainWindow.xaml.cs` call site**

In `src/SparkVault.App/MainWindow.xaml.cs`, change:
```csharp
        var restoreRunner = new RestoreRunner(App.RunFileRepository, App.Runner.RunLock, Serilog.Log.Logger);
```
to:
```csharp
        var restoreRunner = new RestoreRunner(App.RunFileRepository, App.QuarantineRepository, App.Runner.RunLock, Serilog.Log.Logger);
```

- [ ] **Step 4: Update every `new RestoreRunner(...)` test call site**

Every occurrence below currently reads `new RestoreRunner(runFileRepo, new SemaphoreSlim(1, 1), Log.Logger)`.
Change to `new RestoreRunner(runFileRepo, new QuarantineRepository(connectionString), new SemaphoreSlim(1, 1), Log.Logger)`
(the enclosing test already has `connectionString` in scope):

- `tests/SparkVault.Core.Tests/RestoreRunnerTests.cs` — lines 47, 95, 168, 218 (4 occurrences)

- [ ] **Step 5: `RestoreRunnerTests` — quarantine fallback test**

In `tests/SparkVault.Core.Tests/RestoreRunnerTests.cs`, add a new test after
`RestoreAsync_AfterJobRename_RestoresToOriginalRelativePaths`:
```csharp
    [Fact]
    public async Task RestoreAsync_FileQuarantinedByLaterRun_StillRestoresFromQuarantine()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-restore-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-restore-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "will be deleted later");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;
            var targetConfig = job.Targets[0];

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var backupRunner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            // Run 1: file A gets backed up.
            var firstResults = await backupRunner.RunAsync(job, progress: null, CancellationToken.None);
            var firstRunId = firstResults[0].Id;

            // Delete from source, run again: run 2 quarantines A on the target.
            File.Delete(filePath);
            await backupRunner.RunAsync(job, progress: null, CancellationToken.None);

            // Restoring run 1 (whose catalog still says "a.txt" at its original path) must fall
            // back to wherever run 2's quarantine moved it.
            var restoreRunner = new RestoreRunner(runFileRepo, quarantineRepo, new SemaphoreSlim(1, 1), Log.Logger);
            await restoreRunner.RestoreAsync(job, targetConfig, firstRunId, progress: null, CancellationToken.None);

            Assert.Equal("will be deleted later", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
```

- [ ] **Step 6: Build and test**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/SparkVault.Core/RestoreRunner.cs src/SparkVault.App/MainWindow.xaml.cs tests/SparkVault.Core.Tests/RestoreRunnerTests.cs
git commit -m "Core: RestoreRunner falls back to the quarantine location for moved files"
```

---

### Task 8: `ScheduleType.OnChange`, `OnChangeJobChecker`, App startup wiring

**Files:**
- Modify: `src/SparkVault.Core/Models.cs`
- Modify: `src/SparkVault.Core/ScheduleCalculator.cs`
- Create: `src/SparkVault.Core/OnChangeJobChecker.cs`
- Modify: `src/SparkVault.App/App.xaml.cs`
- Modify: `tests/SparkVault.Core.Tests/ScheduleCalculatorTests.cs`
- Create: `tests/SparkVault.Core.Tests/OnChangeJobCheckerTests.cs`

**Interfaces:**
- Consumes: `JobFileScanner.Scan` (Task 3), `IncrementalPlanner.HasChanges` (Task 3), `RunRepository.GetLatestSuccessfulRun` (Task 4), `BackupRunner` (Task 6).
- Produces: `ScheduleType.OnChange`, `OnChangeJobChecker.RunDueJobsAsync(...)`.

- [ ] **Step 1: Add `ScheduleType.OnChange`**

In `src/SparkVault.Core/Models.cs`, change:
```csharp
public enum ScheduleType { None, Interval, DailyAt, Weekdays, Weekly, Monthly }
```
to:
```csharp
public enum ScheduleType { None, Interval, DailyAt, Weekdays, Weekly, Monthly, OnChange }
```

- [ ] **Step 2: `ScheduleCalculator.IsDue` — `OnChange` is never due via the periodic path**

In `src/SparkVault.Core/ScheduleCalculator.cs`, add a case right before `case ScheduleType.None:`:
```csharp
                case ScheduleType.OnChange:
                    return false;

```

- [ ] **Step 3: Create `OnChangeJobChecker`**

Create `src/SparkVault.Core/OnChangeJobChecker.cs`:
```csharp
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
            var previousManifest = lastSuccessful is null
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
```

- [ ] **Step 4: Wire it into `App.xaml.cs` startup**

In `src/SparkVault.App/App.xaml.cs`, at the very end of `OnStartup` — after the
`Runner.RunStarted += ...` / `Runner.RunCompleted += ...` lines and after the welcome-window
`if` block, i.e. as the last statement in the method — add:
```csharp
        _ = Task.Run(() => OnChangeJobChecker.RunDueJobsAsync(
            JobRepository, RunRepository, RunFileRepository, Runner, Log.Logger, CancellationToken.None));
```
This must come after the tray icon and `Runner.RunStarted`/`RunCompleted` event subscriptions so
any run this check triggers correctly updates the tray status.

- [ ] **Step 5: `ScheduleCalculatorTests` — `OnChange` never due**

In `tests/SparkVault.Core.Tests/ScheduleCalculatorTests.cs`, add after `None_IsNeverDue`:
```csharp
    [Fact]
    public void OnChange_NeverDueViaIsDue()
    {
        var job = new BackupJob { ScheduleType = ScheduleType.OnChange };
        Assert.False(ScheduleCalculator.IsDue(job, null, DateTime.UtcNow));
        Assert.False(ScheduleCalculator.IsDue(job, DateTime.UtcNow.AddYears(-1), DateTime.UtcNow));
    }
```

- [ ] **Step 6: Create `OnChangeJobCheckerTests`**

Create `tests/SparkVault.Core.Tests/OnChangeJobCheckerTests.cs`:
```csharp
using Serilog;
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class OnChangeJobCheckerTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public async Task RunDueJobsAsync_NeverRunBefore_RunsIt()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            jobRepo.Add(new BackupJob
            {
                Name = "OnChangeJob",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.OnChange,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await OnChangeJobChecker.RunDueJobsAsync(jobRepo, runRepo, runFileRepo, runner, Log.Logger, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(destDir.FullName, "OnChangeJob", "a.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunDueJobsAsync_NothingChangedSinceLastRun_DoesNotRun()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "OnChangeJob",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.OnChange,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            // Pre-existing successful run with an up-to-date catalog, as if a manual run already happened.
            await runner.RunAsync(job, progress: null, CancellationToken.None);
            var runCountBefore = runRepo.GetByJobId(jobId).Count;

            await OnChangeJobChecker.RunDueJobsAsync(jobRepo, runRepo, runFileRepo, runner, Log.Logger, CancellationToken.None);

            Assert.Equal(runCountBefore, runRepo.GetByJobId(jobId).Count);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunDueJobsAsync_FileChangedSinceLastRun_RunsAgain()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "OnChangeJob",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.OnChange,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await runner.RunAsync(job, progress: null, CancellationToken.None);
            var runCountBefore = runRepo.GetByJobId(jobId).Count;

            await Task.Delay(50);
            File.WriteAllText(filePath, "changed content, much longer than before");

            await OnChangeJobChecker.RunDueJobsAsync(jobRepo, runRepo, runFileRepo, runner, Log.Logger, CancellationToken.None);

            Assert.Equal(runCountBefore + 1, runRepo.GetByJobId(jobId).Count);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunDueJobsAsync_JobWithoutTargets_NeverRuns()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "NoTargets",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.OnChange,
                Targets = new List<BackupTarget>(),
            });

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await OnChangeJobChecker.RunDueJobsAsync(jobRepo, runRepo, runFileRepo, runner, Log.Logger, CancellationToken.None);

            Assert.Empty(runRepo.GetByJobId(jobId));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
```

- [ ] **Step 7: Build and test**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds, all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/SparkVault.Core/Models.cs src/SparkVault.Core/ScheduleCalculator.cs src/SparkVault.Core/OnChangeJobChecker.cs src/SparkVault.App/App.xaml.cs tests/SparkVault.Core.Tests/ScheduleCalculatorTests.cs tests/SparkVault.Core.Tests/OnChangeJobCheckerTests.cs
git commit -m "Core: add OnChange schedule type, checked once at app startup"
```

---

### Task 9: UI — schedule dropdown entry, "verify target" checkbox

**Files:**
- Modify: `src/SparkVault.App/MainWindow.xaml`
- Modify: `src/SparkVault.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `ScheduleType.OnChange` (Task 8), `BackupJob.VerifyTargetBeforeRun` (Task 4).

- [ ] **Step 1: Add the combo item and checkbox to the XAML**

In `src/SparkVault.App/MainWindow.xaml`, inside `SettingsScheduleTypeCombo` (currently ending with
`<ComboBoxItem Content="Monatlich"/>`), add a 7th item right after it:
```xml
                        <ComboBoxItem Content="Bei veränderten Daten"/>
```
Immediately after the `</ComboBox>` that closes `SettingsScheduleTypeCombo` — i.e. right before
the `<TextBlock x:Name="SettingsIntervalLabel" ...` line — add:
```xml
                    <CheckBox x:Name="SettingsVerifyTargetCheckBox" Content="Vor jedem Lauf Ziel-Inhalt prüfen" Margin="0,0,0,14"/>
```

- [ ] **Step 2: `LoadSettings()` — index 6 and the checkbox**

In `src/SparkVault.App/MainWindow.xaml.cs`, `LoadSettings()`: change the `SelectedIndex` switch
```csharp
            SettingsScheduleTypeCombo.SelectedIndex = job.ScheduleType switch
            {
                ScheduleType.Interval => 1,
                ScheduleType.DailyAt => 2,
                ScheduleType.Weekdays => 3,
                ScheduleType.Weekly => 4,
                ScheduleType.Monthly => 5,
                _ => 0,
            };
```
to:
```csharp
            SettingsScheduleTypeCombo.SelectedIndex = job.ScheduleType switch
            {
                ScheduleType.Interval => 1,
                ScheduleType.DailyAt => 2,
                ScheduleType.Weekdays => 3,
                ScheduleType.Weekly => 4,
                ScheduleType.Monthly => 5,
                ScheduleType.OnChange => 6,
                _ => 0,
            };
```
Add, right after the existing `SettingsMonthlyDayBox.Text = job.MonthlyDay?.ToString() ?? "";`
line (job branch):
```csharp
            SettingsVerifyTargetCheckBox.IsChecked = job.VerifyTargetBeforeRun;
```
And after the draft-mode branch's `SettingsMonthlyDayBox.Text = "";`:
```csharp
            SettingsVerifyTargetCheckBox.IsChecked = false;
```

- [ ] **Step 3: `SettingsSave_Click()` — index 6 and the checkbox**

In `src/SparkVault.App/MainWindow.xaml.cs`, `SettingsSave_Click()`: change the schedule-type switch
```csharp
        var scheduleType = SettingsScheduleTypeCombo.SelectedIndex switch
        {
            1 => ScheduleType.Interval,
            2 => ScheduleType.DailyAt,
            3 => ScheduleType.Weekdays,
            4 => ScheduleType.Weekly,
            5 => ScheduleType.Monthly,
            _ => ScheduleType.None,
        };
```
to:
```csharp
        var scheduleType = SettingsScheduleTypeCombo.SelectedIndex switch
        {
            1 => ScheduleType.Interval,
            2 => ScheduleType.DailyAt,
            3 => ScheduleType.Weekdays,
            4 => ScheduleType.Weekly,
            5 => ScheduleType.Monthly,
            6 => ScheduleType.OnChange,
            _ => ScheduleType.None,
        };
```
In the constructed `BackupJob` object literal, add a line next to `MonthlyDay = monthlyDay,`:
```csharp
            VerifyTargetBeforeRun = SettingsVerifyTargetCheckBox.IsChecked == true,
```

- [ ] **Step 4: `DescribeNextRun` — `OnChange` case**

In `src/SparkVault.App/MainWindow.xaml.cs`, `DescribeNextRun`, add a case right before `default:`:
```csharp
            case ScheduleType.OnChange:
                return "Beim nächsten Programmstart, falls Änderungen";
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: build succeeds. (No new automated UI test — matches this project's existing convention
of verifying schedule-UI wiring via a live manual check rather than UI Automation unit tests.)

- [ ] **Step 6: Commit**

```bash
git add src/SparkVault.App/MainWindow.xaml src/SparkVault.App/MainWindow.xaml.cs
git commit -m "App: add 'Bei veränderten Daten' schedule option and verify-target checkbox"
```

---

## After all tasks: live verification

Not part of any single task — do this once every task above is committed and `dotnet test` is
fully green:

1. Launch the app, create a job with a Local target, run it once, confirm the file lands as usual.
2. Run the same job again without changing anything — confirm (via the log file) that it reports
   "0 neu/geändert" and completes near-instantly.
3. Modify one file in the source, run again — confirm only that one file gets re-transferred.
4. Delete a file from the source, run again — confirm it disappears from the target's normal path
   and reappears under `_deleted\<timestamp>\...`.
5. Restore an *older* run whose catalog references the now-quarantined file — confirm it comes
   back correctly.
6. Create a job with schedule "Bei veränderten Daten", restart the app with no source changes —
   confirm no run fires; change a file, restart again — confirm a run fires automatically.
7. Toggle "Vor jedem Lauf Ziel-Inhalt prüfen" on a job, manually delete a file directly from the
   target (bypassing the app) that the app still believes is unchanged, run the job — confirm it
   gets re-uploaded instead of silently skipped.
