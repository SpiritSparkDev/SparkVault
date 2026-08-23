# Restore Feature Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user pick a past successful backup run for a job's target and restore those files back to the job's original source path, overwriting what's there.

**Architecture:** A new `RunFiles` table records, per successful `BackupRun`, exactly which files were uploaded (the full job-folder-prefixed remote path, matching what `UploadAsync` was called with). `IBackupTarget` gains a `DownloadAsync(remotePath, localDestinationPath, ct)` method, implemented identically in spirit to each target's existing `UploadAsync`/`DeleteAsync`. A new `RestoreRunner` class (sibling to `BackupRunner`, not an extension of it — opposite direction, single-target, manifest-driven instead of filesystem-scan-driven) orchestrates downloading a chosen run's files back to `BackupJob.SourcePath`. UI: a new "Wiederherstellung" sidebar tab in `JobDashboardWindow`.

**Tech Stack:** .NET 8, existing FluentFTP 54.2.0 / SSH.NET 2026.0.0 / AWSSDK.S3 4.0.102.3 dependencies (no new packages). Every download API call in this plan (FluentFTP `DownloadStream`, SSH.NET `DownloadFile`, AWSSDK.S3 `GetObjectAsync`) was verified by the plan's author against the real installed package versions, including a live upload-then-download roundtrip against the running Docker test containers (FTP, SFTP, MinIO) with byte-for-byte content verification.

**Spec:** [docs/superpowers/specs/2026-08-23-restore-feature-design.md](../specs/2026-08-23-restore-feature-design.md)

## Global Constraints

- .NET 8, C#. No new NuGet packages — `DownloadAsync` uses the same libraries each target already depends on.
- Strict 0-warnings build convention.
- `RunFiles.RelativePath` stores the exact string passed to `UploadAsync` — i.e. **including** the job-folder prefix (`{JobFolder}\...`, from `BackupRunner.SanitizeForPath`). Restore strips that prefix back off before writing to `BackupJob.SourcePath`.
- The manifest is written **only** for `RunStatus.Success` runs, as one batch insert after the upload loop completes — never per-file, never for Failed/Cancelled runs.
- Restore always writes to the job's original `SourcePath`, overwriting existing files. No "restore elsewhere" option in this plan (explicit v1 scope decision — YAGNI).
- Restore does not create `BackupRun`/`RunFiles` rows of its own — it is not a backup run and must not appear in "Letztes Backup erfolgreich" status or the Verlauf table.
- `BackupRunner.SanitizeForPath` becomes `internal static` (from `private static`) so `RestoreRunner` can reuse the identical job-folder sanitization — never reimplement it a second time.
- No database migration framework (existing, accepted limitation) — schema changes mean deleting the dev DB (`%AppData%\SparkVault\sparkvault.db`) before first run after this plan.
- Remote paths: forward-slash string concatenation only, matching every existing target implementation — never `Path.*` for remote-side paths. Local destination paths (the restore's output side) do use `Path.*`, matching `LocalTarget`'s existing convention.

---

## File Structure

```
src/SparkVault.Core/
  Models.cs                         (modify: RunFileRecord)
  SparkVaultDatabase.cs              (modify: RunFiles table)
  RunFileRepository.cs               (new)
  IBackupTarget.cs                   (modify: DownloadAsync)
  LocalTarget.cs                     (modify: DownloadAsync)
  FtpTarget.cs                       (modify: DownloadAsync)
  SftpTarget.cs                      (modify: DownloadAsync)
  S3Target.cs                        (modify: DownloadAsync)
  BackupRunner.cs                    (modify: write RunFiles manifest on success, SanitizeForPath -> internal)
  RestoreRunner.cs                   (new)

src/SparkVault.App/
  JobDashboardWindow.xaml / .xaml.cs (modify: Wiederherstellung tab)

tests/SparkVault.Core.Tests/
  RunFileRepositoryTests.cs          (new)
  LocalTargetTests.cs                (modify: DownloadAsync test)
  FtpTargetTests.cs                  (modify: DownloadAsync test)
  SftpTargetTests.cs                 (modify: DownloadAsync test)
  S3TargetTests.cs                   (modify: DownloadAsync test)
  BackupRunnerTests.cs               (modify: manifest-written-on-success / not-written-on-failure tests)
  RestoreRunnerTests.cs              (new)
```

---

### Task 1: Schema + domain model — `RunFiles` table, `RunFileRecord`

**Files:**
- Modify: `src/SparkVault.Core/Models.cs`
- Modify: `src/SparkVault.Core/SparkVaultDatabase.cs`

**Interfaces:**
- Produces: `public sealed record RunFileRecord(string RelativePath, long Size);` and the `RunFiles` table, both consumed by Task 2 (`RunFileRepository`).

No dedicated test — pure schema/data-shape addition, exercised indirectly by Task 2's repository test (same convention as the S3 plan's Task 2).

- [ ] **Step 1: Add `RunFileRecord` to `src/SparkVault.Core/Models.cs`**

Add after the existing `BackupFile` record at the end of the file:
```csharp
public sealed record RunFileRecord(string RelativePath, long Size);
```

- [ ] **Step 2: Add the `RunFiles` table to `src/SparkVault.Core/SparkVaultDatabase.cs`**

Add to the same multi-statement `CommandText` block, after the existing `Runs` table's closing `);` and before the closing `"""`:
```sql

            CREATE TABLE IF NOT EXISTS RunFiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                RelativePath TEXT NOT NULL,
                Size INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_RunFiles_RunId ON RunFiles(RunId);
```
(Read the current file first — the exact surrounding whitespace/`Jobs`/`Targets`/`Runs` table bodies must stay byte-for-byte unchanged, only `RunFiles` is new.)

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/SparkVault.Core/Models.cs src/SparkVault.Core/SparkVaultDatabase.cs
git commit -m "Add RunFiles table and RunFileRecord for restore manifests"
```

---

### Task 2: `RunFileRepository`

**Files:**
- Create: `src/SparkVault.Core/RunFileRepository.cs`
- Create: `tests/SparkVault.Core.Tests/RunFileRepositoryTests.cs`

**Interfaces:**
- Consumes: `RunFileRecord` (Task 1), `RunFiles` table (Task 1).
- Produces: `RunFileRepository.AddRange(int runId, IEnumerable<RunFileRecord> files)` and `RunFileRepository.GetByRunId(int runId) -> List<RunFileRecord>`, consumed by Task 7 (`BackupRunner`) and Task 8 (`RestoreRunner`).

- [ ] **Step 1: Write the failing test in `tests/SparkVault.Core.Tests/RunFileRepositoryTests.cs`**

```csharp
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

            var files = new List<RunFileRecord>
            {
                new("Test\\a.txt", 100),
                new("Test\\sub\\b.txt", 250),
            };
            repo.AddRange(runId: 42, files);

            var loaded = repo.GetByRunId(42);

            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, f => f.RelativePath == "Test\\a.txt" && f.Size == 100);
            Assert.Contains(loaded, f => f.RelativePath == "Test\\sub\\b.txt" && f.Size == 250);
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~RunFileRepositoryTests"`
Expected: FAIL to compile — `RunFileRepository` doesn't exist yet.

- [ ] **Step 3: Write `src/SparkVault.Core/RunFileRepository.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class RunFileRepository
{
    private readonly string _connectionString;

    public RunFileRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
    }

    public void AddRange(int runId, IEnumerable<RunFileRecord> files)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO RunFiles (RunId, RelativePath, Size)
            VALUES ($runId, $relativePath, $size);
            """;
        var runIdParam = command.Parameters.Add("$runId", SqliteType.Integer);
        var relativePathParam = command.Parameters.Add("$relativePath", SqliteType.Text);
        var sizeParam = command.Parameters.Add("$size", SqliteType.Integer);

        foreach (var file in files)
        {
            runIdParam.Value = runId;
            relativePathParam.Value = file.RelativePath;
            sizeParam.Value = file.Size;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public List<RunFileRecord> GetByRunId(int runId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelativePath, Size FROM RunFiles WHERE RunId = $runId;";
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        var results = new List<RunFileRecord>();
        while (reader.Read())
            results.Add(new RunFileRecord(
                reader.GetString(reader.GetOrdinal("RelativePath")),
                reader.GetInt64(reader.GetOrdinal("Size"))));

        return results;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~RunFileRepositoryTests"`
Expected: PASS (2/2).

- [ ] **Step 5: Run the full test suite to confirm no regression**

Run: `dotnet test`
Expected: all previously-passing tests (71 as of this plan's start) plus these 2 new ones.

- [ ] **Step 6: Commit**

```bash
git add src/SparkVault.Core/RunFileRepository.cs tests/SparkVault.Core.Tests/RunFileRepositoryTests.cs
git commit -m "Add RunFileRepository"
```

---

### Task 3: `IBackupTarget.DownloadAsync` + `LocalTarget` implementation

**Files:**
- Modify: `src/SparkVault.Core/IBackupTarget.cs`
- Modify: `src/SparkVault.Core/LocalTarget.cs`
- Modify: `tests/SparkVault.Core.Tests/LocalTargetTests.cs`

**Interfaces:**
- Produces: `IBackupTarget.DownloadAsync(string remotePath, string localDestinationPath, CancellationToken ct)`. Every existing and future `IBackupTarget` implementation must satisfy it — this task only implements it for `LocalTarget`; Tasks 4-6 implement it for Ftp/Sftp/S3, so the solution will not build between this task and Task 4 landing (a single-task interim break is normal for an interface-adding task; the plan's remaining tasks land immediately after in sequence).

- [ ] **Step 1: Add the failing test to `tests/SparkVault.Core.Tests/LocalTargetTests.cs`**

Read the existing file first for its exact `NewTestConfig`/setup helpers and style, then add:
```csharp
    [Fact]
    public async Task DownloadAsync_UploadedFile_WritesIdenticalContentToDestination()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-local-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-local-dest-");
        var restoreDir = Directory.CreateTempSubdirectory("sparkvault-local-restore-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello local download");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            await using var target = new LocalTarget(destDir.FullName);
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var restoredPath = Path.Combine(restoreDir.FullName, "restored-a.txt");
            await target.DownloadAsync("a.txt", restoredPath, CancellationToken.None);

            Assert.Equal("hello local download", await File.ReadAllTextAsync(restoredPath));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            restoreDir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails to compile**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~LocalTargetTests.DownloadAsync_UploadedFile"`
Expected: FAIL to compile — `LocalTarget` has no `DownloadAsync` yet, and `IBackupTarget` doesn't declare it.

- [ ] **Step 3: Add `DownloadAsync` to `src/SparkVault.Core/IBackupTarget.cs`**

Add to the interface, after `UploadAsync`:
```csharp
    Task DownloadAsync(string remotePath, string localDestinationPath, CancellationToken ct);
```

- [ ] **Step 4: Implement it in `src/SparkVault.Core/LocalTarget.cs`**

Add after `UploadAsync`:
```csharp
    public Task DownloadAsync(string remotePath, string localDestinationPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sourcePath = Path.Combine(_destinationRoot, remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localDestinationPath)!);
        File.Copy(sourcePath, localDestinationPath, overwrite: true);
        return Task.CompletedTask;
    }
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: fails — `FtpTarget`, `SftpTarget`, `S3Target` don't implement `DownloadAsync` yet (interface is not satisfied). This is expected; Tasks 4-6 fix each in turn. Confirm the *only* errors are "does not implement interface member 'DownloadAsync'" for those three classes — no other unexpected errors.

- [ ] **Step 6: Commit anyway — the plan proceeds task-by-task and the next three tasks land immediately**

```bash
git add src/SparkVault.Core/IBackupTarget.cs src/SparkVault.Core/LocalTarget.cs tests/SparkVault.Core.Tests/LocalTargetTests.cs
git commit -m "IBackupTarget: add DownloadAsync, implement for LocalTarget"
```

---

### Task 4: `FtpTarget.DownloadAsync`

**Files:**
- Modify: `src/SparkVault.Core/FtpTarget.cs`
- Modify: `tests/SparkVault.Core.Tests/FtpTargetTests.cs`

**Interfaces:**
- Consumes: `IBackupTarget.DownloadAsync` (Task 3).
- Produces: `FtpTarget` now fully implements `IBackupTarget` again.

- [ ] **Step 1: Add the failing test to `tests/SparkVault.Core.Tests/FtpTargetTests.cs`**

Read the existing file first for its exact `NewTestConfig`/`Host`/`Port` constants and style, then add:
```csharp
    [Fact]
    public async Task DownloadAsync_UploadedFile_WritesIdenticalContentToDestination()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        var restoreDir = Directory.CreateTempSubdirectory("sparkvault-ftp-restore-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello ftp download");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            await using var target = new FtpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var restoredPath = Path.Combine(restoreDir.FullName, "restored-a.txt");
            await target.DownloadAsync("a.txt", restoredPath, CancellationToken.None);

            Assert.Equal("hello ftp download", await File.ReadAllTextAsync(restoredPath));

            await target.DeleteAsync("a.txt", CancellationToken.None);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            restoreDir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails to compile**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~FtpTargetTests.DownloadAsync_UploadedFile"`
Expected: FAIL to compile — `FtpTarget` doesn't implement `DownloadAsync` yet.

- [ ] **Step 3: Implement `DownloadAsync` in `src/SparkVault.Core/FtpTarget.cs`**

Verified against real FluentFTP 54.2.0 (`AsyncFtpClient.DownloadStream(Stream outStream, string remotePath, ..., CancellationToken token = default)` returns `Task<bool>`). Add after `UploadAsync`:
```csharp
    public async Task DownloadAsync(string remotePath, string localDestinationPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureConnectedAsync(ct);

        var full = RemotePath(remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localDestinationPath)!);

        await using var dest = File.Create(localDestinationPath);
        var ok = await _client.DownloadStream(dest, full, token: ct);
        if (!ok)
            throw new IOException($"FTP-Download fehlgeschlagen für {remotePath}.");
    }
```
Add `using System.IO;` at the top of the file if not already present (check first — the file already uses `File`/`IOException` elsewhere, so it's likely already there via implicit usings; only add if the build in Step 5 complains).

- [ ] **Step 4: Ensure the FTP Docker container is running**

Run: `docker compose -f docker/docker-compose.test.yml up -d` (no-op if already running).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~FtpTargetTests.DownloadAsync_UploadedFile"`
Expected: PASS, with a real (non-instant) duration confirming it hit the actual container.

- [ ] **Step 6: Commit**

```bash
git add src/SparkVault.Core/FtpTarget.cs tests/SparkVault.Core.Tests/FtpTargetTests.cs
git commit -m "FtpTarget: implement DownloadAsync"
```

---

### Task 5: `SftpTarget.DownloadAsync`

**Files:**
- Modify: `src/SparkVault.Core/SftpTarget.cs`
- Modify: `tests/SparkVault.Core.Tests/SftpTargetTests.cs`

**Interfaces:**
- Consumes: `IBackupTarget.DownloadAsync` (Task 3).
- Produces: `SftpTarget` now fully implements `IBackupTarget` again.

- [ ] **Step 1: Add the failing test to `tests/SparkVault.Core.Tests/SftpTargetTests.cs`**

Read the existing file first for its exact `NewTestConfig`/`Host`/`Port` constants and style, then add (mirroring Task 4's FTP test):
```csharp
    [Fact]
    public async Task DownloadAsync_UploadedFile_WritesIdenticalContentToDestination()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        var restoreDir = Directory.CreateTempSubdirectory("sparkvault-sftp-restore-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello sftp download");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            await using var target = new SftpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var restoredPath = Path.Combine(restoreDir.FullName, "restored-a.txt");
            await target.DownloadAsync("a.txt", restoredPath, CancellationToken.None);

            Assert.Equal("hello sftp download", await File.ReadAllTextAsync(restoredPath));

            await target.DeleteAsync("a.txt", CancellationToken.None);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            restoreDir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails to compile**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~SftpTargetTests.DownloadAsync_UploadedFile"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `DownloadAsync` in `src/SparkVault.Core/SftpTarget.cs`**

Verified against real SSH.NET 2026.0.0 (`SftpClient.DownloadFile(string path, Stream output)` is synchronous — no `Async` suffix in this library, matching the existing `UploadFile`/`GetAttributes` calls already in this file, which follow the same `Task.Run`-wrapping pattern). Add after `UploadAsync`:
```csharp
    public async Task DownloadAsync(string remotePath, string localDestinationPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureConnectedAsync(ct);

        var full = RemotePath(remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localDestinationPath)!);

        await Task.Run(() =>
        {
            using var dest = File.Create(localDestinationPath);
            _client.DownloadFile(full, dest);
        }, ct);
    }
```

- [ ] **Step 4: Ensure the SFTP Docker container is running**

Run: `docker compose -f docker/docker-compose.test.yml up -d` (no-op if already running).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~SftpTargetTests.DownloadAsync_UploadedFile"`
Expected: PASS, with a real (non-instant) duration.

- [ ] **Step 6: Commit**

```bash
git add src/SparkVault.Core/SftpTarget.cs tests/SparkVault.Core.Tests/SftpTargetTests.cs
git commit -m "SftpTarget: implement DownloadAsync"
```

---

### Task 6: `S3Target.DownloadAsync`

**Files:**
- Modify: `src/SparkVault.Core/S3Target.cs`
- Modify: `tests/SparkVault.Core.Tests/S3TargetTests.cs`

**Interfaces:**
- Consumes: `IBackupTarget.DownloadAsync` (Task 3).
- Produces: `S3Target` now fully implements `IBackupTarget` again — every `IBackupTarget` implementation now satisfies the interface, so the solution builds cleanly again after this task.

- [ ] **Step 1: Add the failing test to `tests/SparkVault.Core.Tests/S3TargetTests.cs`**

Read the existing file first for its exact `NewTestConfig`/`CreateBucketAsync`/`Endpoint`/`Port` helpers and style (this file's tests pre-create the bucket directly via the SDK before exercising `S3Target`, since `S3Target` no longer auto-creates buckets — follow that same pattern), then add:
```csharp
    [Fact]
    public async Task DownloadAsync_UploadedFile_WritesIdenticalContentToDestination()
    {
        if (!DockerTestHelper.IsReachable("127.0.0.1", Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-s3-src-");
        var restoreDir = Directory.CreateTempSubdirectory("sparkvault-s3-restore-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello s3 download");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            var config = NewTestConfig();
            await CreateBucketAsync(config.Bucket!);
            await using var target = new S3Target(config);
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var restoredPath = Path.Combine(restoreDir.FullName, "restored-a.txt");
            await target.DownloadAsync("a.txt", restoredPath, CancellationToken.None);

            Assert.Equal("hello s3 download", await File.ReadAllTextAsync(restoredPath));

            await target.DeleteAsync("a.txt", CancellationToken.None);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            restoreDir.Delete(recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails to compile**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~S3TargetTests.DownloadAsync_UploadedFile"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `DownloadAsync` in `src/SparkVault.Core/S3Target.cs`**

Verified against real AWSSDK.S3 4.0.102.3 (`GetObjectAsync(GetObjectRequest)` returns a response exposing `.ResponseStream`). Add after `UploadAsync`:
```csharp
    public async Task DownloadAsync(string remotePath, string localDestinationPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureBucketAsync(ct);

        var key = RemoteKey(remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localDestinationPath)!);

        using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct);
        await using var dest = File.Create(localDestinationPath);
        await response.ResponseStream.CopyToAsync(dest, ct);
    }
```

- [ ] **Step 4: Ensure the MinIO Docker container is running**

Run: `docker compose -f docker/docker-compose.test.yml up -d` (no-op if already running).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~S3TargetTests.DownloadAsync_UploadedFile"`
Expected: PASS, with a real (non-instant) duration.

- [ ] **Step 6: Run the full test suite to confirm the solution builds and no regression occurred**

Run: `dotnet test`
Expected: all tests pass (71 + 2 RunFileRepository + 4 new DownloadAsync tests = 77), 0 build errors — every `IBackupTarget` implementation is now complete.

- [ ] **Step 7: Commit**

```bash
git add src/SparkVault.Core/S3Target.cs tests/SparkVault.Core.Tests/S3TargetTests.cs
git commit -m "S3Target: implement DownloadAsync"
```

---

### Task 7: `BackupRunner` — write the `RunFiles` manifest on success

**Files:**
- Modify: `src/SparkVault.Core/BackupRunner.cs`
- Modify: `tests/SparkVault.Core.Tests/BackupRunnerTests.cs`

**Interfaces:**
- Consumes: `RunFileRepository.AddRange` (Task 2).
- Produces: `BackupRunner.SanitizeForPath` becomes `internal static` (was `private static`), consumed by Task 8 (`RestoreRunner`). `BackupRunner` gains a `RunFileRepository` constructor dependency.

- [ ] **Step 1: Add the failing tests to `tests/SparkVault.Core.Tests/BackupRunnerTests.cs`**

Read the existing file first — `BackupRunner`'s constructor currently takes `(RunRepository, ILogger)`; this task adds a `RunFileRepository` parameter, which means **every** existing `new BackupRunner(runRepo, Log.Logger)` call site in this test file must be updated to `new BackupRunner(runRepo, runFileRepo, Log.Logger)` (or however the final constructor is ordered — see Step 3). Update every one of them as part of this task, not just the two new tests below — the plan author counted at least 9 call sites in the current file; grep for `new BackupRunner(` to find them all and update each.

Add these two new tests:
```csharp
    [Fact]
    public async Task RunAsync_SuccessfulRun_WritesRunFilesManifest()
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
            var runner = new BackupRunner(runRepo, runFileRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(RunStatus.Success, results[0].Status);
            var manifest = runFileRepo.GetByRunId(results[0].Id);
            Assert.Equal(2, manifest.Count);
            Assert.Contains(manifest, f => f.RelativePath == "Test\\a.txt" && f.Size == 5);
            Assert.Contains(manifest, f => f.RelativePath == "Test\\b.txt" && f.Size == 6);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_FailedRun_WritesNoRunFilesManifest()
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
                Name = "Test",
                SourcePath = srcDir.FullName,
                // Unreachable FTP target -> TestConnectionAsync fails -> run is Failed, no files uploaded.
                Targets = new List<BackupTarget> { new() { Type = TargetType.Ftp, Host = "127.0.0.1", Port = 1, Username = "x", RemotePath = "/x" } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(RunStatus.Failed, results[0].Status);
            Assert.Empty(runFileRepo.GetByRunId(results[0].Id));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~RunAsync_SuccessfulRun_WritesRunFilesManifest|FullyQualifiedName~RunAsync_FailedRun_WritesNoRunFilesManifest"`
Expected: FAIL to compile — `BackupRunner`'s constructor doesn't take a `RunFileRepository` yet.

- [ ] **Step 3: Modify `src/SparkVault.Core/BackupRunner.cs`**

Add the field, constructor parameter, and manifest-writing logic. Change:
```csharp
    private readonly RunRepository _runRepository;
    private readonly ILogger _logger;
```
to:
```csharp
    private readonly RunRepository _runRepository;
    private readonly RunFileRepository _runFileRepository;
    private readonly ILogger _logger;
```
Change the constructor:
```csharp
    public BackupRunner(RunRepository runRepository, ILogger logger)
    {
        _runRepository = runRepository;
        _logger = logger;
    }
```
to:
```csharp
    public BackupRunner(RunRepository runRepository, RunFileRepository runFileRepository, ILogger logger)
    {
        _runRepository = runRepository;
        _runFileRepository = runFileRepository;
        _logger = logger;
    }
```

In `RunForTargetAsync`, collect the manifest as files upload and write it after a successful run. Change:
```csharp
        int done = 0;
        long bytesDone = 0;

        try
        {
            run.Id = _runRepository.Add(run);
```
to:
```csharp
        int done = 0;
        long bytesDone = 0;
        var uploaded = new List<RunFileRecord>();

        try
        {
            run.Id = _runRepository.Add(run);
```
Change the per-file loop body's success line (`done++; bytesDone += file.Size;`) to also record the file:
```csharp
                await target.UploadAsync(file, progress, ct);
                done++;
                bytesDone += file.Size;
                uploaded.Add(new RunFileRecord(file.RelativePath, file.Size));
                progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));
```
Change the success line to write the manifest:
```csharp
            run.Status = RunStatus.Success;
            _logger.Information("Job {JobName} -> {Target} completed: {FileCount} files, {TotalBytes} bytes",
                job.Name, targetConfig.Describe(), done, bytesDone);
```
to:
```csharp
            run.Status = RunStatus.Success;
            _logger.Information("Job {JobName} -> {Target} completed: {FileCount} files, {TotalBytes} bytes",
                job.Name, targetConfig.Describe(), done, bytesDone);
```
(unchanged) — then, immediately after the `finally` block's `_runRepository.Update(run);` line (so the manifest is only written once `run.Id` and `run.Status` are both final), add the manifest write. Change:
```csharp
        finally
        {
            run.EndedAt = DateTime.UtcNow;
            run.FileCount = done;
            run.TotalBytes = bytesDone;
            if (run.Id != 0)
                _runRepository.Update(run);
        }

        return run;
```
to:
```csharp
        finally
        {
            run.EndedAt = DateTime.UtcNow;
            run.FileCount = done;
            run.TotalBytes = bytesDone;
            if (run.Id != 0)
                _runRepository.Update(run);
        }

        if (run.Status == RunStatus.Success && run.Id != 0)
            _runFileRepository.AddRange(run.Id, uploaded);

        return run;
```

Finally, make `SanitizeForPath` reusable by `RestoreRunner`:
```csharp
    private static string SanitizeForPath(string name)
```
to:
```csharp
    internal static string SanitizeForPath(string name)
```

- [ ] **Step 4: Fix every other `new BackupRunner(...)` call site**

Grep the whole solution: `grep -rn "new BackupRunner(" src tests`. For each match outside this task's own new tests, add a `RunFileRepository` instance as the second constructor argument (construct one the same way the existing `RunRepository` at that call site is constructed — same connection string). This includes `src/SparkVault.App/App.xaml.cs` (the app's real startup wiring) and every other test in `BackupRunnerTests.cs` that constructs a `BackupRunner` directly.

- [ ] **Step 5: Run the new tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~RunAsync_SuccessfulRun_WritesRunFilesManifest|FullyQualifiedName~RunAsync_FailedRun_WritesNoRunFilesManifest"`
Expected: PASS (2/2).

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass, 0 build errors (every `new BackupRunner(...)` call site now compiles).

- [ ] **Step 7: Commit**

```bash
git add src/SparkVault.Core/BackupRunner.cs src/SparkVault.App/App.xaml.cs tests/SparkVault.Core.Tests/BackupRunnerTests.cs
git commit -m "BackupRunner: write RunFiles manifest on successful runs"
```

---

### Task 8: `RestoreRunner`

**Files:**
- Create: `src/SparkVault.Core/RestoreRunner.cs`
- Create: `tests/SparkVault.Core.Tests/RestoreRunnerTests.cs`

**Interfaces:**
- Consumes: `RunFileRepository.GetByRunId` (Task 2), `IBackupTarget.DownloadAsync` (Tasks 3-6), `TargetFactory.Create` (existing), `BackupRunner.SanitizeForPath` (Task 7, now `internal`).
- Produces: `RestoreRunner.RestoreAsync(BackupJob job, BackupTarget targetConfig, int runId, IProgress<TransferProgress>? progress, CancellationToken ct)`, consumed by Task 9 (UI).

- [ ] **Step 1: Write the failing test in `tests/SparkVault.Core.Tests/RestoreRunnerTests.cs`**

Full roundtrip: back up, then delete/modify the source, then restore, then confirm the source is back exactly as it was.
```csharp
using Serilog;
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class RestoreRunnerTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public async Task RestoreAsync_AfterSourceDeleted_RecreatesOriginalFiles()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-restore-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-restore-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "original content a");
            File.WriteAllText(Path.Combine(srcDir.FullName, "b.txt"), "original content b");

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
            var backupRunner = new BackupRunner(runRepo, runFileRepo, Log.Logger);
            var results = await backupRunner.RunAsync(job, progress: null, CancellationToken.None);
            var runId = results[0].Id;

            // Simulate data loss: delete one file, corrupt the other.
            File.Delete(Path.Combine(srcDir.FullName, "a.txt"));
            File.WriteAllText(Path.Combine(srcDir.FullName, "b.txt"), "corrupted!");

            var restoreRunner = new RestoreRunner(runFileRepo, Log.Logger);
            await restoreRunner.RestoreAsync(job, targetConfig, runId, progress: null, CancellationToken.None);

            Assert.Equal("original content a", await File.ReadAllTextAsync(Path.Combine(srcDir.FullName, "a.txt")));
            Assert.Equal("original content b", await File.ReadAllTextAsync(Path.Combine(srcDir.FullName, "b.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails to compile**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~RestoreRunnerTests"`
Expected: FAIL to compile — `RestoreRunner` doesn't exist yet.

- [ ] **Step 3: Write `src/SparkVault.Core/RestoreRunner.cs`**

```csharp
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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "FullyQualifiedName~RestoreRunnerTests"`
Expected: PASS.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass, 0 build errors.

- [ ] **Step 6: Commit**

```bash
git add src/SparkVault.Core/RestoreRunner.cs tests/SparkVault.Core.Tests/RestoreRunnerTests.cs
git commit -m "Add RestoreRunner"
```

---

### Task 9: UI — "Wiederherstellung" tab in `JobDashboardWindow`

**Files:**
- Modify: `src/SparkVault.App/JobDashboardWindow.xaml`
- Modify: `src/SparkVault.App/JobDashboardWindow.xaml.cs`

**Interfaces:**
- Consumes: `RestoreRunner.RestoreAsync` (Task 8), `RunRepository.GetByJobId` (existing), `TransferProgress` (existing).
- Produces: no new public surface — the dashboard window's constructor/usage from `MainWindow` is unchanged.

Read both files in full first — the exact current sidebar `RadioButton` list, the `Grid`'s panel-visibility pattern, and `Nav_Checked`'s toggle logic must be matched precisely; this is prose-described, not literal find-and-replace, because the plan author has not re-read these files since Task 4 of the earlier UI sub-projects landed and their exact current line numbers may have drifted.

- [ ] **Step 1: Add the sidebar nav item**

In `JobDashboardWindow.xaml`, add a fourth `RadioButton` between the existing "Dateiauswahl" and "Verlauf" entries (matching the mockup's order: Übersicht, Dateiauswahl, Verlauf, Wiederherstellung, Einstellungen — so place it between Verlauf and Einstellungen instead if that's the existing order; check the live file for the current order and slot it in immediately before "Einstellungen"):
```xml
                    <RadioButton x:Name="NavRestore" Content="Wiederherstellung" GroupName="Nav"
                                 Style="{StaticResource SidebarNavStyle}" Checked="Nav_Checked"/>
```

- [ ] **Step 2: Add the "Wiederherstellung" panel**

Add a new top-level child inside the same `Grid` that already hosts `OverviewPanel`/`FilesPanel`/`HistoryGrid`/`SettingsPanel` (each toggles visibility independently — this one follows the identical pattern):
```xml
            <!-- Wiederherstellung -->
            <Grid x:Name="RestorePanel" Visibility="Collapsed">
                <StackPanel x:Name="RestoreEmptyState">
                    <TextBlock Text="Wiederherstellung" Style="{StaticResource HeadingTextStyle}" FontSize="22" Margin="0,0,0,16"/>
                    <TextBlock Text="Noch keine Sicherung zum Wiederherstellen vorhanden." Style="{StaticResource MutedTextStyle}"/>
                </StackPanel>

                <StackPanel x:Name="RestoreContent" Visibility="Collapsed">
                    <DockPanel Margin="0,0,0,16">
                        <TextBlock Text="Wiederherstellung" Style="{StaticResource HeadingTextStyle}" FontSize="22"/>
                        <ComboBox x:Name="RestoreTargetCombo" DockPanel.Dock="Right" Width="200" HorizontalAlignment="Right"
                                  SelectionChanged="RestoreTargetCombo_SelectionChanged" Visibility="Collapsed"/>
                    </DockPanel>
                    <DockPanel LastChildFill="True">
                        <ListBox x:Name="RestoreVersionsListBox" DockPanel.Dock="Left" Width="220" BorderThickness="0" Margin="0,0,20,0"
                                 SelectionChanged="RestoreVersionsListBox_SelectionChanged" DisplayMemberPath="Label"/>
                        <Border Style="{StaticResource CardStyle}" VerticalAlignment="Top">
                            <StackPanel>
                                <TextBlock Text="AUSGEWÄHLTER STAND" Style="{StaticResource MutedTextStyle}"/>
                                <TextBlock x:Name="RestoreSelectedLabelText" Style="{StaticResource HeadingTextStyle}" FontSize="18" Margin="0,4,0,10"/>
                                <TextBlock x:Name="RestoreSelectedMetaText" Style="{StaticResource MutedTextStyle}" Margin="0,0,0,14"/>
                                <TextBlock Text="Dateien werden an ihren ursprünglichen Ort zurückgespielt." Style="{StaticResource MutedTextStyle}" TextWrapping="Wrap" Margin="0,0,0,14"/>
                                <Button x:Name="RestoreButton" Content="Wiederherstellen" Style="{StaticResource PrimaryButtonStyle}"
                                        HorizontalAlignment="Left" Click="RestoreButton_Click"/>
                            </StackPanel>
                        </Border>
                    </DockPanel>
                </StackPanel>

                <StackPanel x:Name="RestoreRunningView" Visibility="Collapsed">
                    <TextBlock Text="Wiederherstellung läuft" Style="{StaticResource HeadingTextStyle}" FontSize="22" Margin="0,0,0,16"/>
                    <Border Style="{StaticResource CardStyle}" Margin="0,0,0,16">
                        <StackPanel>
                            <DockPanel Margin="0,0,0,14">
                                <Button x:Name="RestoreCancelButton" Content="Abbrechen" Style="{StaticResource GhostButtonStyle}"
                                        DockPanel.Dock="Right" Click="RestoreCancelButton_Click"/>
                                <TextBlock x:Name="RestorePercentText" Text="0" Style="{StaticResource HeadingTextStyle}" FontSize="38" Foreground="{StaticResource Accent700Brush}"/>
                                <TextBlock Text="%" Style="{StaticResource HeadingTextStyle}" FontSize="22" Foreground="{StaticResource Accent700Brush}" Margin="4,0,0,3" VerticalAlignment="Bottom"/>
                            </DockPanel>
                            <ProgressBar x:Name="RestoreProgressBar" Height="6" Minimum="0" Maximum="100" Margin="0,0,0,10"/>
                            <TextBlock x:Name="RestoreCurrentFileText" Style="{StaticResource MutedTextStyle}"
                                       FontFamily="Consolas" TextTrimming="CharacterEllipsis"/>
                        </StackPanel>
                    </Border>
                </StackPanel>
            </Grid>
```

- [ ] **Step 3: Update `Nav_Checked` in `JobDashboardWindow.xaml.cs`**

Add `RestorePanel`'s visibility toggle and a `LoadRestore()` call, following the exact pattern already used for `FilesPanel`/`LoadFiles()`:
```csharp
        RestorePanel.Visibility = NavRestore.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
```
and:
```csharp
        if (NavRestore.IsChecked == true) LoadRestore();
```

- [ ] **Step 4: Add the restore view-model and loading logic**

Add near the top of the class, alongside the existing `HistoryRow`/`FolderRow` nested types:
```csharp
    private sealed class VersionRow
    {
        public required int RunId { get; init; }
        public required string Label { get; init; }
        public required int FileCount { get; init; }
        public required long TotalBytes { get; init; }
    }
```

Add the loading method (uses the existing `FormatBytes` helper already in this file):
```csharp
    private void LoadRestore()
    {
        var job = CurrentJob;
        if (job is null) return;

        RestoreTargetCombo.ItemsSource = job.Targets;
        RestoreTargetCombo.DisplayMemberPath = null; // BackupTarget has no Description; use Describe() via a converter-free approach below
        RestoreTargetCombo.ItemTemplate = null;
        RestoreTargetCombo.Items.Clear();
        foreach (var t in job.Targets)
            RestoreTargetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = t.Describe(), Tag = t });
        RestoreTargetCombo.Visibility = job.Targets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (RestoreTargetCombo.Items.Count > 0)
            RestoreTargetCombo.SelectedIndex = 0;

        LoadRestoreVersionsForSelectedTarget();
    }

    private BackupTarget? SelectedRestoreTarget()
    {
        var job = CurrentJob;
        if (job is null || job.Targets.Count == 0) return null;
        if (RestoreTargetCombo.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: BackupTarget t }) return t;
        return job.Targets[0];
    }

    private void LoadRestoreVersionsForSelectedTarget()
    {
        var job = CurrentJob;
        var selectedTarget = SelectedRestoreTarget();
        if (job is null || selectedTarget is null)
        {
            RestoreEmptyState.Visibility = Visibility.Visible;
            RestoreContent.Visibility = Visibility.Collapsed;
            return;
        }

        var versions = App.RunRepository.GetByJobId(job.Id)
            .Where(r => r.TargetId == selectedTarget.Id && r.Status == RunStatus.Success)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new VersionRow
            {
                RunId = r.Id,
                Label = r.StartedAt.ToLocalTime().ToString("g"),
                FileCount = r.FileCount,
                TotalBytes = r.TotalBytes,
            })
            .ToList();

        if (versions.Count == 0)
        {
            RestoreEmptyState.Visibility = Visibility.Visible;
            RestoreContent.Visibility = Visibility.Collapsed;
            return;
        }

        RestoreEmptyState.Visibility = Visibility.Collapsed;
        RestoreContent.Visibility = Visibility.Visible;
        RestoreVersionsListBox.ItemsSource = versions;
        RestoreVersionsListBox.SelectedIndex = 0;
    }

    private void RestoreTargetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadRestoreVersionsForSelectedTarget();
    }

    private void RestoreVersionsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RestoreVersionsListBox.SelectedItem is not VersionRow row) return;
        RestoreSelectedLabelText.Text = row.Label;
        RestoreSelectedMetaText.Text = $"{FormatBytes(row.TotalBytes)} · {row.FileCount} Dateien";
    }
```

- [ ] **Step 5: Add the restore-execution handlers**

Add a `CancellationTokenSource? _restoreCts` field alongside the existing `_runCts`/`_pauseToken` fields, then:
```csharp
    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var job = CurrentJob;
        var target = SelectedRestoreTarget();
        if (job is null || target is null) return;
        if (RestoreVersionsListBox.SelectedItem is not VersionRow row) return;

        var result = System.Windows.MessageBox.Show(this,
            $"Dateien vom Stand \"{row.Label}\" werden nach \"{job.SourcePath}\" zurückgespielt und überschreiben dortige Dateien. Fortfahren?",
            "SparkVault", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        RestoreContent.Visibility = Visibility.Collapsed;
        RestoreRunningView.Visibility = Visibility.Visible;
        RestorePercentText.Text = "0";
        RestoreProgressBar.Value = 0;
        RestoreCurrentFileText.Text = "";

        _restoreCts = new CancellationTokenSource();
        var progress = new Progress<TransferProgress>(p =>
        {
            RestoreProgressBar.Value = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal * 100;
            RestorePercentText.Text = ((int)RestoreProgressBar.Value).ToString();
            RestoreCurrentFileText.Text = p.CurrentFile;
        });

        var runFileRepo = new RunFileRepository(App.ConnectionString);
        var restoreRunner = new RestoreRunner(runFileRepo, Serilog.Log.Logger);

        try
        {
            await restoreRunner.RestoreAsync(job, target, row.RunId, progress, _restoreCts.Token);
            System.Windows.MessageBox.Show(this, "Wiederherstellung abgeschlossen.", "SparkVault",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            // user-initiated cancel, no error dialog
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Wiederherstellung fehlgeschlagen: {ex.Message}", "SparkVault",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            RestoreRunningView.Visibility = Visibility.Collapsed;
            RestoreContent.Visibility = Visibility.Visible;
            _restoreCts.Dispose();
            _restoreCts = null;
        }
    }

    private void RestoreCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _restoreCts?.Cancel();
    }
```

`App.ConnectionString` does not exist yet — check `src/SparkVault.App/App.xaml.cs` for how the connection string is currently held (it's a local variable inside `OnStartup` today, used to construct `JobRepository`/`RunRepository`/`BackupRunner`). Add a `public static string ConnectionString { get; private set; } = null!;` alongside the existing `public static JobRepository JobRepository { get; private set; }` etc., and assign it at the same point the local `connectionString` variable is currently assigned, so `JobDashboardWindow` (and Task 7's `App.xaml.cs` `BackupRunner` construction) can both reach it.

- [ ] **Step 6: Build**

Run: `dotnet build`
Expected: 0 errors, 0 warnings.

- [ ] **Step 7: Manual verification**

Delete the stale dev DB (`%AppData%\SparkVault\sparkvault.db` and log files — schema changed again). Run `dotnet run --project src/SparkVault.App`. Create or reuse a job with a Local target, run "Jetzt sichern" at least twice (so there are 2+ versions), open the job's dashboard, click "Wiederherstellung" — confirm the version list shows both runs, newest first, with correct file-count/size in the detail panel. Delete/modify a source file, select the newer version, click "Wiederherstellen", confirm the Ja/Nein dialog appears, confirm Ja triggers a progress view and the file is correctly restored. Test the empty state too (a brand-new job with no successful runs shows "Noch keine Sicherung...").

- [ ] **Step 8: Commit**

```bash
git add src/SparkVault.App/JobDashboardWindow.xaml src/SparkVault.App/JobDashboardWindow.xaml.cs src/SparkVault.App/App.xaml.cs
git commit -m "App: add Wiederherstellung tab to JobDashboardWindow"
```

---

### Task 10: Full-solution verification

**Files:** none (verification only)

- [ ] **Step 1: Delete the stale dev database**

Schema changed (new `RunFiles` table) and `App.xaml.cs` changed (new `ConnectionString` property) — delete `%AppData%\SparkVault\sparkvault.db` and Serilog log files before the first post-plan run.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass (71 pre-plan + 2 RunFileRepository + 4 DownloadAsync + 2 manifest + 1 RestoreRunner = 80). Confirm via `docker ps` that all three Docker test containers are up and that the Docker-gated tests genuinely exercised them (non-instant timings), not silently skipped.

- [ ] **Step 3: Full manual smoke test**

Run the app for real. Create a job with a Local target and a small real source folder. Run it twice with different file contents between runs (edit a file, add a file) so the two versions are genuinely distinguishable. Open Wiederherstellung, restore the OLDER version, confirm the edited/added file reverts correctly and nothing from the newer version leaks in. Confirm Verlauf and Übersicht are unaffected by the restore (no new rows, no "letztes Backup" change). If a second target exists on the same job, confirm the target dropdown appears and switching targets reloads the version list for that target only.

- [ ] **Step 4: Report findings**

Report exact test counts, confirmation the Docker-gated tests ran for real, and concrete manual-test observations (not "worked as expected" — actual detail: what was restored, what was verified against what).
