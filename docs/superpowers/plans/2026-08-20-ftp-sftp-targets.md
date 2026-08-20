# FTP/SFTP Targets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add FTP (incl. FTPS) and SFTP as backup targets alongside `LocalTarget`, and let a job have multiple targets instead of exactly one, with per-target run logging.

**Architecture:** `SparkVault.Core` gains `FtpTarget` (FluentFTP) and `SftpTarget` (SSH.NET), both implementing the now-`IAsyncDisposable` `IBackupTarget`. `BackupJob` gets a `List<BackupTarget> Targets` (replacing the single `DestinationPath`), each target typed `Local`/`Ftp`/`Sftp` with its own connection config. Credentials (FTP/SFTP passwords, SFTP key passphrase) are DPAPI-encrypted via a new `CredentialProtector` before they ever reach SQLite. `BackupRunner` scans the source once per run and iterates `job.Targets` sequentially, creating one `BackupRun` row per target (grouped by a shared `RunGroupId`) so a partial failure across targets is visible per-target in the log.

**Tech Stack:** .NET 8, `FluentFTP` 54.2.0, `SSH.NET` 2026.0.0, `System.Security.Cryptography.ProtectedData` 8.0.0 (DPAPI). All three package versions and every FluentFTP/SSH.NET API call used in this plan were verified by the plan's author against the real installed packages, including live round-trips against real `garethflowers/ftp-server` and `atmoz/sftp` Docker containers — the code in this plan is not guessed.

**Spec:** [docs/superpowers/specs/2026-08-20-ftp-sftp-targets-design.md](../specs/2026-08-20-ftp-sftp-targets-design.md)

**Prior work:** [docs/superpowers/specs/2026-08-19-sparkvault-mvp-design.md](../specs/2026-08-19-sparkvault-mvp-design.md) and [docs/superpowers/plans/2026-08-19-sparkvault-mvp.md](2026-08-19-sparkvault-mvp.md) — the MVP this plan extends, already merged into this branch's history (`feature/mvp` → `feature/ftp-sftp-targets`). Read the actual current state of the files this plan touches before editing — some MVP-era code changed during that plan's own final review (e.g. `BackupRunner` already has a `SemaphoreSlim` and `TestConnectionAsync` call, `RunRepository` already parses dates with `DateTimeStyles.RoundtripKind`, `App.xaml.cs` already has `DispatcherUnhandledException` handling and autostart registration) — this plan's tasks are written against that actual current state, not the original MVP plan text.

## Global Constraints

- .NET 8, C#. `SparkVault.Core` targets plain `net8.0` (not `-windows`) but is Windows-only in practice (DPAPI, this app never runs elsewhere) — add `<SupportedOSPlatform>windows</SupportedOSPlatform>` to its csproj AND an explicit `[assembly: SupportedOSPlatform("windows")]` in a new `AssemblyAttributes.cs` file (the csproj property alone does not generate the attribute in this SDK — verified; without the explicit attribute, DPAPI calls produce CA1416 warnings that break the "0 warnings" build requirement).
- Credentials (FTP/SFTP passwords, SFTP key passphrase) are never stored in plaintext in SQLite and never logged in plaintext (spec §5, §9). Only `CredentialProtector.Protect`/`Unprotect` touch plaintext credential values, and only at the point of saving to/loading from the DB or connecting to a server.
- `IBackupTarget`'s existing four methods keep their exact signatures; the only interface change is adding `IAsyncDisposable` as a base interface (a deliberate, documented extension — connected targets need cleanup that `LocalTarget` never needed). `LocalTarget` gets a trivial `ValueTask DisposeAsync() => ValueTask.CompletedTask;`.
- Temp-name-then-verify-then-commit applies to FTP/SFTP uploads too (spec §6): upload under a `.sparkvault-tmp`-suffixed remote name, verify by remote file size, then rename to the final name; on any failure/cancellation, best-effort delete the temp remote file before rethrowing.
- One `BackupRun` row per target per run attempt, sharing a `RunGroupId` (Guid) (spec §2, §4). `RunStarted` fires once per job-level run (not per target); `RunCompleted` fires once at the end with an aggregated status (`Success` only if every target succeeded).
- Remote paths use forward slashes always; `BackupFile.RelativePath` (Windows-style, backslash-separated) must be normalized to `/` before use in any FTP/SFTP remote path — this is the same class of bug the MVP hit with `FileSystemName.MatchesSimpleExpression` (backslash-as-escape); do not reuse `Path.GetRelativePath`/`Path.Combine` for remote paths, only plain string concatenation with `/`.
- No database migration framework — `SparkVaultDatabase.EnsureCreated` still uses `CREATE TABLE IF NOT EXISTS`. The schema changes in this plan are NOT backward-compatible with an existing dev-machine `%AppData%\SparkVault\sparkvault.db` from the MVP (the `Jobs` table drops `DestinationPath`, `Runs` gains required columns). Task 19's manual verification step must delete that file first — this is a one-time, pre-release, single-developer-machine concern, not something to build migration logic for.
- Docker integration tests (`FtpTargetTests`, `SftpTargetTests`) must not fail the build when Docker isn't running — each test does a short TCP-reachability pre-check and returns early (no assertions run, reports as a trivial pass) if the test server isn't reachable, rather than requiring `Xunit.SkippableFact` or failing hard. This is a known, accepted MVP-scope limitation (documented, not hidden).
- App title stays **SparkVault**.

---

## File Structure

```
src/SparkVault.Core/
  SparkVault.Core.csproj      (modify: add FluentFTP, SSH.NET, ProtectedData packages + SupportedOSPlatform)
  AssemblyAttributes.cs        (new: [assembly: SupportedOSPlatform("windows")])
  Models.cs                    (modify: BackupTarget, TargetType, FtpEncryption; BackupJob.Targets; BackupRun.TargetId/RunGroupId)
  CredentialProtector.cs       (new: DPAPI wrapper)
  IBackupTarget.cs             (modify: add IAsyncDisposable)
  LocalTarget.cs                (modify: trivial DisposeAsync)
  SparkVaultDatabase.cs         (modify: Targets table, Runs columns)
  BackupTargetRepository.cs     (new: CRUD for BackupTarget)
  JobRepository.cs              (modify: compose BackupTargetRepository, drop DestinationPath)
  RunRepository.cs              (modify: TargetId/RunGroupId, new group-query methods)
  TargetFactory.cs              (new: BackupTarget config -> IBackupTarget)
  FtpTarget.cs                   (new)
  SftpTarget.cs                  (new)
  BackupRunner.cs                (modify: multi-target loop, new RunAsync signature)
  BackgroundScheduler.cs         (modify: drop targetFactory constructor param)

src/SparkVault.App/
  App.xaml.cs                    (modify: remove CreateTarget, update scheduler/tray call sites)
  MainWindow.xaml / .xaml.cs      (modify: Targets column, call site update)
  TargetEditorWindow.xaml / .xaml.cs  (new: per-target type-specific form)
  JobEditorWindow.xaml / .xaml.cs (modify: target list management)
  LogWindow.xaml / .xaml.cs       (modify: Ziel column)

tests/SparkVault.Core.Tests/
  DockerTestHelper.cs             (new: shared TCP-reachability check)
  CredentialProtectorTests.cs     (new)
  BackupTargetRepositoryTests.cs  (new)
  JobRepositoryTests.cs           (modify: multi-target scenarios)
  RunRepositoryTests.cs           (modify: TargetId/RunGroupId, group queries)
  TargetFactoryTests.cs           (new)
  FtpTargetTests.cs               (new, Docker-dependent)
  SftpTargetTests.cs              (new, Docker-dependent)
  BackupRunnerTests.cs            (modify: new RunAsync signature, multi-target)
  BackgroundSchedulerTests.cs     (modify: drop targetFactory param)

docker/
  docker-compose.test.yml         (new)
```

---

### Task 1: NuGet dependencies, platform attribute, Docker test compose file

**Files:**
- Modify: `src/SparkVault.Core/SparkVault.Core.csproj`
- Create: `src/SparkVault.Core/AssemblyAttributes.cs`
- Create: `docker/docker-compose.test.yml`

**Interfaces:**
- Produces: the three new package references and the `SupportedOSPlatform` attribute every later Core task's DPAPI/FTP/SFTP code needs to compile warning-free; the Docker compose file Tasks 10-11's integration tests depend on being started manually before those tests actually assert anything.

- [ ] **Step 1: Add package references to `src/SparkVault.Core/SparkVault.Core.csproj`**

Add inside the existing `<ItemGroup>` with `Microsoft.Data.Sqlite`/`Serilog`:
```xml
    <PackageReference Include="FluentFTP" Version="54.2.0" />
    <PackageReference Include="SSH.NET" Version="2026.0.0" />
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />
```
Also add to the existing `<PropertyGroup>`:
```xml
    <SupportedOSPlatform>windows</SupportedOSPlatform>
```

- [ ] **Step 2: Create `src/SparkVault.Core/AssemblyAttributes.cs`**

```csharp
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]
```

- [ ] **Step 3: Run `dotnet restore` and `dotnet build`**

Run: `dotnet restore && dotnet build`
Expected: succeeds, 0 errors, 0 warnings. (If `dotnet restore` reports a security advisory for either package, stop and tell the controller — do not silently downgrade or ignore it; these exact versions were chosen because they were advisory-free at plan-writing time, but package advisories can be published later.)

- [ ] **Step 4: Create `docker/docker-compose.test.yml`**

```yaml
services:
  sparkvault-test-ftp:
    image: garethflowers/ftp-server:latest
    environment:
      FTP_USER: testuser
      FTP_PASS: testpass
    ports:
      - "2121:21"
      - "40000-40009:40000-40009"
  sparkvault-test-sftp:
    image: atmoz/sftp:latest
    command: testuser:testpass:::upload
    ports:
      - "2222:22"
```

- [ ] **Step 5: Start the containers and verify they come up**

Run: `docker compose -f docker/docker-compose.test.yml up -d`
Expected: both containers report `Up` (or `Up (healthy)`) within ~10 seconds. Run `docker ps` to confirm. Leave them running — Tasks 10-11's tests and Task 19's final verification depend on them being up. If Docker Desktop isn't running, start it first; if starting it isn't possible in this environment, note that in your report as DONE_WITH_CONCERNS rather than blocking — later tasks' Docker-dependent tests are designed to no-op (not fail) without a reachable server.

- [ ] **Step 6: Commit**

```bash
git add src/SparkVault.Core/SparkVault.Core.csproj src/SparkVault.Core/AssemblyAttributes.cs docker/docker-compose.test.yml
git commit -m "Add FluentFTP, SSH.NET, DPAPI packages and Docker test compose file"
```

---

### Task 2: Domain model changes

**Files:**
- Modify: `src/SparkVault.Core/Models.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `TargetType` enum, `FtpEncryption` enum, `BackupTarget` class, `BackupJob.Targets` (replaces `BackupJob.DestinationPath`), `BackupRun.TargetId`/`BackupRun.RunGroupId`. Every later task in this plan references these exact names/types — this is the shared vocabulary, same role `Models.cs` played in the MVP plan.

Read the current file first — it has `ScheduleType`, `BackupJob`, `RunStatus`, `BackupRun`, `BackupFile` already; you're modifying `BackupJob`/`BackupRun` and adding new types, not rewriting the whole file.

No dedicated test for the plain data holders (`BackupTarget`, the two new enums) — same call as the MVP's `Models.cs` task: trivial POCOs, correctness exercised indirectly by every later task that persists/reads them. `BackupJob`/`BackupRun`'s field changes are exercised by Tasks 6-8's repository tests.

- [ ] **Step 1: Modify `src/SparkVault.Core/Models.cs`**

Remove `DestinationPath` from `BackupJob` and add `Targets`:
```csharp
public sealed class BackupJob
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public List<string> ExcludePatterns { get; set; } = new();
    public ScheduleType ScheduleType { get; set; } = ScheduleType.None;
    public int? IntervalHours { get; set; }
    public TimeOnly? DailyAtTime { get; set; }
    public List<BackupTarget> Targets { get; set; } = new();
}
```

Add `TargetId` and `RunGroupId` to `BackupRun`:
```csharp
public sealed class BackupRun
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public int TargetId { get; set; }
    public Guid RunGroupId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public RunStatus Status { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorMessage { get; set; }
}
```

Add these new types (place near `BackupJob`, before or after it — match the file's existing top-to-bottom ordering style):
```csharp
public enum TargetType { Local, Ftp, Sftp }

public enum FtpEncryption { None, Explicit, Implicit }

public sealed class BackupTarget
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public TargetType Type { get; set; }

    // Local
    public string? DestinationPath { get; set; }

    // Ftp / Sftp shared
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? EncryptedPassword { get; set; }
    public string? RemotePath { get; set; }

    // Ftp only
    public FtpEncryption? EncryptionMode { get; set; }

    // Sftp only
    public string? PrivateKeyPath { get; set; }
    public string? EncryptedKeyPassphrase { get; set; }
}
```

Note the enum is named `FtpEncryption`, not `FtpEncryptionMode` — FluentFTP has its own `FtpEncryptionMode` enum, and `FtpTarget.cs` (Task 10) needs both `using FluentFTP;` and the implicit `SparkVault.Core` namespace in scope at once. Naming ours differently avoids an ambiguous-reference compile error (the same class of collision the MVP hit with `Application`/`MessageBox` from `System.Windows.Forms` vs `System.Windows`).

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: fails — `JobRepository`, `RunRepository`, `BackupRunner`, `MainWindow.xaml.cs`, `JobEditorWindow.xaml.cs`, `LogWindow.xaml.cs`, and their tests all still reference the old `BackupJob.DestinationPath` field and old `BackupRun`/`RunAsync` shapes. This is expected — Tasks 3-18 update all of them. Confirm the errors are all `CS1061`/`CS7036`-style "member not found"/"no argument"-style errors referencing `DestinationPath` or the old `RunAsync`/constructor signatures, not something unrelated to this change. Do not attempt to fix any other file in this task — that's every later task's job.

- [ ] **Step 3: Commit**

```bash
git add src/SparkVault.Core/Models.cs
git commit -m "Add BackupTarget, TargetType, FtpEncryption; multi-target BackupJob/BackupRun fields"
```

---

### Task 3: CredentialProtector (DPAPI wrapper)

**Files:**
- Create: `src/SparkVault.Core/CredentialProtector.cs`
- Test: `tests/SparkVault.Core.Tests/CredentialProtectorTests.cs`

**Interfaces:**
- Consumes: `System.Security.Cryptography.ProtectedData` (Task 1's new package).
- Produces: `CredentialProtector.Protect(string plaintext) -> string`, `CredentialProtector.Unprotect(string protectedValue) -> string`, used by Task 9 (`BackupTargetRepository` doesn't call these — the UI does, at save time — see Task 16/17) and Tasks 10-11 (`FtpTarget`/`SftpTarget` call `Unprotect` when connecting).

- [ ] **Step 1: Write the failing tests**

```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class CredentialProtectorTests
{
    [Fact]
    public void Protect_ThenUnprotect_RoundTripsPlaintext()
    {
        var protectedValue = CredentialProtector.Protect("s3cret-p@ss");
        var result = CredentialProtector.Unprotect(protectedValue);

        Assert.Equal("s3cret-p@ss", result);
    }

    [Fact]
    public void Protect_DoesNotReturnThePlaintext()
    {
        var protectedValue = CredentialProtector.Protect("s3cret-p@ss");

        Assert.DoesNotContain("s3cret-p@ss", protectedValue);
    }

    [Fact]
    public void Protect_HandlesEmptyString()
    {
        var protectedValue = CredentialProtector.Protect("");
        var result = CredentialProtector.Unprotect(protectedValue);

        Assert.Equal("", result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter CredentialProtectorTests`
Expected: FAIL to compile — `CredentialProtector` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace SparkVault.Core;

public static class CredentialProtector
{
    public static string Protect(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string protectedValue)
    {
        var protectedBytes = Convert.FromBase64String(protectedValue);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter CredentialProtectorTests`
Expected: PASS (3/3), no CA1416 warnings (the assembly-level `SupportedOSPlatform` attribute from Task 1 covers this).

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/CredentialProtector.cs tests/SparkVault.Core.Tests/CredentialProtectorTests.cs
git commit -m "Add DPAPI-based credential protector"
```

---

### Task 4: IBackupTarget gains IAsyncDisposable; LocalTarget updated

**Files:**
- Modify: `src/SparkVault.Core/IBackupTarget.cs`
- Modify: `src/SparkVault.Core/LocalTarget.cs`

**Interfaces:**
- Produces: `IBackupTarget : IAsyncDisposable` — every implementer (`LocalTarget`, and Tasks 10-11's `FtpTarget`/`SftpTarget`) must provide `DisposeAsync`. `BackupRunner` (Task 12) will do `await using var target = TargetFactory.Create(config);` per target, relying on this.

Why this change: connected targets (FTP/SFTP) need to close their connection after a target's uploads finish; `LocalTarget` never needed cleanup, but the interface has to cover every implementer uniformly. This is a deliberate, minimal interface extension — not a redesign.

No new dedicated test — `LocalTargetTests.cs` (from the MVP) already exercises `LocalTarget` without ever calling `DisposeAsync` explicitly, which still compiles fine (C# doesn't require you to dispose an `IAsyncDisposable`); a trivial no-op method doesn't need its own test (ponytail: trivial one-liners need no test).

- [ ] **Step 1: Modify `src/SparkVault.Core/IBackupTarget.cs`**

```csharp
namespace SparkVault.Core;

public sealed record TransferProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

public sealed record RemoteFileInfo(string Path, long Size);

public interface IBackupTarget : IAsyncDisposable
{
    Task<bool> TestConnectionAsync(CancellationToken ct);
    Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct);
    Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct);
    Task DeleteAsync(string remotePath, CancellationToken ct);
}
```

- [ ] **Step 2: Add a trivial `DisposeAsync` to `src/SparkVault.Core/LocalTarget.cs`**

Add this method to the `LocalTarget` class (anywhere among its other methods):
```csharp
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: still fails for the same pre-existing reasons as Task 2's Step 2 (BackupJob.DestinationPath, old RunAsync shape) — but confirm no NEW error appeared about `LocalTarget` or `IBackupTarget` themselves (i.e. `LocalTarget` now satisfies the extended interface cleanly). If a new error specifically about `IBackupTarget`/`LocalTarget` appears, that's a real problem to fix in this task.

- [ ] **Step 4: Run the existing LocalTarget tests**

Run: `dotnet test tests/SparkVault.Core.Tests --filter LocalTargetTests`
Expected: PASS (3/3) — unchanged, `DisposeAsync` being unused by these tests doesn't affect them.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/IBackupTarget.cs src/SparkVault.Core/LocalTarget.cs
git commit -m "Extend IBackupTarget with IAsyncDisposable for connected targets"
```

---

### Task 5: SQLite schema — Targets table, Runs columns

**Files:**
- Modify: `src/SparkVault.Core/SparkVaultDatabase.cs`

**Interfaces:**
- Produces: `Targets` table (Id, JobId, Type, DestinationPath, Host, Port, Username, EncryptedPassword, RemotePath, EncryptionMode, PrivateKeyPath, EncryptedKeyPassphrase); `Runs` table gains `TargetId INTEGER NOT NULL` and `RunGroupId TEXT NOT NULL`; `Jobs` table drops `DestinationPath`. Used by Task 6 (`BackupTargetRepository`), Task 7 (`JobRepository`), Task 8 (`RunRepository`).

Read the current file first (it has `EnsureCreated` and `DisablePooling` — you're only touching the `CommandText` inside `EnsureCreated`).

- [ ] **Step 1: Modify `src/SparkVault.Core/SparkVaultDatabase.cs`**

Replace the `CommandText` block inside `EnsureCreated` with:
```csharp
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Jobs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                ExcludePatterns TEXT NOT NULL,
                ScheduleType TEXT NOT NULL,
                IntervalHours INTEGER NULL,
                DailyAtTime TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Targets (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                Type TEXT NOT NULL,
                DestinationPath TEXT NULL,
                Host TEXT NULL,
                Port INTEGER NULL,
                Username TEXT NULL,
                EncryptedPassword TEXT NULL,
                RemotePath TEXT NULL,
                EncryptionMode TEXT NULL,
                PrivateKeyPath TEXT NULL,
                EncryptedKeyPassphrase TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Runs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                TargetId INTEGER NOT NULL,
                RunGroupId TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                Status TEXT NOT NULL,
                FileCount INTEGER NOT NULL,
                TotalBytes INTEGER NOT NULL,
                ErrorMessage TEXT NULL
            );
            """;
```

Note: `Jobs` no longer has `DestinationPath` — it moved to `Targets`. This is a breaking schema change from the MVP; per this plan's Global Constraints, no migration is written — an existing dev-machine `sparkvault.db` from the MVP must be deleted before running the new code (Task 19 does this).

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: still fails for the same pre-existing reasons as prior tasks (JobRepository/RunRepository/etc. not yet updated). No new errors specific to `SparkVaultDatabase.cs` itself.

- [ ] **Step 3: Commit**

```bash
git add src/SparkVault.Core/SparkVaultDatabase.cs
git commit -m "Add Targets table, TargetId/RunGroupId on Runs, drop DestinationPath from Jobs"
```

---

### Task 6: BackupTargetRepository

**Files:**
- Create: `src/SparkVault.Core/BackupTargetRepository.cs`
- Test: `tests/SparkVault.Core.Tests/BackupTargetRepositoryTests.cs`

**Interfaces:**
- Consumes: `BackupTarget`, `TargetType`, `FtpEncryption` (Task 2); `SparkVaultDatabase.EnsureCreated`/`DisablePooling` (Task 5).
- Produces: `class BackupTargetRepository(string connectionString)` with `int Add(BackupTarget target)`, `void Update(BackupTarget target)`, `void Delete(int id)`, `List<BackupTarget> GetByJobId(int jobId)` — used by Task 7 (`JobRepository`, which composes this repository internally).

Follow the exact per-method-fresh-`SqliteConnection` pattern already established in `JobRepository`/`RunRepository` (Task 5's `DisablePooling` call in the constructor, `AddWithValue` parameter binding, `SELECT last_insert_rowid()` for `Add`).

- [ ] **Step 1: Write the failing tests**

```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class BackupTargetRepositoryTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public void AddThenGetByJobId_RoundTripsLocalTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);

            var target = new BackupTarget { JobId = 1, Type = TargetType.Local, DestinationPath = @"D:\Backups\Docs" };
            var id = repo.Add(target);

            var loaded = repo.GetByJobId(1).Single();
            Assert.Equal(id, loaded.Id);
            Assert.Equal(TargetType.Local, loaded.Type);
            Assert.Equal(@"D:\Backups\Docs", loaded.DestinationPath);
            Assert.Null(loaded.Host);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddThenGetByJobId_RoundTripsFtpTargetAllFields()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);

            var target = new BackupTarget
            {
                JobId = 2,
                Type = TargetType.Ftp,
                Host = "ftp.example.com",
                Port = 21,
                Username = "user1",
                EncryptedPassword = "cGxhaW50ZXh0",
                RemotePath = "/backups",
                EncryptionMode = FtpEncryption.Explicit,
            };
            repo.Add(target);

            var loaded = repo.GetByJobId(2).Single();
            Assert.Equal(TargetType.Ftp, loaded.Type);
            Assert.Equal("ftp.example.com", loaded.Host);
            Assert.Equal(21, loaded.Port);
            Assert.Equal("user1", loaded.Username);
            Assert.Equal("cGxhaW50ZXh0", loaded.EncryptedPassword);
            Assert.Equal("/backups", loaded.RemotePath);
            Assert.Equal(FtpEncryption.Explicit, loaded.EncryptionMode);
            Assert.Null(loaded.DestinationPath);
            Assert.Null(loaded.PrivateKeyPath);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddThenGetByJobId_RoundTripsSftpTargetWithKeyAuth()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);

            var target = new BackupTarget
            {
                JobId = 3,
                Type = TargetType.Sftp,
                Host = "sftp.example.com",
                Port = 22,
                Username = "user2",
                PrivateKeyPath = @"C:\keys\id_rsa",
                EncryptedKeyPassphrase = "c2VjcmV0",
                RemotePath = "/upload",
            };
            repo.Add(target);

            var loaded = repo.GetByJobId(3).Single();
            Assert.Equal(TargetType.Sftp, loaded.Type);
            Assert.Equal(@"C:\keys\id_rsa", loaded.PrivateKeyPath);
            Assert.Equal("c2VjcmV0", loaded.EncryptedKeyPassphrase);
            Assert.Null(loaded.EncryptionMode);
            Assert.Null(loaded.EncryptedPassword);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Update_PersistsChanges()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);
            var id = repo.Add(new BackupTarget { JobId = 1, Type = TargetType.Local, DestinationPath = "C:\\a" });

            var target = repo.GetByJobId(1).Single();
            target.DestinationPath = "C:\\b";
            repo.Update(target);

            Assert.Equal("C:\\b", repo.GetByJobId(1).Single().DestinationPath);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Delete_RemovesTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);
            var id = repo.Add(new BackupTarget { JobId = 1, Type = TargetType.Local, DestinationPath = "C:\\a" });

            repo.Delete(id);

            Assert.Empty(repo.GetByJobId(1));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetByJobId_OnlyReturnsTargetsForThatJob()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);
            repo.Add(new BackupTarget { JobId = 1, Type = TargetType.Local, DestinationPath = "C:\\a" });
            repo.Add(new BackupTarget { JobId = 2, Type = TargetType.Local, DestinationPath = "C:\\b" });

            Assert.Single(repo.GetByJobId(1));
            Assert.Single(repo.GetByJobId(2));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter BackupTargetRepositoryTests`
Expected: FAIL to compile — `BackupTargetRepository` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class BackupTargetRepository
{
    private readonly string _connectionString;

    public BackupTargetRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
    }

    public int Add(BackupTarget target)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Targets (JobId, Type, DestinationPath, Host, Port, Username, EncryptedPassword,
                                  RemotePath, EncryptionMode, PrivateKeyPath, EncryptedKeyPassphrase)
            VALUES ($jobId, $type, $destPath, $host, $port, $username, $password,
                    $remotePath, $encMode, $keyPath, $keyPassphrase);
            SELECT last_insert_rowid();
            """;
        BindTargetParameters(command, target);

        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public void Update(BackupTarget target)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Targets
            SET JobId = $jobId, Type = $type, DestinationPath = $destPath, Host = $host, Port = $port,
                Username = $username, EncryptedPassword = $password, RemotePath = $remotePath,
                EncryptionMode = $encMode, PrivateKeyPath = $keyPath, EncryptedKeyPassphrase = $keyPassphrase
            WHERE Id = $id;
            """;
        BindTargetParameters(command, target);
        command.Parameters.AddWithValue("$id", target.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Targets WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public List<BackupTarget> GetByJobId(int jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Targets WHERE JobId = $jobId ORDER BY Id;";
        command.Parameters.AddWithValue("$jobId", jobId);

        using var reader = command.ExecuteReader();
        var targets = new List<BackupTarget>();
        while (reader.Read())
            targets.Add(ReadTarget(reader));

        return targets;
    }

    private static void BindTargetParameters(SqliteCommand command, BackupTarget target)
    {
        command.Parameters.AddWithValue("$jobId", target.JobId);
        command.Parameters.AddWithValue("$type", target.Type.ToString());
        command.Parameters.AddWithValue("$destPath", (object?)target.DestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$host", (object?)target.Host ?? DBNull.Value);
        command.Parameters.AddWithValue("$port", (object?)target.Port ?? DBNull.Value);
        command.Parameters.AddWithValue("$username", (object?)target.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("$password", (object?)target.EncryptedPassword ?? DBNull.Value);
        command.Parameters.AddWithValue("$remotePath", (object?)target.RemotePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$encMode", target.EncryptionMode is { } mode ? mode.ToString() : (object)DBNull.Value);
        command.Parameters.AddWithValue("$keyPath", (object?)target.PrivateKeyPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$keyPassphrase", (object?)target.EncryptedKeyPassphrase ?? DBNull.Value);
    }

    private static BackupTarget ReadTarget(SqliteDataReader reader)
    {
        string? GetNullableString(string column) =>
            reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

        return new BackupTarget
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            JobId = reader.GetInt32(reader.GetOrdinal("JobId")),
            Type = Enum.Parse<TargetType>(reader.GetString(reader.GetOrdinal("Type"))),
            DestinationPath = GetNullableString("DestinationPath"),
            Host = GetNullableString("Host"),
            Port = reader.IsDBNull(reader.GetOrdinal("Port")) ? null : reader.GetInt32(reader.GetOrdinal("Port")),
            Username = GetNullableString("Username"),
            EncryptedPassword = GetNullableString("EncryptedPassword"),
            RemotePath = GetNullableString("RemotePath"),
            EncryptionMode = GetNullableString("EncryptionMode") is { } m ? Enum.Parse<FtpEncryption>(m) : null,
            PrivateKeyPath = GetNullableString("PrivateKeyPath"),
            EncryptedKeyPassphrase = GetNullableString("EncryptedKeyPassphrase"),
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter BackupTargetRepositoryTests`
Expected: PASS (6/6).

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/BackupTargetRepository.cs tests/SparkVault.Core.Tests/BackupTargetRepositoryTests.cs
git commit -m "Add BackupTargetRepository"
```

---

### Task 7: JobRepository — compose BackupTargetRepository, drop DestinationPath

**Files:**
- Modify: `src/SparkVault.Core/JobRepository.cs`
- Modify: `tests/SparkVault.Core.Tests/JobRepositoryTests.cs`

**Interfaces:**
- Consumes: `BackupTargetRepository` (Task 6).
- Produces: same public method signatures as before (`Add`, `Update`, `Delete`, `GetById`, `GetAll`) — but `GetById`/`GetAll` now always return jobs with `Targets` fully populated, and `Add`/`Update`/`Delete` transparently keep the `Targets` table in sync. Every caller (BackupRunner, MainWindow, JobEditorWindow) can keep treating `JobRepository` as the single façade for a fully-usable `BackupJob`, exactly like the MVP.

Read the current file first. You're replacing it, not patching around the edges — every method changes because `DestinationPath` moves off `Jobs` and `Targets` becomes owned by this repository via composition with `BackupTargetRepository`.

`Update`'s target diffing: delete target rows that exist in the DB for this job but aren't in `job.Targets` anymore (by Id), update ones with a non-zero `Id`, insert ones with `Id == 0` (new). This preserves `BackupRun.TargetId` references for targets that still exist, instead of naively deleting-and-recreating every target on every edit (which would orphan every historical run log entry's `TargetId`).

- [ ] **Step 1: Update the failing tests in `tests/SparkVault.Core.Tests/JobRepositoryTests.cs`**

Replace the file's contents entirely with:
```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class JobRepositoryTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public void AddThenGetById_RoundTripsAllFieldsAndTargets()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);

            var job = new BackupJob
            {
                Name = "Documents",
                SourcePath = @"C:\Users\me\Documents",
                ExcludePatterns = new List<string> { "*.tmp", "cache\\*" },
                ScheduleType = ScheduleType.DailyAt,
                DailyAtTime = new TimeOnly(2, 0),
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = @"D:\Backups\Documents" },
                    new() { Type = TargetType.Sftp, Host = "sftp.example.com", Port = 22, Username = "u", RemotePath = "/x" },
                },
            };

            var id = repo.Add(job);
            var loaded = repo.GetById(id);

            Assert.NotNull(loaded);
            Assert.Equal("Documents", loaded!.Name);
            Assert.Equal(new List<string> { "*.tmp", "cache\\*" }, loaded.ExcludePatterns);
            Assert.Equal(ScheduleType.DailyAt, loaded.ScheduleType);
            Assert.Equal(new TimeOnly(2, 0), loaded.DailyAtTime);
            Assert.Equal(2, loaded.Targets.Count);
            Assert.Contains(loaded.Targets, t => t.Type == TargetType.Local && t.DestinationPath == @"D:\Backups\Documents");
            Assert.Contains(loaded.Targets, t => t.Type == TargetType.Sftp && t.Host == "sftp.example.com");
            Assert.All(loaded.Targets, t => Assert.NotEqual(0, t.Id));
            Assert.All(loaded.Targets, t => Assert.Equal(id, t.JobId));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Update_PreservesTargetIdForUnchangedTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob
            {
                Name = "Old",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\b" } },
            });

            var job = repo.GetById(id)!;
            var targetId = job.Targets.Single().Id;
            job.Name = "New";
            job.Targets.Single().DestinationPath = "C:\\c";
            repo.Update(job);

            var reloaded = repo.GetById(id)!;
            Assert.Equal("New", reloaded.Name);
            Assert.Equal(targetId, reloaded.Targets.Single().Id);
            Assert.Equal("C:\\c", reloaded.Targets.Single().DestinationPath);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Update_RemovesTargetsNoLongerPresent()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob
            {
                Name = "Job",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = "C:\\b" },
                    new() { Type = TargetType.Local, DestinationPath = "C:\\c" },
                },
            });

            var job = repo.GetById(id)!;
            job.Targets.RemoveAt(1);
            repo.Update(job);

            Assert.Single(repo.GetById(id)!.Targets);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Update_AddsNewTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob
            {
                Name = "Job",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\b" } },
            });

            var job = repo.GetById(id)!;
            job.Targets.Add(new BackupTarget { Type = TargetType.Ftp, Host = "ftp.example.com", RemotePath = "/x" });
            repo.Update(job);

            Assert.Equal(2, repo.GetById(id)!.Targets.Count);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Delete_RemovesJobAndItsTargets()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob
            {
                Name = "Temp",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\b" } },
            });

            repo.Delete(id);

            Assert.Null(repo.GetById(id));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetAll_ReturnsAllJobsWithTargets()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            repo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\a1" } } });
            repo.Add(new BackupJob { Name = "B", SourcePath = "C:\\b", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\b1" } } });

            var all = repo.GetAll();

            Assert.Equal(2, all.Count);
            Assert.All(all, j => Assert.Single(j.Targets));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter JobRepositoryTests`
Expected: FAIL to compile — `JobRepository`'s current implementation still references `job.DestinationPath`, which no longer exists on `BackupJob`.

- [ ] **Step 3: Replace `src/SparkVault.Core/JobRepository.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class JobRepository
{
    private readonly string _connectionString;
    private readonly BackupTargetRepository _targetRepository;

    public JobRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
        _targetRepository = new BackupTargetRepository(connectionString);
    }

    public int Add(BackupJob job)
    {
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Jobs (Name, SourcePath, ExcludePatterns, ScheduleType, IntervalHours, DailyAtTime)
                VALUES ($name, $source, $exclude, $scheduleType, $intervalHours, $dailyAtTime);
                SELECT last_insert_rowid();
                """;
            BindJobParameters(command, job);
            job.Id = Convert.ToInt32((long)command.ExecuteScalar()!);
        }

        foreach (var target in job.Targets)
        {
            target.JobId = job.Id;
            target.Id = _targetRepository.Add(target);
        }

        return job.Id;
    }

    public void Update(BackupJob job)
    {
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Jobs
                SET Name = $name, SourcePath = $source, ExcludePatterns = $exclude,
                    ScheduleType = $scheduleType, IntervalHours = $intervalHours, DailyAtTime = $dailyAtTime
                WHERE Id = $id;
                """;
            BindJobParameters(command, job);
            command.Parameters.AddWithValue("$id", job.Id);
            command.ExecuteNonQuery();
        }

        var existingIds = _targetRepository.GetByJobId(job.Id).Select(t => t.Id).ToHashSet();
        var currentIds = job.Targets.Where(t => t.Id != 0).Select(t => t.Id).ToHashSet();

        foreach (var staleId in existingIds.Except(currentIds))
            _targetRepository.Delete(staleId);

        foreach (var target in job.Targets)
        {
            target.JobId = job.Id;
            if (target.Id == 0)
                target.Id = _targetRepository.Add(target);
            else
                _targetRepository.Update(target);
        }
    }

    public void Delete(int id)
    {
        foreach (var target in _targetRepository.GetByJobId(id))
            _targetRepository.Delete(target.Id);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public BackupJob? GetById(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var job = ReadJob(reader);
        job.Targets = _targetRepository.GetByJobId(job.Id);
        return job;
    }

    public List<BackupJob> GetAll()
    {
        List<BackupJob> jobs;
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM Jobs ORDER BY Name;";

            using var reader = command.ExecuteReader();
            jobs = new List<BackupJob>();
            while (reader.Read())
                jobs.Add(ReadJob(reader));
        }

        foreach (var job in jobs)
            job.Targets = _targetRepository.GetByJobId(job.Id);

        return jobs;
    }

    private static void BindJobParameters(SqliteCommand command, BackupJob job)
    {
        command.Parameters.AddWithValue("$name", job.Name);
        command.Parameters.AddWithValue("$source", job.SourcePath);
        command.Parameters.AddWithValue("$exclude", string.Join('\n', job.ExcludePatterns));
        command.Parameters.AddWithValue("$scheduleType", job.ScheduleType.ToString());
        command.Parameters.AddWithValue("$intervalHours", (object?)job.IntervalHours ?? DBNull.Value);
        command.Parameters.AddWithValue("$dailyAtTime", (object?)job.DailyAtTime?.ToString("HH:mm") ?? DBNull.Value);
    }

    private static BackupJob ReadJob(SqliteDataReader reader)
    {
        var excludeRaw = reader.GetString(reader.GetOrdinal("ExcludePatterns"));
        return new BackupJob
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            SourcePath = reader.GetString(reader.GetOrdinal("SourcePath")),
            ExcludePatterns = excludeRaw.Length == 0
                ? new List<string>()
                : excludeRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
            ScheduleType = Enum.Parse<ScheduleType>(reader.GetString(reader.GetOrdinal("ScheduleType"))),
            IntervalHours = reader.IsDBNull(reader.GetOrdinal("IntervalHours")) ? null : reader.GetInt32(reader.GetOrdinal("IntervalHours")),
            DailyAtTime = reader.IsDBNull(reader.GetOrdinal("DailyAtTime")) ? null : TimeOnly.Parse(reader.GetString(reader.GetOrdinal("DailyAtTime"))),
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter JobRepositoryTests`
Expected: PASS (6/6).

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/JobRepository.cs tests/SparkVault.Core.Tests/JobRepositoryTests.cs
git commit -m "JobRepository: compose BackupTargetRepository for multi-target jobs"
```

---

### Task 8: RunRepository — TargetId/RunGroupId, group queries

**Files:**
- Modify: `src/SparkVault.Core/RunRepository.cs`
- Modify: `tests/SparkVault.Core.Tests/RunRepositoryTests.cs`

**Interfaces:**
- Produces: `Add`/`Update`/`GetByJobId`/`GetLatestByJobId` keep their exact signatures (still used by `BackgroundScheduler` exactly as before — see Task 13 — for its schedule-due check, where "approximately when the job last ran" is precise enough). New: `List<BackupRun> GetByRunGroupId(Guid runGroupId)` and `Guid? GetLatestRunGroupId(int jobId)`, used by Task 15 (`MainWindow`) to compute an accurate aggregated last-run status across every target — a single-target `GetLatestByJobId` row is not enough once a job can have multiple targets (see Task 15's own notes for why). `LogWindow` (Task 18) does NOT need these — it lists every run row directly via the unchanged `GetByJobId`, and rows from the same run group already sort together since `StartedAt DESC` naturally keeps sequentially-run targets adjacent.

Read the current file first — it already has the `ParseRoundtrip`/`DateTimeStyles.RoundtripKind` fix from the MVP's final review (a real timezone bug fix); do not remove or alter that logic, only add the new columns/methods around it.

- [ ] **Step 1: Update the failing tests in `tests/SparkVault.Core.Tests/RunRepositoryTests.cs`**

Replace the file's contents entirely with:
```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class RunRepositoryTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public void AddThenUpdate_PersistsCompletionIncludingTargetAndGroup()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "D:\\a" } } });
            var targetId = jobRepo.GetById(jobId)!.Targets.Single().Id;

            var runRepo = new RunRepository(connectionString);
            var groupId = Guid.NewGuid();
            var run = new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = groupId, StartedAt = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), Status = RunStatus.Failed };
            var runId = run.Id = runRepo.Add(run);

            run.Status = RunStatus.Success;
            run.EndedAt = new DateTime(2026, 8, 20, 10, 5, 0, DateTimeKind.Utc);
            run.FileCount = 3;
            run.TotalBytes = 1024;
            runRepo.Update(run);

            var loaded = runRepo.GetByJobId(jobId).Single(r => r.Id == runId);
            Assert.Equal(targetId, loaded.TargetId);
            Assert.Equal(groupId, loaded.RunGroupId);
            Assert.Equal(RunStatus.Success, loaded.Status);
            Assert.Equal(3, loaded.FileCount);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestByJobId_ReturnsMostRecentStart()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "D:\\a" } } });
            var targetId = jobRepo.GetById(jobId)!.Targets.Single().Id;

            var runRepo = new RunRepository(connectionString);
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = Guid.NewGuid(), StartedAt = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc), Status = RunStatus.Success });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = Guid.NewGuid(), StartedAt = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), Status = RunStatus.Success });

            var latest = runRepo.GetLatestByJobId(jobId);

            Assert.NotNull(latest);
            Assert.Equal(new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), latest!.StartedAt);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetByRunGroupId_ReturnsAllTargetsOfThatRun()
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
            var groupId = Guid.NewGuid();
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetIds[0], RunGroupId = groupId, StartedAt = DateTime.UtcNow, Status = RunStatus.Success });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetIds[1], RunGroupId = groupId, StartedAt = DateTime.UtcNow, Status = RunStatus.Failed });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetIds[0], RunGroupId = Guid.NewGuid(), StartedAt = DateTime.UtcNow.AddDays(-1), Status = RunStatus.Success });

            var groupRuns = runRepo.GetByRunGroupId(groupId);

            Assert.Equal(2, groupRuns.Count);
            Assert.All(groupRuns, r => Assert.Equal(groupId, r.RunGroupId));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestRunGroupId_ReturnsMostRecentGroup()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "D:\\a" } } });
            var targetId = jobRepo.GetById(jobId)!.Targets.Single().Id;

            var runRepo = new RunRepository(connectionString);
            var olderGroup = Guid.NewGuid();
            var newerGroup = Guid.NewGuid();
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = olderGroup, StartedAt = DateTime.UtcNow.AddHours(-2), Status = RunStatus.Success });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = newerGroup, StartedAt = DateTime.UtcNow, Status = RunStatus.Success });

            Assert.Equal(newerGroup, runRepo.GetLatestRunGroupId(jobId));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestRunGroupId_ReturnsNullWhenNoRuns()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var runRepo = new RunRepository(connectionString);

            Assert.Null(runRepo.GetLatestRunGroupId(999));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter RunRepositoryTests`
Expected: FAIL to compile — `BackupRun` has no `TargetId`/`RunGroupId` binding yet in `RunRepository`, and `GetByRunGroupId`/`GetLatestRunGroupId` don't exist.

- [ ] **Step 3: Modify `src/SparkVault.Core/RunRepository.cs`**

Update `BindRunParameters` to add the two new columns:
```csharp
    private static void BindRunParameters(SqliteCommand command, BackupRun run)
    {
        command.Parameters.AddWithValue("$jobId", run.JobId);
        command.Parameters.AddWithValue("$targetId", run.TargetId);
        command.Parameters.AddWithValue("$runGroupId", run.RunGroupId.ToString());
        command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$endedAt", (object?)run.EndedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$fileCount", run.FileCount);
        command.Parameters.AddWithValue("$totalBytes", run.TotalBytes);
        command.Parameters.AddWithValue("$error", (object?)run.ErrorMessage ?? DBNull.Value);
    }
```

Update `Add`'s `CommandText`:
```csharp
        command.CommandText = """
            INSERT INTO Runs (JobId, TargetId, RunGroupId, StartedAt, EndedAt, Status, FileCount, TotalBytes, ErrorMessage)
            VALUES ($jobId, $targetId, $runGroupId, $startedAt, $endedAt, $status, $fileCount, $totalBytes, $error);
            SELECT last_insert_rowid();
            """;
```

Update `Update`'s `CommandText`:
```csharp
        command.CommandText = """
            UPDATE Runs
            SET JobId = $jobId, TargetId = $targetId, RunGroupId = $runGroupId, StartedAt = $startedAt,
                EndedAt = $endedAt, Status = $status, FileCount = $fileCount, TotalBytes = $totalBytes, ErrorMessage = $error
            WHERE Id = $id;
            """;
```

Update `ReadRun` to populate the two new fields:
```csharp
    private static BackupRun ReadRun(SqliteDataReader reader)
    {
        return new BackupRun
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            JobId = reader.GetInt32(reader.GetOrdinal("JobId")),
            TargetId = reader.GetInt32(reader.GetOrdinal("TargetId")),
            RunGroupId = Guid.Parse(reader.GetString(reader.GetOrdinal("RunGroupId"))),
            StartedAt = ParseRoundtrip(reader.GetString(reader.GetOrdinal("StartedAt"))),
            EndedAt = reader.IsDBNull(reader.GetOrdinal("EndedAt")) ? null : ParseRoundtrip(reader.GetString(reader.GetOrdinal("EndedAt"))),
            Status = Enum.Parse<RunStatus>(reader.GetString(reader.GetOrdinal("Status"))),
            FileCount = reader.GetInt32(reader.GetOrdinal("FileCount")),
            TotalBytes = reader.GetInt64(reader.GetOrdinal("TotalBytes")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
        };
    }
```

Add two new methods (anywhere among the existing public methods, e.g. after `GetLatestByJobId`):
```csharp
    public List<BackupRun> GetByRunGroupId(Guid runGroupId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Runs WHERE RunGroupId = $runGroupId ORDER BY StartedAt;";
        command.Parameters.AddWithValue("$runGroupId", runGroupId.ToString());

        using var reader = command.ExecuteReader();
        var runs = new List<BackupRun>();
        while (reader.Read())
            runs.Add(ReadRun(reader));

        return runs;
    }

    public Guid? GetLatestRunGroupId(int jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RunGroupId FROM Runs WHERE JobId = $jobId ORDER BY StartedAt DESC LIMIT 1;";
        command.Parameters.AddWithValue("$jobId", jobId);

        var result = command.ExecuteScalar();
        return result is string s ? Guid.Parse(s) : null;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter RunRepositoryTests`
Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/RunRepository.cs tests/SparkVault.Core.Tests/RunRepositoryTests.cs
git commit -m "RunRepository: TargetId/RunGroupId columns and group queries"
```

---

### Task 9: TargetFactory

**Files:**
- Create: `src/SparkVault.Core/TargetFactory.cs`
- Test: `tests/SparkVault.Core.Tests/TargetFactoryTests.cs`

**Interfaces:**
- Consumes: `BackupTarget`, `TargetType` (Task 2); `IBackupTarget`, `LocalTarget` (Task 4); `FtpTarget` (Task 10, not yet built — this task's `Ftp`/`Sftp` branches will fail to compile until Tasks 10-11 land; see Step 1 note), `SftpTarget` (Task 11).
- Produces: `TargetFactory.Create(BackupTarget config) -> IBackupTarget`, used by Task 12 (`BackupRunner`) and Task 16 (`TargetEditorWindow`'s "Verbindung testen" button).

This task is written to depend on `FtpTarget`/`SftpTarget`, which don't exist until Tasks 10-11. **Do this task AFTER Tasks 10 and 11**, not in numeric order — the plan lists it here because it's conceptually the small "glue" task between the target implementations and everything that consumes them, but implement Tasks 10 and 11 first so this one actually compiles. (If you're executing tasks strictly in file order, skip ahead to Task 10, then 11, then come back to this one.)

- [ ] **Step 1: Write the failing tests**

```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class TargetFactoryTests
{
    [Fact]
    public void Create_Local_ReturnsLocalTarget()
    {
        var config = new BackupTarget { Type = TargetType.Local, DestinationPath = "C:\\a" };

        var target = TargetFactory.Create(config);

        Assert.IsType<LocalTarget>(target);
    }

    [Fact]
    public void Create_Ftp_ReturnsFtpTarget()
    {
        var config = new BackupTarget { Type = TargetType.Ftp, Host = "ftp.example.com", Username = "u", RemotePath = "/x" };

        var target = TargetFactory.Create(config);

        Assert.IsType<FtpTarget>(target);
    }

    [Fact]
    public void Create_Sftp_ReturnsSftpTarget()
    {
        var config = new BackupTarget { Type = TargetType.Sftp, Host = "sftp.example.com", Username = "u", RemotePath = "/x" };

        var target = TargetFactory.Create(config);

        Assert.IsType<SftpTarget>(target);
    }

    [Fact]
    public void Create_Local_WithoutDestinationPath_Throws()
    {
        var config = new BackupTarget { Type = TargetType.Local, DestinationPath = null };

        Assert.Throws<InvalidOperationException>(() => TargetFactory.Create(config));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter TargetFactoryTests`
Expected: FAIL to compile — `TargetFactory` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
namespace SparkVault.Core;

public static class TargetFactory
{
    public static IBackupTarget Create(BackupTarget config) => config.Type switch
    {
        TargetType.Local => new LocalTarget(config.DestinationPath ?? throw new InvalidOperationException("Local target requires DestinationPath.")),
        TargetType.Ftp => new FtpTarget(config),
        TargetType.Sftp => new SftpTarget(config),
        _ => throw new NotSupportedException($"Unbekannter Zieltyp: {config.Type}"),
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter TargetFactoryTests`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/TargetFactory.cs tests/SparkVault.Core.Tests/TargetFactoryTests.cs
git commit -m "Add TargetFactory dispatching BackupTarget config to concrete IBackupTarget"
```

---

### Task 10: FtpTarget (FluentFTP)

**Files:**
- Create: `src/SparkVault.Core/FtpTarget.cs`
- Create: `tests/SparkVault.Core.Tests/DockerTestHelper.cs`
- Create: `tests/SparkVault.Core.Tests/FtpTargetTests.cs`

**Interfaces:**
- Consumes: `BackupTarget`, `TargetType`, `FtpEncryption` (Task 2); `IBackupTarget` (Task 4); `CredentialProtector.Unprotect` (Task 3).
- Produces: `class FtpTarget(BackupTarget config) : IBackupTarget`, consumed by `TargetFactory` (Task 9, once you circle back to it) and `BackupRunner` (Task 12).

Every FluentFTP API call below was verified by the plan's author against the real installed `FluentFTP` 54.2.0 package, including a live round-trip (connect, create directory, upload, verify size, rename, get-info, delete, disconnect) against a real running `garethflowers/ftp-server` Docker container. Transcribe it as given — if something doesn't compile against the actual installed package version, that's a real discrepancy worth investigating carefully (check `dotnet list package` shows exactly `FluentFTP 54.2.0` — if a different version got restored, that's the likely cause) rather than guessing around it.

- [ ] **Step 1: Write `tests/SparkVault.Core.Tests/DockerTestHelper.cs`** (shared by this task and Task 11)

```csharp
namespace SparkVault.Core.Tests;

internal static class DockerTestHelper
{
    public static bool IsReachable(string host, int port, int timeoutMs = 500)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            return connectTask.Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
```

- [ ] **Step 2: Write the failing tests in `tests/SparkVault.Core.Tests/FtpTargetTests.cs`**

```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class FtpTargetTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2121;

    // ponytail: no Xunit.SkippableFact dependency yet — each test returns early (reports as a
    // trivial pass, not a failure) if `docker compose -f docker/docker-compose.test.yml up -d`
    // hasn't been run. Add SkippableFact (or move to xUnit v3's Assert.Skip) if a clearer
    // "skipped" signal in test output ever matters more than avoiding the extra dependency.

    private static BackupTarget NewTestConfig() => new()
    {
        Type = TargetType.Ftp,
        Host = Host,
        Port = Port,
        Username = "testuser",
        EncryptedPassword = CredentialProtector.Protect("testpass"),
        EncryptionMode = FtpEncryption.None,
        RemotePath = $"/test-{Guid.NewGuid():N}",
    };

    [Fact]
    public async Task UploadAsync_UploadsVerifiesAndListsFile()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello ftp");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            await using var target = new FtpTarget(NewTestConfig());

            Assert.True(await target.TestConnectionAsync(CancellationToken.None));
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.Contains(listed, f => f.Path == "a.txt" && f.Size == file.Size);

            await target.DeleteAsync("a.txt", CancellationToken.None);
            listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "a.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_NestedRelativePath_CreatesSubfolder()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "b.txt");
            await File.WriteAllTextAsync(filePath, "nested");
            // RelativePath uses a Windows-style backslash, exactly what FileScanner produces on Windows.
            var file = new BackupFile(filePath, "sub\\b.txt", new FileInfo(filePath).Length);

            await using var target = new FtpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.Contains(listed, f => f.Path == "sub/b.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_OnPreCancellation_LeavesNoFileBehind()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "c.txt");
            await File.WriteAllTextAsync(filePath, "cancel me");
            var file = new BackupFile(filePath, "c.txt", new FileInfo(filePath).Length);

            await using var target = new FtpTarget(NewTestConfig());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => target.UploadAsync(file, progress: null, cts.Token));

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "c.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TestConnectionAsync_WrongCredentials_ReturnsFalse()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var config = NewTestConfig();
        config.EncryptedPassword = CredentialProtector.Protect("wrong-password");

        await using var target = new FtpTarget(config);

        Assert.False(await target.TestConnectionAsync(CancellationToken.None));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter FtpTargetTests`
Expected: FAIL to compile — `FtpTarget` does not exist yet.

- [ ] **Step 4: Write `src/SparkVault.Core/FtpTarget.cs`**

```csharp
using FluentFTP;

namespace SparkVault.Core;

public sealed class FtpTarget : IBackupTarget
{
    private const string TempSuffix = ".sparkvault-tmp";
    private readonly BackupTarget _config;
    private readonly AsyncFtpClient _client;

    public FtpTarget(BackupTarget config)
    {
        _config = config;
        var ftpConfig = new FtpConfig
        {
            EncryptionMode = config.EncryptionMode switch
            {
                FtpEncryption.Explicit => FluentFTP.FtpEncryptionMode.Explicit,
                FtpEncryption.Implicit => FluentFTP.FtpEncryptionMode.Implicit,
                _ => FluentFTP.FtpEncryptionMode.None,
            },
        };
        var password = string.IsNullOrEmpty(config.EncryptedPassword)
            ? ""
            : CredentialProtector.Unprotect(config.EncryptedPassword);
        _client = new AsyncFtpClient(config.Host, config.Username, password, config.Port ?? 21, ftpConfig);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!_client.IsConnected)
            await _client.Connect(ct);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct);
            if (!await _client.DirectoryExists(_config.RemotePath, ct))
                await _client.CreateDirectory(_config.RemotePath, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureConnectedAsync(ct);

        var finalPath = RemotePath(file.RelativePath);
        var tempPath = finalPath + TempSuffix;

        try
        {
            await using var source = File.OpenRead(file.FullPath);
            var status = await _client.UploadStream(source, tempPath, FtpRemoteExists.Overwrite, createRemoteDir: true, token: ct);
            if (status != FtpStatus.Success)
                throw new IOException($"FTP-Upload fehlgeschlagen für {file.RelativePath}: {status}");

            var info = await _client.GetObjectInfo(tempPath, token: ct);
            if (info is null || info.Size != file.Size)
                throw new IOException(
                    $"Verifikation fehlgeschlagen für {file.RelativePath}: erwartet {file.Size} Bytes, erhalten {info?.Size ?? -1}.");

            if (await _client.FileExists(finalPath, ct))
                await _client.DeleteFile(finalPath, ct);
            await _client.Rename(tempPath, finalPath, ct);
        }
        catch
        {
            try { await _client.DeleteFile(tempPath, ct); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    public async Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        if (!await _client.DirectoryExists(_config.RemotePath, ct))
            return Enumerable.Empty<RemoteFileInfo>();

        var items = await _client.GetListing(_config.RemotePath, FtpListOption.Recursive, ct);
        var rootPrefix = _config.RemotePath.TrimEnd('/') + "/";
        return items
            .Where(i => i.Type == FtpObjectType.File)
            .Select(i => new RemoteFileInfo(
                i.FullName.StartsWith(rootPrefix, StringComparison.Ordinal) ? i.FullName[rootPrefix.Length..] : i.FullName,
                i.Size));
    }

    public async Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var full = RemotePath(remotePath);
        if (await _client.FileExists(full, ct))
            await _client.DeleteFile(full, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
            await _client.Disconnect();
        _client.Dispose();
    }

    // Remote paths are always forward-slash, regardless of RelativePath's Windows-style
    // backslashes — mixing separators here is the same class of bug the exclusion matcher
    // hit with backslash-as-escape; plain string concatenation avoids Path.* entirely.
    private string RemotePath(string relativePath) =>
        string.Concat(_config.RemotePath.TrimEnd('/'), "/", relativePath.Replace('\\', '/'));
}
```

- [ ] **Step 5: Start the Docker test containers if not already running**

Run: `docker compose -f docker/docker-compose.test.yml up -d` (from Task 1 — if they're still running from that task, this is a no-op).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter FtpTargetTests`
Expected: PASS (4/4) if Docker is reachable, or PASS (4/4) trivially if not (each test returns early — see the `DockerTestHelper` note). Report in your test summary which case actually happened; if Docker was reachable, confirm the assertions genuinely ran (not just "4 passed" — check the test output/duration; a suite of real FTP round-trips takes noticeably longer than a suite of instant no-ops).

- [ ] **Step 7: Commit**

```bash
git add src/SparkVault.Core/FtpTarget.cs tests/SparkVault.Core.Tests/DockerTestHelper.cs tests/SparkVault.Core.Tests/FtpTargetTests.cs
git commit -m "Add FtpTarget (FluentFTP) with temp-name-then-commit uploads"
```

---

### Task 11: SftpTarget (SSH.NET)

**Files:**
- Create: `src/SparkVault.Core/SftpTarget.cs`
- Create: `tests/SparkVault.Core.Tests/SftpTargetTests.cs`

**Interfaces:**
- Consumes: `BackupTarget`, `TargetType` (Task 2); `IBackupTarget` (Task 4); `CredentialProtector.Unprotect` (Task 3); `DockerTestHelper` (Task 10).
- Produces: `class SftpTarget(BackupTarget config) : IBackupTarget`, consumed by `TargetFactory` (Task 9, once you circle back to it) and `BackupRunner` (Task 12).

Every SSH.NET API call below was verified against the real installed `SSH.NET` 2026.0.0 package, including a live round-trip (connect, exists-check, create directory, upload, verify size via `GetAttributes`, rename, delete, disconnect) against a real running `atmoz/sftp` Docker container.

**Important — SSH.NET's `SftpClient` API is synchronous**, unlike FluentFTP's `AsyncFtpClient`. There is no native `CancellationToken` support mid-transfer. Each operation below is wrapped in `Task.Run` so it doesn't block the calling thread (e.g. the WPF UI thread during a manual "Jetzt sichern" click), and `ct` is checked before each step starts. Cancellation of an already-started upload is best-effort only (the transfer itself still runs to completion before the `OperationCanceledException` observation point is reached on the next check) — this is a deliberate, documented scope limitation, not a bug to work around with more plumbing.

- [ ] **Step 1: Write the failing tests in `tests/SparkVault.Core.Tests/SftpTargetTests.cs`**

```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class SftpTargetTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    // atmoz/sftp with command "testuser:testpass:::upload" chroots the user and exposes a
    // writable "upload" directory — remote paths in these tests must live under /upload.

    private static BackupTarget NewTestConfig() => new()
    {
        Type = TargetType.Sftp,
        Host = Host,
        Port = Port,
        Username = "testuser",
        EncryptedPassword = CredentialProtector.Protect("testpass"),
        RemotePath = $"/upload/test-{Guid.NewGuid():N}",
    };

    [Fact]
    public async Task UploadAsync_UploadsVerifiesAndListsFile()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello sftp");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            await using var target = new SftpTarget(NewTestConfig());

            Assert.True(await target.TestConnectionAsync(CancellationToken.None));
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.Contains(listed, f => f.Path == "a.txt" && f.Size == file.Size);

            await target.DeleteAsync("a.txt", CancellationToken.None);
            listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "a.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_NestedRelativePath_CreatesSubfolder()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "b.txt");
            await File.WriteAllTextAsync(filePath, "nested");
            var file = new BackupFile(filePath, "sub\\b.txt", new FileInfo(filePath).Length);

            await using var target = new SftpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.Contains(listed, f => f.Path == "sub/b.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_OnPreCancellation_LeavesNoFileBehind()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "c.txt");
            await File.WriteAllTextAsync(filePath, "cancel me");
            var file = new BackupFile(filePath, "c.txt", new FileInfo(filePath).Length);

            await using var target = new SftpTarget(NewTestConfig());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => target.UploadAsync(file, progress: null, cts.Token));

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "c.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TestConnectionAsync_WrongCredentials_ReturnsFalse()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var config = NewTestConfig();
        config.EncryptedPassword = CredentialProtector.Protect("wrong-password");

        await using var target = new SftpTarget(config);

        Assert.False(await target.TestConnectionAsync(CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter SftpTargetTests`
Expected: FAIL to compile — `SftpTarget` does not exist yet.

- [ ] **Step 3: Write `src/SparkVault.Core/SftpTarget.cs`**

```csharp
using Renci.SshNet;

namespace SparkVault.Core;

public sealed class SftpTarget : IBackupTarget
{
    private const string TempSuffix = ".sparkvault-tmp";
    private readonly BackupTarget _config;
    private readonly SftpClient _client;

    public SftpTarget(BackupTarget config)
    {
        _config = config;

        var authMethods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(config.EncryptedPassword))
            authMethods.Add(new PasswordAuthenticationMethod(config.Username, CredentialProtector.Unprotect(config.EncryptedPassword)));
        if (!string.IsNullOrEmpty(config.PrivateKeyPath))
        {
            var passphrase = string.IsNullOrEmpty(config.EncryptedKeyPassphrase)
                ? null
                : CredentialProtector.Unprotect(config.EncryptedKeyPassphrase);
            authMethods.Add(new PrivateKeyAuthenticationMethod(config.Username, new PrivateKeyFile(config.PrivateKeyPath, passphrase)));
        }

        var connectionInfo = new ConnectionInfo(config.Host, config.Port ?? 22, config.Username, authMethods.ToArray());
        _client = new SftpClient(connectionInfo);
    }

    private Task EnsureConnectedAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!_client.IsConnected)
            _client.Connect();
    }, ct);

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct);
            await Task.Run(() =>
            {
                if (!_client.Exists(_config.RemotePath))
                    CreateDirectoryRecursive(_config.RemotePath);
            }, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureConnectedAsync(ct);

        var finalPath = RemotePath(file.RelativePath);
        var tempPath = finalPath + TempSuffix;

        try
        {
            await Task.Run(() =>
            {
                var remoteDir = finalPath[..finalPath.LastIndexOf('/')];
                if (!_client.Exists(remoteDir))
                    CreateDirectoryRecursive(remoteDir);

                using var source = File.OpenRead(file.FullPath);
                _client.UploadFile(source, tempPath, canOverride: true);

                var attrs = _client.GetAttributes(tempPath);
                if (attrs.Size != file.Size)
                    throw new IOException(
                        $"Verifikation fehlgeschlagen für {file.RelativePath}: erwartet {file.Size} Bytes, erhalten {attrs.Size}.");

                if (_client.Exists(finalPath))
                    _client.DeleteFile(finalPath);
                _client.RenameFile(tempPath, finalPath);
            }, ct);
        }
        catch
        {
            try { await Task.Run(() => { if (_client.Exists(tempPath)) _client.DeleteFile(tempPath); }); } catch { /* best effort cleanup */ }
            throw;
        }
    }

    public async Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        return await Task.Run(() =>
        {
            if (!_client.Exists(_config.RemotePath))
                return Enumerable.Empty<RemoteFileInfo>();

            var results = new List<RemoteFileInfo>();
            WalkDirectory(_config.RemotePath, results);
            return (IEnumerable<RemoteFileInfo>)results;
        }, ct);
    }

    public async Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var full = RemotePath(remotePath);
        await Task.Run(() =>
        {
            if (_client.Exists(full))
                _client.DeleteFile(full);
        }, ct);
    }

    public ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
            _client.Disconnect();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private void CreateDirectoryRecursive(string path)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!_client.Exists(current))
                _client.CreateDirectory(current);
        }
    }

    private void WalkDirectory(string path, List<RemoteFileInfo> results)
    {
        var rootPrefix = _config.RemotePath.TrimEnd('/') + "/";
        foreach (var entry in _client.ListDirectory(path))
        {
            if (entry.Name is "." or "..") continue;

            if (entry.IsDirectory)
            {
                WalkDirectory(entry.FullName, results);
            }
            else
            {
                var relative = entry.FullName.StartsWith(rootPrefix, StringComparison.Ordinal)
                    ? entry.FullName[rootPrefix.Length..]
                    : entry.FullName;
                results.Add(new RemoteFileInfo(relative, entry.Length));
            }
        }
    }

    // Same reasoning as FtpTarget: plain string concatenation on forward slashes, never Path.*.
    private string RemotePath(string relativePath) =>
        string.Concat(_config.RemotePath.TrimEnd('/'), "/", relativePath.Replace('\\', '/'));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter SftpTargetTests`
Expected: PASS (4/4) if Docker is reachable (containers from Task 1/10 should still be up — run `docker ps` to confirm, `docker compose -f docker/docker-compose.test.yml up -d` if not), or PASS (4/4) trivially if not. Same reporting expectation as Task 10 Step 6: state which case happened.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/SftpTarget.cs tests/SparkVault.Core.Tests/SftpTargetTests.cs
git commit -m "Add SftpTarget (SSH.NET) with temp-name-then-commit uploads"
```

---

**Reminder:** go back and finish Task 9 (`TargetFactory`) now if you haven't — it depends on `FtpTarget`/`SftpTarget`, which now exist. Run `dotnet test tests/SparkVault.Core.Tests --filter TargetFactoryTests` and confirm 4/4 before continuing to Task 12.

---

### Task 12: BackupRunner — multi-target loop

**Files:**
- Modify: `src/SparkVault.Core/BackupRunner.cs`
- Modify: `tests/SparkVault.Core.Tests/BackupRunnerTests.cs`

**Interfaces:**
- Consumes: `TargetFactory.Create` (Task 9); `FileScanner.Scan` (MVP); `RunRepository` incl. `TargetId`/`RunGroupId` (Task 8).
- Produces: `Task<IReadOnlyList<BackupRun>> RunAsync(BackupJob job, IProgress<TransferProgress>? progress, CancellationToken ct)` — **note the signature change**: the MVP-era `IBackupTarget target` parameter is gone (each target is now constructed internally, once per `job.Targets` entry, via `TargetFactory.Create`). `RunStarted`/`RunCompleted` keep their exact MVP event signatures (`Action<BackupJob>` / `Action<BackupJob, RunStatus>`) — `RunStarted` fires once per job-level call, `RunCompleted` fires once at the end with the aggregated status. Used by Task 13 (`BackgroundScheduler`), Task 14 (`App.xaml.cs` tray submenu), Task 15 (`MainWindow`).

Read the current file first (it already has the `SemaphoreSlim` lock and per-run `try/catch/finally` bookkeeping from the MVP's final review) — you're restructuring the single-target body into a per-target loop inside the same lock scope, not bolting something on top.

- [ ] **Step 1: Update the failing tests in `tests/SparkVault.Core.Tests/BackupRunnerTests.cs`**

Replace the file's contents entirely with:
```csharp
using Serilog;
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class BackupRunnerTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public async Task RunAsync_SingleTarget_CopiesFilesAndRecordsSuccessfulRun()
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
            var runner = new BackupRunner(runRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Single(results);
            Assert.Equal(RunStatus.Success, results[0].Status);
            Assert.Equal(2, results[0].FileCount);
            Assert.Equal(11, results[0].TotalBytes);
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "b.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_MultipleTargets_RecordsOneRunPerTargetWithSharedGroupId()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir1 = Directory.CreateTempSubdirectory("sparkvault-dest1-");
        var destDir2 = Directory.CreateTempSubdirectory("sparkvault-dest2-");
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
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = destDir1.FullName },
                    new() { Type = TargetType.Local, DestinationPath = destDir2.FullName },
                },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(RunStatus.Success, r.Status));
            var groupIds = results.Select(r => r.RunGroupId).Distinct().ToList();
            Assert.Single(groupIds);
            var targetIds = results.Select(r => r.TargetId).Distinct().ToList();
            Assert.Equal(2, targetIds.Count);
            Assert.True(File.Exists(Path.Combine(destDir1.FullName, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir2.FullName, "a.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir1.Delete(recursive: true);
            destDir2.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_OneTargetFails_OtherTargetStillAttempted()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDirOk = Directory.CreateTempSubdirectory("sparkvault-dest-ok-");
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
                Targets = new List<BackupTarget>
                {
                    // Unreachable FTP host — TestConnectionAsync will fail fast.
                    new() { Type = TargetType.Ftp, Host = "127.0.0.1", Port = 1, Username = "x", EncryptedPassword = CredentialProtector.Protect("x"), RemotePath = "/x" },
                    new() { Type = TargetType.Local, DestinationPath = destDirOk.FullName },
                },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.Equal(RunStatus.Failed, results[0].Status);
            Assert.Equal(RunStatus.Success, results[1].Status);
            Assert.True(File.Exists(Path.Combine(destDirOk.FullName, "a.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDirOk.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_OnSourceMissing_RecordsFailedRunForEveryTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = Path.Combine(Path.GetTempPath(), "sparkvault-does-not-exist"),
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Single(results);
            Assert.Equal(RunStatus.Failed, results[0].Status);
            Assert.NotNull(results[0].ErrorMessage);
        }
        finally
        {
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_FiresRunStartedOnceAndRunCompletedOnceWithAggregatedStatus()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir1 = Directory.CreateTempSubdirectory("sparkvault-dest1-");
        var destDir2 = Directory.CreateTempSubdirectory("sparkvault-dest2-");
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
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = destDir1.FullName },
                    new() { Type = TargetType.Local, DestinationPath = destDir2.FullName },
                },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var startedCount = 0;
            var completedStatuses = new List<RunStatus>();
            runner.RunStarted += _ => startedCount++;
            runner.RunCompleted += (_, status) => completedStatuses.Add(status);

            await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(1, startedCount);
            Assert.Equal(new[] { RunStatus.Success }, completedStatuses);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir1.Delete(recursive: true);
            destDir2.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter BackupRunnerTests`
Expected: FAIL to compile — `RunAsync`'s current signature still takes an `IBackupTarget target` parameter and returns a single `BackupRun`.

- [ ] **Step 3: Replace `src/SparkVault.Core/BackupRunner.cs`**

```csharp
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

        try
        {
            await _runLock.WaitAsync(ct);
            lockHeld = true;

            IReadOnlyList<BackupFile> files;
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

                RunCompleted?.Invoke(job, RunStatus.Failed);
                return results;
            }

            foreach (var targetConfig in job.Targets)
                results.Add(await RunForTargetAsync(job, targetConfig, runGroupId, files, progress, ct));
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
                throw new IOException($"Ziel nicht erreichbar: {DescribeTarget(targetConfig)}");

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
                job.Name, DescribeTarget(targetConfig), done, bytesDone);
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Cancelled;
            _logger.Warning("Job {JobName} -> {Target} was cancelled", job.Name, DescribeTarget(targetConfig));
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            _logger.Error(ex, "Job {JobName} -> {Target} failed", job.Name, DescribeTarget(targetConfig));
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
        _runRepository.Update(run);
        _logger.Error("Job {JobName} -> {Target} failed: {Error}", job.Name, DescribeTarget(targetConfig), errorMessage);
        return run;
    }

    private static string DescribeTarget(BackupTarget target) => target.Type switch
    {
        TargetType.Local => $"Local:{target.DestinationPath}",
        TargetType.Ftp => $"FTP:{target.Host}",
        TargetType.Sftp => $"SFTP:{target.Host}",
        _ => target.Type.ToString(),
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter BackupRunnerTests`
Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/BackupRunner.cs tests/SparkVault.Core.Tests/BackupRunnerTests.cs
git commit -m "BackupRunner: iterate multiple targets per run, one BackupRun per target"
```

---

### Task 13: BackgroundScheduler — drop the targetFactory parameter

**Files:**
- Modify: `src/SparkVault.Core/BackgroundScheduler.cs`
- Modify: `tests/SparkVault.Core.Tests/BackgroundSchedulerTests.cs`

**Interfaces:**
- Produces: `BackgroundScheduler(JobRepository jobRepository, RunRepository runRepository, BackupRunner runner, TimeSpan pollInterval, ILogger logger)` — the `Func<BackupJob, IBackupTarget> targetFactory` parameter is gone (no longer needed: `BackupRunner.RunAsync` now builds each target itself from `job.Targets`, per Task 12). Used by Task 14 (`App.xaml.cs`).

Read the current file first — you're deleting the `_targetFactory` field and its constructor parameter, and simplifying the `RunAsync` call inside `LoopAsync`. Everything else (the `GetJobsSafely` guard, the per-job `try/catch` from the MVP's final review, `IAsyncDisposable`) stays as-is.

- [ ] **Step 1: Update the failing test in `tests/SparkVault.Core.Tests/BackgroundSchedulerTests.cs`**

Replace the file's contents entirely with:
```csharp
using Serilog;
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class BackgroundSchedulerTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public async Task RunsDueJobOnFirstTick()
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
                Name = "Scheduled",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.Interval,
                IntervalHours = 6,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            await using var scheduler = new BackgroundScheduler(
                jobRepo, runRepo, runner,
                pollInterval: TimeSpan.FromMilliseconds(50),
                Log.Logger);

            await Task.Delay(TimeSpan.FromMilliseconds(400));

            var runs = runRepo.GetByJobId(jobId);
            Assert.NotEmpty(runs);
            Assert.Equal(RunStatus.Success, runs[0].Status);
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

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SparkVault.Core.Tests --filter BackgroundSchedulerTests`
Expected: FAIL to compile — the constructor call above doesn't pass a `targetFactory` argument, which the current constructor still requires.

- [ ] **Step 3: Modify `src/SparkVault.Core/BackgroundScheduler.cs`**

Remove the `_targetFactory` field and the `targetFactory` constructor parameter:
```csharp
    private readonly JobRepository _jobRepository;
    private readonly RunRepository _runRepository;
    private readonly BackupRunner _runner;
    private readonly ILogger _logger;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public BackgroundScheduler(
        JobRepository jobRepository,
        RunRepository runRepository,
        BackupRunner runner,
        TimeSpan pollInterval,
        ILogger logger)
    {
        _jobRepository = jobRepository;
        _runRepository = runRepository;
        _runner = runner;
        _logger = logger;
        _timer = new PeriodicTimer(pollInterval);
        _loopTask = Task.Run(LoopAsync);
    }
```

Simplify the `RunAsync` call inside `LoopAsync` (drop the `_targetFactory(job)` argument):
```csharp
                        if (ScheduleCalculator.IsDue(job, lastRun?.StartedAt.ToLocalTime(), DateTime.Now))
                        {
                            await _runner.RunAsync(job, progress: null, _cts.Token);
                        }
```

Everything else in the file (`GetJobsSafely`, `DisposeAsync`, the `catch` blocks) is unchanged.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SparkVault.Core.Tests --filter BackgroundSchedulerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/BackgroundScheduler.cs tests/SparkVault.Core.Tests/BackgroundSchedulerTests.cs
git commit -m "BackgroundScheduler: drop targetFactory, BackupRunner builds targets itself"
```

---

### Task 14: App.xaml.cs — remove CreateTarget, update call sites

**Files:**
- Modify: `src/SparkVault.App/App.xaml.cs`

**Interfaces:**
- Consumes: `BackgroundScheduler`'s new constructor (Task 13); `BackupRunner.RunAsync`'s new signature (Task 12).
- Produces: no new public surface — `App.JobRepository`/`RunRepository`/`Runner` stay exactly as before. `CreateTarget` is deleted entirely (target construction now lives inside `BackupRunner`, via `TargetFactory`, invisible to the composition root).

Read the current file first. Three changes only: delete the `CreateTarget` static method, update the `BackgroundScheduler` constructor call (drop the `CreateTarget` factory argument, add the new `pollInterval`/`logger` positions if they shifted — check the actual parameter order from Task 13), update the tray "Jetzt sichern" submenu's `Click` handler (drop `CreateTarget(job)` from the `Runner.RunAsync` call). Everything else (`DispatcherUnhandledException`, `EnsureAutostartRegistered`, tray icon setup, `SetTrayStatus`, `OnExit`) is unchanged.

- [ ] **Step 1: Delete the `CreateTarget` method**

Remove this line entirely:
```csharp
    /// <summary>Single place that decides how a job's backup target is built.</summary>
    public static IBackupTarget CreateTarget(BackupJob job) => new LocalTarget(job.DestinationPath);
```

- [ ] **Step 2: Update the `BackgroundScheduler` construction**

Change:
```csharp
        _scheduler = new BackgroundScheduler(
            JobRepository,
            RunRepository,
            Runner,
            CreateTarget,
            pollInterval: TimeSpan.FromMinutes(1),
            Log.Logger);
```
to:
```csharp
        _scheduler = new BackgroundScheduler(
            JobRepository,
            RunRepository,
            Runner,
            pollInterval: TimeSpan.FromMinutes(1),
            Log.Logger);
```

- [ ] **Step 3: Update the tray "Jetzt sichern" submenu's Click handler**

Change:
```csharp
                jobItem.Click += async (_, _) =>
                    await Runner.RunAsync(job, CreateTarget(job), progress: null, CancellationToken.None);
```
to:
```csharp
                jobItem.Click += async (_, _) =>
                    await Runner.RunAsync(job, progress: null, CancellationToken.None);
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `SparkVault.Core` and `SparkVault.Core.Tests` now build cleanly (0 errors). `SparkVault.App` still fails — `MainWindow.xaml.cs`, `JobEditorWindow.xaml.cs`/`.xaml`, `LogWindow.xaml.cs` still reference the old single-target shape (`job.DestinationPath`, `App.CreateTarget`, old `RunAsync` signature). Confirm the remaining errors are all in those three files, nothing in `App.xaml.cs` itself.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.App/App.xaml.cs
git commit -m "App.xaml.cs: drop CreateTarget, BackupRunner builds targets internally now"
```

---

### Task 15: MainWindow — Targets column, call site update

**Files:**
- Modify: `src/SparkVault.App/MainWindow.xaml`
- Modify: `src/SparkVault.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `App.Runner.RunAsync`'s new signature (Task 12); `BackupJob.Targets`, `BackupTarget` (Task 2); `RunRepository.GetLatestRunGroupId`/`GetByRunGroupId` (Task 8).
- Produces: `JobRow` gains `TargetsDisplay` (replaces `DestinationPath`), consumed only by this file's own `MainWindow.xaml` binding.

Read both files first. Same structural pattern as the MVP's `MainWindow.xaml.cs` (`JobRow`, `ReloadJobs`, `DescribeNextRun`, button handlers) — you're changing the "Ziel" column's data source, the `RunNowButton_Click` call site, and — this is the part that's easy to get subtly wrong — the "Letzter Lauf"/"Status" logic.

**Why "Letzter Lauf"/"Status" need more than a find-and-replace:** the MVP's `ReloadJobs` called `App.RunRepository.GetLatestByJobId(job.Id)`, which returns a single most-recent `BackupRun` row. With multi-target jobs, that row now represents only *one* target's outcome (whichever target happened to run last in the sequence) — showing its status as "the job's status" would silently hide a failure in an earlier target while a later target succeeded. Use `RunRepository.GetLatestRunGroupId`/`GetByRunGroupId` (Task 8) instead, to fetch every row from the most recent run group and aggregate: `Success` only if every target in that group succeeded, matching exactly the same aggregation rule `BackupRunner.RunAsync` (Task 12) already uses for its own `RunCompleted` event — don't invent a different rule here.

- [ ] **Step 1: Modify `src/SparkVault.App/MainWindow.xaml`**

Change the DataGrid column bound to the destination path:
```xml
                <DataGridTextColumn Header="Ziele" Binding="{Binding TargetsDisplay}" Width="220" />
```
(This replaces the existing `<DataGridTextColumn Header="Ziel" Binding="{Binding DestinationPath}" Width="200" />` — same position in the column list, just before "Letzter Lauf".)

- [ ] **Step 2: Modify `src/SparkVault.App/MainWindow.xaml.cs`**

Change `JobRow`'s `DestinationPath` property to `TargetsDisplay`:
```csharp
public sealed class JobRow
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string TargetsDisplay { get; init; } = "";
    public string LastRunDisplay { get; init; } = "-";
    public string NextRunDisplay { get; init; } = "-";
    public string LastStatusDisplay { get; init; } = "-";
}
```

Replace the whole `ReloadJobs` method:
```csharp
    private void ReloadJobs()
    {
        _jobs.Clear();
        foreach (var job in App.JobRepository.GetAll())
        {
            var latestGroupId = App.RunRepository.GetLatestRunGroupId(job.Id);
            var groupRuns = latestGroupId is { } groupId ? App.RunRepository.GetByRunGroupId(groupId) : new List<BackupRun>();
            DateTime? lastRunStartedAt = groupRuns.Count > 0 ? groupRuns.Min(r => r.StartedAt) : null;
            RunStatus? lastStatus = groupRuns.Count == 0
                ? null
                : groupRuns.All(r => r.Status == RunStatus.Success) ? RunStatus.Success : RunStatus.Failed;

            _jobs.Add(new JobRow
            {
                Id = job.Id,
                Name = job.Name,
                SourcePath = job.SourcePath,
                TargetsDisplay = string.Join("; ", job.Targets.Select(DescribeTarget)),
                LastRunDisplay = lastRunStartedAt?.ToLocalTime().ToString("g") ?? "-",
                NextRunDisplay = DescribeNextRun(job, lastRunStartedAt),
                LastStatusDisplay = lastStatus?.ToString() ?? "-",
            });
        }
    }
```

Note `lastRunStartedAt` uses the *earliest* `StartedAt` across the group (when the job-level run actually began), not any single target's individual timestamp.

Replace `DescribeNextRun`'s signature — it only ever used `lastRun.StartedAt`, so take a `DateTime?` directly instead of a whole `BackupRun?`:
```csharp
    private static string DescribeNextRun(BackupJob job, DateTime? lastRunStartedAt)
    {
        switch (job.ScheduleType)
        {
            case ScheduleType.Interval when job.IntervalHours is { } hours:
                if (lastRunStartedAt is null) return "fällig";
                return lastRunStartedAt.Value.ToLocalTime().AddHours(hours).ToString("g");

            case ScheduleType.DailyAt when job.DailyAtTime is { } time:
                var todayTarget = DateTime.Today + time.ToTimeSpan();
                var next = DateTime.Now < todayTarget ? todayTarget : todayTarget.AddDays(1);
                return next.ToString("g");

            default:
                return "-";
        }
    }
```

Add this helper (near `DescribeNextRun`):
```csharp
    private static string DescribeTarget(BackupTarget target) => target.Type switch
    {
        TargetType.Local => $"Lokal: {target.DestinationPath}",
        TargetType.Ftp => $"FTP: {target.Host}",
        TargetType.Sftp => $"SFTP: {target.Host}",
        _ => target.Type.ToString(),
    };
```

In `RunNowButton_Click`, change:
```csharp
            await App.Runner.RunAsync(job, App.CreateTarget(job), progress, CancellationToken.None);
```
to:
```csharp
            await App.Runner.RunAsync(job, progress, CancellationToken.None);
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `MainWindow.xaml.cs` now builds. `JobEditorWindow.xaml.cs`/`.xaml` and `LogWindow.xaml.cs` still fail (old single-target shape) — Tasks 16-18 fix them. Confirm no error remains that references `MainWindow.xaml.cs` or its `.xaml`.

- [ ] **Step 4: Commit**

```bash
git add src/SparkVault.App/MainWindow.xaml src/SparkVault.App/MainWindow.xaml.cs
git commit -m "MainWindow: show multiple targets per job, update RunAsync call site"
```

---

### Task 16: TargetEditorWindow (new)

**Files:**
- Create: `src/SparkVault.App/TargetEditorWindow.xaml`
- Create: `src/SparkVault.App/TargetEditorWindow.xaml.cs`

**Interfaces:**
- Consumes: `BackupTarget`, `TargetType`, `FtpEncryption` (Task 2); `CredentialProtector.Protect` (Task 3); `TargetFactory.Create` (Task 9).
- Produces: `TargetEditorWindow(BackupTarget? existing)` — a modal dialog; on `ShowDialog() == true`, `Result` holds the built `BackupTarget` (with `Id` preserved from `existing` when editing, `0` when new — matching `JobRepository.Update`'s new-vs-existing diffing from Task 7). Consumed by Task 17 (`JobEditorWindow`).

This window has no `App.JobRepository`/`App.RunRepository` dependency at all — it only builds a `BackupTarget` value from form fields and hands it back via `Result`; `JobEditorWindow` (Task 17) is the one that actually persists it, as part of saving the whole job.

- [ ] **Step 1: Write `src/SparkVault.App/TargetEditorWindow.xaml`**

```xml
<Window x:Class="SparkVault.App.TargetEditorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Ziel bearbeiten" Height="560" Width="420" WindowStartupLocation="CenterOwner">
    <StackPanel Margin="12">
        <TextBlock Text="Zieltyp" />
        <ComboBox x:Name="TypeCombo" Margin="0,0,0,8" SelectionChanged="TypeCombo_SelectionChanged">
            <ComboBoxItem Content="Lokal / Netzlaufwerk" Tag="Local" />
            <ComboBoxItem Content="FTP" Tag="Ftp" />
            <ComboBoxItem Content="SFTP" Tag="Sftp" />
        </ComboBox>

        <StackPanel x:Name="LocalPanel">
            <TextBlock Text="Zielpfad (lokal/UNC)" />
            <DockPanel Margin="0,0,0,8">
                <Button Content="..." Width="30" DockPanel.Dock="Right" Click="BrowseDestination_Click" />
                <TextBox x:Name="DestinationPathBox" />
            </DockPanel>
        </StackPanel>

        <StackPanel x:Name="RemotePanel">
            <TextBlock Text="Host" />
            <TextBox x:Name="HostBox" Margin="0,0,0,8" />

            <TextBlock Text="Port (leer = Standard)" />
            <TextBox x:Name="PortBox" Margin="0,0,0,8" />

            <TextBlock Text="Benutzername" />
            <TextBox x:Name="UsernameBox" Margin="0,0,0,8" />

            <TextBlock Text="Passwort (bei Bearbeiten leer lassen = unverändert)" />
            <PasswordBox x:Name="PasswordBox" Margin="0,0,0,8" />

            <TextBlock Text="Remote-Pfad" />
            <TextBox x:Name="RemotePathBox" Margin="0,0,0,8" />
        </StackPanel>

        <StackPanel x:Name="FtpOnlyPanel">
            <TextBlock Text="Verschlüsselung" />
            <ComboBox x:Name="EncryptionModeCombo" Margin="0,0,0,8">
                <ComboBoxItem Content="Keine" Tag="None" />
                <ComboBoxItem Content="Explizit (FTPS)" Tag="Explicit" />
                <ComboBoxItem Content="Implizit (FTPS)" Tag="Implicit" />
            </ComboBox>
        </StackPanel>

        <StackPanel x:Name="SftpOnlyPanel">
            <TextBlock Text="Privater Schlüssel (optional)" />
            <DockPanel Margin="0,0,0,8">
                <Button Content="..." Width="30" DockPanel.Dock="Right" Click="BrowseKeyFile_Click" />
                <TextBox x:Name="PrivateKeyPathBox" />
            </DockPanel>

            <TextBlock Text="Schlüssel-Passphrase (optional, bei Bearbeiten leer lassen = unverändert)" />
            <PasswordBox x:Name="KeyPassphraseBox" Margin="0,0,0,8" />
        </StackPanel>

        <Button x:Name="TestConnectionButton" Content="Verbindung testen" Padding="8,4"
                HorizontalAlignment="Left" Margin="0,4,0,8" Click="TestConnection_Click" />

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Abbrechen" Padding="12,4" Click="Cancel_Click" />
            <Button Content="Speichern" Padding="12,4" Margin="8,0,0,0" Click="Save_Click" />
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Write `src/SparkVault.App/TargetEditorWindow.xaml.cs`**

```csharp
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using SparkVault.Core;

namespace SparkVault.App;

public partial class TargetEditorWindow : Window
{
    private readonly int? _existingId;
    private readonly string? _existingEncryptedPassword;
    private readonly string? _existingEncryptedKeyPassphrase;

    public BackupTarget? Result { get; private set; }

    public TargetEditorWindow(BackupTarget? existing)
    {
        InitializeComponent();

        if (existing is not null)
        {
            _existingId = existing.Id;
            _existingEncryptedPassword = existing.EncryptedPassword;
            _existingEncryptedKeyPassphrase = existing.EncryptedKeyPassphrase;
            LoadTarget(existing);
        }
        else
        {
            TypeCombo.SelectedIndex = 0;
        }
    }

    private void LoadTarget(BackupTarget target)
    {
        TypeCombo.SelectedIndex = target.Type switch
        {
            TargetType.Local => 0,
            TargetType.Ftp => 1,
            TargetType.Sftp => 2,
            _ => 0,
        };
        DestinationPathBox.Text = target.DestinationPath ?? "";
        HostBox.Text = target.Host ?? "";
        PortBox.Text = target.Port?.ToString() ?? "";
        UsernameBox.Text = target.Username ?? "";
        RemotePathBox.Text = target.RemotePath ?? "";
        EncryptionModeCombo.SelectedIndex = target.EncryptionMode switch
        {
            FtpEncryption.Explicit => 1,
            FtpEncryption.Implicit => 2,
            _ => 0,
        };
        PrivateKeyPathBox.Text = target.PrivateKeyPath ?? "";
        // PasswordBox/KeyPassphraseBox stay blank on load by design — an empty field on save
        // means "keep the existing encrypted credential" (see BuildTargetFromForm), so we
        // never need to (and never could, without the DPAPI user context) show the plaintext.
    }

    private void TypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var isLocal = TypeCombo.SelectedIndex == 0;
        var isFtp = TypeCombo.SelectedIndex == 1;
        var isSftp = TypeCombo.SelectedIndex == 2;

        LocalPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        RemotePanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
        FtpOnlyPanel.Visibility = isFtp ? Visibility.Visible : Visibility.Collapsed;
        SftpOnlyPanel.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        TestConnectionButton.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DestinationPathBox.Text = dialog.SelectedPath;
    }

    private void BrowseKeyFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Privaten Schlüssel wählen" };
        if (dialog.ShowDialog(this) == true)
            PrivateKeyPathBox.Text = dialog.FileName;
    }

    private BackupTarget? BuildTargetFromForm(bool validate)
    {
        var type = TypeCombo.SelectedIndex switch
        {
            1 => TargetType.Ftp,
            2 => TargetType.Sftp,
            _ => TargetType.Local,
        };

        var target = new BackupTarget { Id = _existingId ?? 0, Type = type };

        if (type == TargetType.Local)
        {
            if (validate && string.IsNullOrWhiteSpace(DestinationPathBox.Text))
            {
                MessageBox.Show(this, "Bitte einen Zielpfad angeben.", "SparkVault", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            target.DestinationPath = DestinationPathBox.Text.Trim();
            return target;
        }

        if (validate && string.IsNullOrWhiteSpace(HostBox.Text))
        {
            MessageBox.Show(this, "Bitte einen Host angeben.", "SparkVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (validate && string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            MessageBox.Show(this, "Bitte einen Benutzernamen angeben.", "SparkVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (validate && string.IsNullOrWhiteSpace(RemotePathBox.Text))
        {
            MessageBox.Show(this, "Bitte einen Remote-Pfad angeben.", "SparkVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(PortBox.Text))
        {
            if (!int.TryParse(PortBox.Text, out var parsedPort) || parsedPort <= 0)
            {
                if (validate)
                {
                    MessageBox.Show(this, "Bitte einen gültigen Port angeben (oder leer lassen).", "SparkVault",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }
            }
            else
            {
                port = parsedPort;
            }
        }

        target.Host = HostBox.Text.Trim();
        target.Port = port;
        target.Username = UsernameBox.Text.Trim();
        target.RemotePath = RemotePathBox.Text.Trim();

        target.EncryptedPassword = PasswordBox.Password.Length > 0
            ? CredentialProtector.Protect(PasswordBox.Password)
            : _existingEncryptedPassword;

        if (type == TargetType.Ftp)
        {
            target.EncryptionMode = EncryptionModeCombo.SelectedIndex switch
            {
                1 => FtpEncryption.Explicit,
                2 => FtpEncryption.Implicit,
                _ => FtpEncryption.None,
            };
        }
        else
        {
            target.PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyPathBox.Text) ? null : PrivateKeyPathBox.Text.Trim();
            target.EncryptedKeyPassphrase = KeyPassphraseBox.Password.Length > 0
                ? CredentialProtector.Protect(KeyPassphraseBox.Password)
                : _existingEncryptedKeyPassphrase;

            if (validate && string.IsNullOrEmpty(target.EncryptedPassword) && string.IsNullOrEmpty(target.PrivateKeyPath))
            {
                MessageBox.Show(this, "Bitte Passwort und/oder privaten Schlüssel angeben.", "SparkVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        return target;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var target = BuildTargetFromForm(validate: true);
        if (target is null) return;

        TestConnectionButton.IsEnabled = false;
        try
        {
            await using var probe = TargetFactory.Create(target);
            var ok = await probe.TestConnectionAsync(CancellationToken.None);
            MessageBox.Show(this, ok ? "Verbindung erfolgreich." : "Verbindung fehlgeschlagen.", "SparkVault",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Verbindung fehlgeschlagen: {ex.Message}", "SparkVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var target = BuildTargetFromForm(validate: true);
        if (target is null) return;

        Result = target;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `TargetEditorWindow` itself compiles cleanly. `JobEditorWindow.xaml.cs`/`.xaml` and `LogWindow.xaml.cs` still fail (Task 17-18 fix them) — confirm no error is specific to `TargetEditorWindow`.

- [ ] **Step 4: Commit**

```bash
git add src/SparkVault.App/TargetEditorWindow.xaml src/SparkVault.App/TargetEditorWindow.xaml.cs
git commit -m "Add TargetEditorWindow (per-target Local/FTP/SFTP form)"
```

---

### Task 17: JobEditorWindow — target list management

**Files:**
- Modify: `src/SparkVault.App/JobEditorWindow.xaml`
- Modify: `src/SparkVault.App/JobEditorWindow.xaml.cs`

**Interfaces:**
- Consumes: `TargetEditorWindow` (Task 16); `App.JobRepository` (MVP, now target-aware per Task 7); `BackupJob.Targets`, `BackupTarget`, `TargetType` (Task 2).
- Produces: no new public surface — `JobEditorWindow(int? jobId)` keeps its exact MVP constructor and `DialogResult` contract; `MainWindow` (Task 15, already updated) doesn't need any further change to keep calling it.

Read both current files first. The "Zielpfad" single-field section is replaced by a target list (add/edit/remove, each item opening `TargetEditorWindow`); everything else (Name, Quellpfad, Ausschlussmuster, Zeitplan, validation for those fields) stays as-is.

- [ ] **Step 1: Modify `src/SparkVault.App/JobEditorWindow.xaml`**

Replace the entire "Zielpfad (lokal/UNC)" `<TextBlock>`+`<DockPanel>` block with:
```xml
        <TextBlock Text="Ziele" />
        <ListBox x:Name="TargetsListBox" Height="80" Margin="0,0,0,4" DisplayMemberPath="Description" />
        <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
            <Button Content="Hinzufügen" Padding="8,2" Click="AddTarget_Click" />
            <Button Content="Bearbeiten" Padding="8,2" Margin="8,0,0,0" Click="EditTarget_Click" />
            <Button Content="Entfernen" Padding="8,2" Margin="8,0,0,0" Click="RemoveTarget_Click" />
        </StackPanel>
```
(Same position in the form, between "Quellpfad" and "Ausschlussmuster".) Also bump `Height="480"` to `Height="560"` on the `<Window>` element — the target list needs a bit more room than the single destination field did.

- [ ] **Step 2: Modify `src/SparkVault.App/JobEditorWindow.xaml.cs`**

Replace the file's contents entirely with:
```csharp
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using SparkVault.Core;

namespace SparkVault.App;

public partial class JobEditorWindow : Window
{
    private sealed class TargetListItem
    {
        public required BackupTarget Target { get; init; }

        public string Description => Target.Type switch
        {
            TargetType.Local => $"Lokal: {Target.DestinationPath}",
            TargetType.Ftp => $"FTP: {Target.Host}",
            TargetType.Sftp => $"SFTP: {Target.Host}",
            _ => Target.Type.ToString(),
        };
    }

    private readonly int? _jobId;
    private readonly ObservableCollection<TargetListItem> _targets = new();

    public JobEditorWindow(int? jobId)
    {
        InitializeComponent();
        TargetsListBox.ItemsSource = _targets;
        _jobId = jobId;

        if (_jobId is { } id)
        {
            var job = App.JobRepository.GetById(id);
            if (job is not null)
                LoadJob(job);
        }
        else
        {
            ScheduleTypeCombo.SelectedIndex = 0;
        }
    }

    private void LoadJob(BackupJob job)
    {
        NameBox.Text = job.Name;
        SourcePathBox.Text = job.SourcePath;
        ExcludePatternsBox.Text = string.Join(Environment.NewLine, job.ExcludePatterns);
        foreach (var target in job.Targets)
            _targets.Add(new TargetListItem { Target = target });
        ScheduleTypeCombo.SelectedIndex = job.ScheduleType switch
        {
            ScheduleType.None => 0,
            ScheduleType.Interval => 1,
            ScheduleType.DailyAt => 2,
            _ => 0,
        };
        IntervalHoursBox.Text = job.IntervalHours?.ToString() ?? "";
        DailyAtTimeBox.Text = job.DailyAtTime?.ToString("HH:mm") ?? "";
    }

    private void ScheduleTypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var isInterval = ScheduleTypeCombo.SelectedIndex == 1;
        var isDailyAt = ScheduleTypeCombo.SelectedIndex == 2;
        IntervalLabel.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        IntervalHoursBox.Visibility = isInterval ? Visibility.Visible : Visibility.Collapsed;
        DailyAtLabel.Visibility = isDailyAt ? Visibility.Visible : Visibility.Collapsed;
        DailyAtTimeBox.Visibility = isDailyAt ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SourcePathBox.Text = dialog.SelectedPath;
    }

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        var editor = new TargetEditorWindow(existing: null) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
            _targets.Add(new TargetListItem { Target = editor.Result });
    }

    private void EditTarget_Click(object sender, RoutedEventArgs e)
    {
        if (TargetsListBox.SelectedItem is not TargetListItem selected) return;

        var editor = new TargetEditorWindow(existing: selected.Target) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            var index = _targets.IndexOf(selected);
            _targets[index] = new TargetListItem { Target = editor.Result };
        }
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (TargetsListBox.SelectedItem is TargetListItem selected)
            _targets.Remove(selected);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(SourcePathBox.Text))
        {
            MessageBox.Show(this, "Name und Quellpfad sind Pflichtfelder.", "SparkVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_targets.Count == 0)
        {
            MessageBox.Show(this, "Bitte mindestens ein Ziel hinzufügen.", "SparkVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // A local target inside the source makes every run re-scan its own output.
        var fullSource = Path.GetFullPath(SourcePathBox.Text.Trim());
        var sourcePrefix = fullSource.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var item in _targets)
        {
            if (item.Target.Type != TargetType.Local || item.Target.DestinationPath is null)
                continue;

            var fullDest = Path.GetFullPath(item.Target.DestinationPath);
            if (fullDest.Equals(fullSource, StringComparison.OrdinalIgnoreCase) ||
                fullDest.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Ein lokales Ziel darf nicht innerhalb des Quellpfads liegen.", "SparkVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var scheduleType = ScheduleTypeCombo.SelectedIndex switch
        {
            1 => ScheduleType.Interval,
            2 => ScheduleType.DailyAt,
            _ => ScheduleType.None,
        };

        int? intervalHours = null;
        if (scheduleType == ScheduleType.Interval)
        {
            if (!int.TryParse(IntervalHoursBox.Text, out var hours) || hours <= 0)
            {
                MessageBox.Show(this, "Bitte eine gültige Stundenzahl angeben.", "SparkVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            intervalHours = hours;
        }

        TimeOnly? dailyAtTime = null;
        if (scheduleType == ScheduleType.DailyAt)
        {
            if (!TimeOnly.TryParse(DailyAtTimeBox.Text, out var time))
            {
                MessageBox.Show(this, "Bitte eine gültige Uhrzeit im Format HH:mm angeben.", "SparkVault",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            dailyAtTime = time;
        }

        var job = new BackupJob
        {
            Id = _jobId ?? 0,
            Name = NameBox.Text.Trim(),
            SourcePath = SourcePathBox.Text.Trim(),
            ExcludePatterns = ExcludePatternsBox.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            ScheduleType = scheduleType,
            IntervalHours = intervalHours,
            DailyAtTime = dailyAtTime,
            Targets = _targets.Select(t => t.Target).ToList(),
        };

        if (_jobId is null)
            App.JobRepository.Add(job);
        else
            App.JobRepository.Update(job);

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `JobEditorWindow` compiles cleanly. `LogWindow.xaml.cs` still fails (Task 18 fixes it) — confirm no error is specific to `JobEditorWindow`.

- [ ] **Step 4: Manual verification**

Run: `dotnet run --project src/SparkVault.App` (build will still fail overall until Task 18 lands — if `dotnet run` refuses to start because of that, skip this step here and fold it into Task 18/19's manual verification instead; note which happened in your report). If it does run: open the main window, "Neuer Job", add a Local target and an FTP target via "Hinzufügen", confirm both show up in the list with sensible descriptions, edit one, confirm the change sticks, remove one, confirm it's gone, save, confirm the job persists with its targets after reopening.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.App/JobEditorWindow.xaml src/SparkVault.App/JobEditorWindow.xaml.cs
git commit -m "JobEditorWindow: manage a list of targets instead of one destination path"
```

---

### Task 18: LogWindow — show which target each run row is for

**Files:**
- Modify: `src/SparkVault.App/LogWindow.xaml`
- Modify: `src/SparkVault.App/LogWindow.xaml.cs`

**Interfaces:**
- Consumes: `App.JobRepository.GetById` (Task 7, now returns `Targets`), `App.RunRepository.GetByJobId` (Task 8, rows now carry `TargetId`).
- Produces: no new public surface — `LogWindow(int jobId, string jobName)` keeps its exact MVP constructor.

Read both current files first. `RunRepository.GetByJobId` already orders by `StartedAt DESC`, so rows from the same run group (same `RunGroupId`) appear consecutively since targets are processed back-to-back within one `BackupRunner.RunAsync` call — this satisfies the spec's "ein Klick auf Job = alle Ziele dieses Laufs zusammen" without needing a grouped/collapsible UI. Add a "Ziel" column showing which target each row belongs to; that's the only change.

- [ ] **Step 1: Modify `src/SparkVault.App/LogWindow.xaml`**

Add a "Ziel" column between "Ende" and "Status":
```xml
            <DataGridTextColumn Header="Ziel" Binding="{Binding Target}" Width="140" />
```

- [ ] **Step 2: Replace `src/SparkVault.App/LogWindow.xaml.cs`**

```csharp
using System.Windows;
using SparkVault.Core;

namespace SparkVault.App;

public partial class LogWindow : Window
{
    private sealed record RunRow(DateTime StartedAt, DateTime? EndedAt, string Target, RunStatus Status, int FileCount, long TotalBytes, string? ErrorMessage);

    public LogWindow(int jobId, string jobName)
    {
        InitializeComponent();
        Title = $"Log – {jobName}";

        var targetsById = (App.JobRepository.GetById(jobId)?.Targets ?? new List<BackupTarget>())
            .ToDictionary(t => t.Id, DescribeTarget);

        RunsGrid.ItemsSource = App.RunRepository.GetByJobId(jobId)
            .Select(r => new RunRow(
                r.StartedAt.ToLocalTime(),
                r.EndedAt?.ToLocalTime(),
                targetsById.TryGetValue(r.TargetId, out var desc) ? desc : $"Ziel #{r.TargetId}",
                r.Status, r.FileCount, r.TotalBytes, r.ErrorMessage))
            .ToList();
    }

    private static string DescribeTarget(BackupTarget target) => target.Type switch
    {
        TargetType.Local => $"Lokal: {target.DestinationPath}",
        TargetType.Ftp => $"FTP: {target.Host}",
        TargetType.Sftp => $"SFTP: {target.Host}",
        _ => target.Type.ToString(),
    };
}
```

Note: `targetsById.TryGetValue(...)` falling back to `"Ziel #{r.TargetId}"` handles the case where a target was later removed from the job (edited out via `JobEditorWindow`) — its historical run rows still reference the old `TargetId`, same accepted orphaning behavior the MVP's final review already documented for `Runs`/`Jobs`.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: the whole solution now builds with **zero errors** for the first time in this plan.

- [ ] **Step 4: Commit**

```bash
git add src/SparkVault.App/LogWindow.xaml src/SparkVault.App/LogWindow.xaml.cs
git commit -m "LogWindow: show which target each run row is for"
```

---

### Task 19: Full-solution verification

**Files:** none (verification only)

This is the real, honest end-to-end proof that FTP and SFTP targets actually work together with everything built across Tasks 1-18 — not a repeat of static reasoning. Be concrete about what you actually observed at each step, including anything that didn't work as expected.

- [ ] **Step 1: Delete the stale MVP-era dev database**

The schema changed incompatibly (Task 5 dropped `Jobs.DestinationPath`, added `Targets`, added required columns to `Runs`). Delete `%AppData%\SparkVault\sparkvault.db` (and the Serilog log files in that same folder, for a clean run) before starting the app for the first time under this plan's code. Confirm the file is actually gone before proceeding (`Test-Path` or `ls`), not just that you ran the delete command.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass — the MVP's original ~27 tests (after its own final-review fixes) plus everything added in Tasks 3, 6-13 (`CredentialProtector`, `BackupTargetRepository`, `TargetFactory`, `FtpTarget`, `SftpTarget`, updated `JobRepository`/`RunRepository`/`BackupRunner`/`BackgroundScheduler`). Report the actual count. If `FtpTargetTests`/`SftpTargetTests` ran against real Docker containers (confirm via `docker ps` that `sparkvault-test-ftp`/`sparkvault-test-sftp` are up), say so explicitly and confirm the suite duration reflects real network round-trips, not instant no-ops — same standard the MVP's Task 14 held itself to when a prior task's "manual testing" turned out to be unbacked claims. If Docker isn't reachable, run `docker compose -f docker/docker-compose.test.yml up -d`, wait for the containers to report healthy, and re-run the suite so the real integration tests actually execute at least once during this task.

- [ ] **Step 3: Full manual smoke test — genuinely interact with the running app**

Run: `dotnet run --project src/SparkVault.App`. Use real UI automation or direct interaction (not code-reading) for every step below, the same bar Task 14 of the MVP plan held itself to:

1. Tray icon appears, no window on launch.
2. Create a job ("FTP/SFTP Test") with a small real source folder (a couple of files), and add **two targets** to it via "Hinzufügen": one `Local` target (a real local folder) and one `Ftp` target pointed at `127.0.0.1:2121`, user `testuser`, password `testpass`, encryption `Keine`, remote path `/verify-run`. Use "Verbindung testen" on the FTP target before saving and confirm it reports success.
3. Click "Jetzt sichern". Confirm on the **filesystem** that the local target received the files, and confirm via an FTP client (or by adding a third temporary target pointed at the same path and checking `ListExistingAsync`-backed UI, or by using `docker exec` into the FTP container to `ls` the uploaded path) that the FTP target actually received the same files remotely — this is the one thing no automated test in this plan exercises through the real UI end-to-end.
4. Open "Log anzeigen" for the job — confirm **two** rows appear (one per target), both `Success`, with a "Ziel" column correctly distinguishing "Lokal: ..." from "FTP: 127.0.0.1".
5. Edit the job: remove the FTP target, add an `Sftp` target instead (`127.0.0.1:2222`, user `testuser`, password `testpass`, remote path `/upload/verify-run`). Save. Run again. Confirm the log now shows a new run group with Local + SFTP rows, and confirm via `docker exec` into the SFTP container (or equivalent) that the file actually landed under `/home/testuser/upload/verify-run`.
6. Kill-test: repeat the MVP's kill-test but for a remote target — start a run with a large-enough file, kill the app mid-transfer to the FTP or SFTP target, and confirm (via the Docker container directly) that only a `*.sparkvault-tmp`-suffixed remote file exists, never the final name. This is the temp-name-then-commit guarantee's first live proof over a network protocol, not just local disk.
7. Confirm `%AppData%\SparkVault\log*.txt` has structured entries naming both the job and which target succeeded/failed, matching `BackupRunner`'s per-target log calls.
8. Quit via tray "Beenden", confirm clean process exit (no orphan), same as the MVP's Task 14.

If any step can't be performed exactly as described (e.g. no convenient way to inspect the Docker container's filesystem from this environment), say precisely what you tried and what you could confirm a different way — don't silently skip it or substitute a weaker claim for what was actually asked.

- [ ] **Step 4: Report findings**

Write a report covering: exact test counts and whether Docker-backed tests genuinely ran, each manual step's concrete observation, any bugs found (do not fix them yourself — report them precisely so the controller can decide), and cleanup performed (test jobs/fixtures removed, Docker containers left running or stopped — your call, note which).

- [ ] **Step 5: Commit** (only if Step 3 uncovered fixes)

```bash
git add -A
git commit -m "Fix issues found during FTP/SFTP full-solution smoke test"
```

