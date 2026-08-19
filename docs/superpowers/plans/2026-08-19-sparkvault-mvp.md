# SparkVault MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first runnable version of SparkVault: a WPF tray app that lets a user define backup jobs (source → local/UNC destination), run them manually or on a schedule, and see progress/history — with a temp-name-then-commit write pattern so an aborted run never leaves a half-written file looking valid.

**Architecture:** `SparkVault.Core` (class library, no UI dependency) holds domain models, the `IBackupTarget` interface + its only MVP implementation `LocalTarget`, the file scanner/exclusion matcher, SQLite persistence, the due-time calculator, the orchestrating `BackupRunner`, and a `PeriodicTimer`-based `BackgroundScheduler`. `SparkVault.App` (WPF) is a thin shell: tray icon, main window (job list), job editor, log view — all wired to `SparkVault.Core` from a composition root in `App.xaml.cs`. `SparkVault.Core.Tests` is an xUnit project covering everything with real logic (scanner, exclusion matching, `LocalTarget`, scheduling math, `BackupRunner`).

**Tech Stack:** .NET 8, WPF (`SparkVault.App`, `net8.0-windows` + `UseWindowsForms` for the tray `NotifyIcon`), `Microsoft.Data.Sqlite`, `Serilog` + `Serilog.Sinks.File`, xUnit. No Quartz.NET (stdlib `PeriodicTimer` instead), no globbing library (stdlib `System.IO.Enumeration.FileSystemName.MatchesSimpleExpression` instead).

**Spec:** [docs/superpowers/specs/2026-08-19-sparkvault-mvp-design.md](../specs/2026-08-19-sparkvault-mvp-design.md)

## Global Constraints

- .NET 8, C#, WPF for the UI project (from spec §2).
- No Windows Service — tray app with autostart, no elevated rights (spec §2).
- `IBackupTarget` interface must be defined with its full future signature up front (`TestConnectionAsync`, `UploadAsync`, `ListExistingAsync`, `DeleteAsync`) so `FtpTarget`/`S3Target` can be added later without touching `BackupRunner` or `BackgroundScheduler` (spec §4).
- MVP implements only `Local` as a target type — no FTP/SFTP/S3, no credentials storage (spec §3).
- Only full-backup copies — no incremental/differential diffing (spec §3).
- Scheduler: in-process `PeriodicTimer`, no Quartz.NET, no Windows Task Scheduler integration; missed runs while the app isn't running are simply skipped (spec §2 Nachtrag, §3).
- Every file write to the destination must go through a temp-name-then-verify-then-rename sequence; on any failure/cancellation the temp file must be deleted and the final file must never exist half-written (spec §7).
- Config + run history persisted in SQLite under `%AppData%/SparkVault/` (spec §5).
- Structured logging via Serilog to a text log file, in addition to the SQLite run history (spec §7).
- App title: **SparkVault**.

---

## File Structure

```
SparkVault.sln
src/
  SparkVault.Core/
    SparkVault.Core.csproj
    Models.cs                  (BackupJob, ScheduleType, BackupRun, RunStatus, BackupFile)
    ExclusionMatcher.cs
    FileScanner.cs
    IBackupTarget.cs            (interface + TransferProgress, RemoteFileInfo)
    LocalTarget.cs
    SparkVaultDatabase.cs       (schema creation)
    JobRepository.cs
    RunRepository.cs
    ScheduleCalculator.cs
    BackupRunner.cs
    BackgroundScheduler.cs
  SparkVault.App/
    SparkVault.App.csproj
    App.xaml / App.xaml.cs      (composition root, tray icon)
    MainWindow.xaml / .cs       (job list)
    JobEditorWindow.xaml / .cs  (add/edit job)
    LogWindow.xaml / .cs        (run history for one job)
tests/
  SparkVault.Core.Tests/
    SparkVault.Core.Tests.csproj
    ExclusionMatcherTests.cs
    FileScannerTests.cs
    LocalTargetTests.cs
    JobRepositoryTests.cs
    RunRepositoryTests.cs
    ScheduleCalculatorTests.cs
    BackupRunnerTests.cs
    BackgroundSchedulerTests.cs
```

---

### Task 1: Solution and project scaffolding

**Files:**
- Create: `SparkVault.sln`
- Create: `src/SparkVault.Core/SparkVault.Core.csproj`
- Create: `src/SparkVault.App/SparkVault.App.csproj`
- Create: `src/SparkVault.App/App.xaml`, `src/SparkVault.App/App.xaml.cs`
- Create: `src/SparkVault.App/MainWindow.xaml`, `src/SparkVault.App/MainWindow.xaml.cs`
- Create: `tests/SparkVault.Core.Tests/SparkVault.Core.Tests.csproj`
- Create: `.gitignore`

**Interfaces:**
- Produces: a solution that builds, with `SparkVault.App` referencing `SparkVault.Core`, and `SparkVault.Core.Tests` referencing `SparkVault.Core`.

- [ ] **Step 1: Create `.gitignore`**

```
bin/
obj/
*.user
```

- [ ] **Step 2: Create `src/SparkVault.Core/SparkVault.Core.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.8" />
    <PackageReference Include="Serilog" Version="4.0.1" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create `src/SparkVault.App/SparkVault.App.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <ApplicationTitle>SparkVault</ApplicationTitle>
    <AssemblyName>SparkVault</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SparkVault.Core\SparkVault.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Create minimal `App.xaml` / `App.xaml.cs`**

`src/SparkVault.App/App.xaml`:
```xml
<Application x:Class="SparkVault.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
</Application>
```

`src/SparkVault.App/App.xaml.cs`:
```csharp
using System.Windows;
using Application = System.Windows.Application;

namespace SparkVault.App;

public partial class App : Application
{
}
```

Note: the project has both `UseWPF` and `UseWindowsForms` enabled (the latter is needed for the tray icon in Task 10), so `Application` is ambiguous between `System.Windows.Application` and `System.Windows.Forms.Application` without this alias.

- [ ] **Step 5: Create minimal `MainWindow.xaml` / `MainWindow.xaml.cs`**

`src/SparkVault.App/MainWindow.xaml`:
```xml
<Window x:Class="SparkVault.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="SparkVault" Height="450" Width="700">
    <Grid>
        <TextBlock Text="SparkVault" FontSize="20" Margin="16" />
    </Grid>
</Window>
```

`src/SparkVault.App/MainWindow.xaml.cs`:
```csharp
using System.Windows;

namespace SparkVault.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 6: Create `tests/SparkVault.Core.Tests/SparkVault.Core.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SparkVault.Core\SparkVault.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 7: Create `SparkVault.sln`**

Run:
```bash
dotnet new sln -n SparkVault
dotnet sln add src/SparkVault.Core/SparkVault.Core.csproj
dotnet sln add src/SparkVault.App/SparkVault.App.csproj
dotnet sln add tests/SparkVault.Core.Tests/SparkVault.Core.Tests.csproj
```

- [ ] **Step 8: Build the solution**

Run: `dotnet build`
Expected: Build succeeds, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "Scaffold SparkVault solution (Core, App, Core.Tests)"
```

---

### Task 2: Domain models

**Files:**
- Create: `src/SparkVault.Core/Models.cs`

**Interfaces:**
- Produces: `BackupJob`, `ScheduleType`, `BackupRun`, `RunStatus`, `BackupFile` — the shared vocabulary every later task uses. Exact shape below; no other task may redefine these.

Plain data holders — no branching logic, so no dedicated test (ponytail: trivial one-liners need no test). Correctness is exercised indirectly by every later task that persists/reads them.

- [ ] **Step 1: Write `src/SparkVault.Core/Models.cs`**

```csharp
namespace SparkVault.Core;

public enum ScheduleType { None, Interval, DailyAt }

public sealed class BackupJob
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public List<string> ExcludePatterns { get; set; } = new();
    public ScheduleType ScheduleType { get; set; } = ScheduleType.None;
    public int? IntervalHours { get; set; }
    public TimeOnly? DailyAtTime { get; set; }
}

public enum RunStatus { Success, Failed, Cancelled }

public sealed class BackupRun
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public RunStatus Status { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed record BackupFile(string FullPath, string RelativePath, long Size);
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/SparkVault.Core/Models.cs
git commit -m "Add SparkVault.Core domain models"
```

---

### Task 3: Exclusion matcher

**Files:**
- Create: `src/SparkVault.Core/ExclusionMatcher.cs`
- Test: `tests/SparkVault.Core.Tests/ExclusionMatcherTests.cs`

**Interfaces:**
- Consumes: nothing beyond stdlib.
- Produces: `ExclusionMatcher.IsExcluded(string relativePath, IEnumerable<string> patterns) -> bool`, used by `FileScanner` (Task 4).

- [ ] **Step 1: Write the failing tests**

```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class ExclusionMatcherTests
{
    [Fact]
    public void MatchesSimpleWildcard()
    {
        Assert.True(ExclusionMatcher.IsExcluded("notes.tmp", new[] { "*.tmp" }));
    }

    [Fact]
    public void DoesNotMatchUnrelatedPattern()
    {
        Assert.False(ExclusionMatcher.IsExcluded("notes.txt", new[] { "*.tmp" }));
    }

    [Fact]
    public void MatchesInsideSubfolder()
    {
        Assert.True(ExclusionMatcher.IsExcluded(@"cache\file.bin", new[] { @"cache\*" }));
    }

    [Fact]
    public void NoPatternsMeansNothingExcluded()
    {
        Assert.False(ExclusionMatcher.IsExcluded("anything.txt", Array.Empty<string>()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter ExclusionMatcherTests`
Expected: FAIL to compile — `ExclusionMatcher` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
using System.IO.Enumeration;

namespace SparkVault.Core;

public static class ExclusionMatcher
{
    public static bool IsExcluded(string relativePath, IEnumerable<string> patterns)
    {
        var normalizedPath = relativePath.Replace('\\', '/');

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            var normalizedPattern = pattern.Replace('\\', '/');
            if (FileSystemName.MatchesSimpleExpression(normalizedPattern, normalizedPath))
                return true;
        }

        return false;
    }
}
```

**Why the normalization:** `FileSystemName.MatchesSimpleExpression` treats a backslash in the
*pattern* as an escape character, not a literal separator — the pattern `cache\*` matches the
literal text `cache*`, not `cache` + separator + wildcard. Forward slashes have no special
meaning to it. Normalizing both sides to `/` before matching sidesteps the escape behavior
while still accepting the natural Windows-style `cache\*` patterns a user types in the UI.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter ExclusionMatcherTests`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/ExclusionMatcher.cs tests/SparkVault.Core.Tests/ExclusionMatcherTests.cs
git commit -m "Add exclusion pattern matcher"
```

---

### Task 4: File scanner

**Files:**
- Create: `src/SparkVault.Core/FileScanner.cs`
- Test: `tests/SparkVault.Core.Tests/FileScannerTests.cs`

**Interfaces:**
- Consumes: `ExclusionMatcher.IsExcluded` (Task 3).
- Produces: `FileScanner.Scan(string sourcePath, IEnumerable<string> excludePatterns) -> IReadOnlyList<BackupFile>`, used by `BackupRunner` (Task 8).

- [ ] **Step 1: Write the failing test**

```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class FileScannerTests
{
    [Fact]
    public void ScansFilesAndAppliesExclusions()
    {
        var root = Directory.CreateTempSubdirectory("sparkvault-scan-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "keep.txt"), "hello");
            File.WriteAllText(Path.Combine(root.FullName, "skip.tmp"), "temp");
            var subDir = Directory.CreateDirectory(Path.Combine(root.FullName, "sub"));
            File.WriteAllText(Path.Combine(subDir.FullName, "nested.txt"), "world");

            var result = FileScanner.Scan(root.FullName, new[] { "*.tmp" });

            Assert.Equal(2, result.Count);
            Assert.Contains(result, f => f.RelativePath == "keep.txt" && f.Size == 5);
            Assert.Contains(result, f => f.RelativePath == Path.Combine("sub", "nested.txt"));
            Assert.DoesNotContain(result, f => f.RelativePath == "skip.tmp");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SparkVault.Core.Tests --filter FileScannerTests`
Expected: FAIL to compile — `FileScanner` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
namespace SparkVault.Core;

public static class FileScanner
{
    public static IReadOnlyList<BackupFile> Scan(string sourcePath, IEnumerable<string> excludePatterns)
    {
        var patterns = excludePatterns.ToList();
        var result = new List<BackupFile>();

        foreach (var fullPath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, fullPath);
            if (ExclusionMatcher.IsExcluded(relativePath, patterns))
                continue;

            result.Add(new BackupFile(fullPath, relativePath, new FileInfo(fullPath).Length));
        }

        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SparkVault.Core.Tests --filter FileScannerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/FileScanner.cs tests/SparkVault.Core.Tests/FileScannerTests.cs
git commit -m "Add file scanner"
```

---

### Task 5: IBackupTarget interface and LocalTarget

**Files:**
- Create: `src/SparkVault.Core/IBackupTarget.cs`
- Create: `src/SparkVault.Core/LocalTarget.cs`
- Test: `tests/SparkVault.Core.Tests/LocalTargetTests.cs`

**Interfaces:**
- Consumes: `BackupFile` (Task 2).
- Produces:
  - `TransferProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal)`
  - `RemoteFileInfo(string Path, long Size)`
  - `interface IBackupTarget { Task<bool> TestConnectionAsync(CancellationToken ct); Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct); Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct); Task DeleteAsync(string remotePath, CancellationToken ct); }`
  - `class LocalTarget : IBackupTarget` with constructor `LocalTarget(string destinationRoot)`.
  - Both are used by `BackupRunner` (Task 8) and `BackgroundScheduler` (Task 9).

- [ ] **Step 1: Write the failing tests**

```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class LocalTargetTests
{
    [Fact]
    public async Task UploadAsync_CopiesFileAndVerifiesSize()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "hello world");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            var target = new LocalTarget(destDir.FullName);
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var finalPath = Path.Combine(destDir.FullName, "a.txt");
            Assert.True(File.Exists(finalPath));
            Assert.Equal("hello world", File.ReadAllText(finalPath));
            Assert.False(File.Exists(finalPath + ".sparkvault-tmp"));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_OnCancellation_LeavesNoFileBehind()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "hello world");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            var target = new LocalTarget(destDir.FullName);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => target.UploadAsync(file, progress: null, cts.Token));

            var finalPath = Path.Combine(destDir.FullName, "a.txt");
            Assert.False(File.Exists(finalPath));
            Assert.False(File.Exists(finalPath + ".sparkvault-tmp"));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TestConnectionAsync_CreatesDestinationIfMissing()
    {
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        destDir.Delete(recursive: true);
        try
        {
            var target = new LocalTarget(destDir.FullName);
            var ok = await target.TestConnectionAsync(CancellationToken.None);

            Assert.True(ok);
            Assert.True(Directory.Exists(destDir.FullName));
        }
        finally
        {
            if (Directory.Exists(destDir.FullName))
                destDir.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter LocalTargetTests`
Expected: FAIL to compile — `IBackupTarget`/`LocalTarget` do not exist yet.

- [ ] **Step 3: Write `IBackupTarget.cs`**

```csharp
namespace SparkVault.Core;

public sealed record TransferProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

public sealed record RemoteFileInfo(string Path, long Size);

public interface IBackupTarget
{
    Task<bool> TestConnectionAsync(CancellationToken ct);
    Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct);
    Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct);
    Task DeleteAsync(string remotePath, CancellationToken ct);
}
```

- [ ] **Step 4: Write `LocalTarget.cs`**

```csharp
namespace SparkVault.Core;

public sealed class LocalTarget : IBackupTarget
{
    private const string TempSuffix = ".sparkvault-tmp";
    private readonly string _destinationRoot;

    public LocalTarget(string destinationRoot)
    {
        _destinationRoot = destinationRoot;
    }

    public Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_destinationRoot);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task UploadAsync(BackupFile file, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var finalPath = Path.Combine(_destinationRoot, file.RelativePath);
        var tempPath = finalPath + TempSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        try
        {
            await using (var source = File.OpenRead(file.FullPath))
            await using (var dest = File.Create(tempPath))
            {
                await source.CopyToAsync(dest, ct);
            }

            var copiedLength = new FileInfo(tempPath).Length;
            if (copiedLength != file.Size)
            {
                throw new IOException(
                    $"Verification failed for {file.RelativePath}: expected {file.Size} bytes, got {copiedLength}.");
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    public Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_destinationRoot))
            return Task.FromResult(Enumerable.Empty<RemoteFileInfo>());

        var files = Directory.EnumerateFiles(_destinationRoot, "*", SearchOption.AllDirectories)
            .Select(f => new RemoteFileInfo(Path.GetRelativePath(_destinationRoot, f), new FileInfo(f).Length));

        return Task.FromResult(files);
    }

    public Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        var fullPath = Path.Combine(_destinationRoot, remotePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter LocalTargetTests`
Expected: PASS (3/3).

- [ ] **Step 6: Commit**

```bash
git add src/SparkVault.Core/IBackupTarget.cs src/SparkVault.Core/LocalTarget.cs tests/SparkVault.Core.Tests/LocalTargetTests.cs
git commit -m "Add IBackupTarget interface and LocalTarget implementation"
```

---

### Task 6: SQLite schema and repositories

**Files:**
- Create: `src/SparkVault.Core/SparkVaultDatabase.cs`
- Create: `src/SparkVault.Core/JobRepository.cs`
- Create: `src/SparkVault.Core/RunRepository.cs`
- Test: `tests/SparkVault.Core.Tests/JobRepositoryTests.cs`
- Test: `tests/SparkVault.Core.Tests/RunRepositoryTests.cs`

**Interfaces:**
- Consumes: `BackupJob`, `BackupRun`, `ScheduleType`, `RunStatus` (Task 2).
- Produces:
  - `SparkVaultDatabase.EnsureCreated(string connectionString)`
  - `class JobRepository(string connectionString)` with `int Add(BackupJob job)`, `void Update(BackupJob job)`, `void Delete(int id)`, `BackupJob? GetById(int id)`, `List<BackupJob> GetAll()`
  - `class RunRepository(string connectionString)` with `int Add(BackupRun run)`, `void Update(BackupRun run)`, `List<BackupRun> GetByJobId(int jobId)`, `BackupRun? GetLatestByJobId(int jobId)`
  - Used by `BackupRunner` (Task 8), `BackgroundScheduler` (Task 9), and every WPF window (Tasks 10-13).

- [ ] **Step 1: Write the failing tests**

`tests/SparkVault.Core.Tests/JobRepositoryTests.cs`:
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
    public void AddThenGetById_RoundTripsAllFields()
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
                DestinationPath = @"D:\Backups\Documents",
                ExcludePatterns = new List<string> { "*.tmp", "cache\\*" },
                ScheduleType = ScheduleType.DailyAt,
                DailyAtTime = new TimeOnly(2, 0),
            };

            var id = repo.Add(job);
            var loaded = repo.GetById(id);

            Assert.NotNull(loaded);
            Assert.Equal("Documents", loaded!.Name);
            Assert.Equal(@"C:\Users\me\Documents", loaded.SourcePath);
            Assert.Equal(new List<string> { "*.tmp", "cache\\*" }, loaded.ExcludePatterns);
            Assert.Equal(ScheduleType.DailyAt, loaded.ScheduleType);
            Assert.Equal(new TimeOnly(2, 0), loaded.DailyAtTime);
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
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob { Name = "Old", SourcePath = "C:\\a", DestinationPath = "D:\\b" });

            var job = repo.GetById(id)!;
            job.Name = "New";
            repo.Update(job);

            Assert.Equal("New", repo.GetById(id)!.Name);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Delete_RemovesJob()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob { Name = "Temp", SourcePath = "C:\\a", DestinationPath = "D:\\b" });

            repo.Delete(id);

            Assert.Null(repo.GetById(id));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetAll_ReturnsAllJobs()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            repo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", DestinationPath = "D:\\a" });
            repo.Add(new BackupJob { Name = "B", SourcePath = "C:\\b", DestinationPath = "D:\\b" });

            Assert.Equal(2, repo.GetAll().Count);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
```

`tests/SparkVault.Core.Tests/RunRepositoryTests.cs`:
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
    public void AddThenUpdate_PersistsCompletion()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", DestinationPath = "D:\\a" });

            var runRepo = new RunRepository(connectionString);
            var run = new BackupRun { JobId = jobId, StartedAt = new DateTime(2026, 8, 19, 10, 0, 0), Status = RunStatus.Failed };
            var runId = run.Id = runRepo.Add(run);

            run.Status = RunStatus.Success;
            run.EndedAt = new DateTime(2026, 8, 19, 10, 5, 0);
            run.FileCount = 3;
            run.TotalBytes = 1024;
            runRepo.Update(run);

            var loaded = runRepo.GetByJobId(jobId).Single(r => r.Id == runId);
            Assert.Equal(RunStatus.Success, loaded.Status);
            Assert.Equal(3, loaded.FileCount);
            Assert.Equal(1024, loaded.TotalBytes);
            Assert.Equal(new DateTime(2026, 8, 19, 10, 5, 0), loaded.EndedAt);
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
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", DestinationPath = "D:\\a" });

            var runRepo = new RunRepository(connectionString);
            runRepo.Add(new BackupRun { JobId = jobId, StartedAt = new DateTime(2026, 8, 18, 10, 0, 0), Status = RunStatus.Success });
            runRepo.Add(new BackupRun { JobId = jobId, StartedAt = new DateTime(2026, 8, 19, 10, 0, 0), Status = RunStatus.Success });

            var latest = runRepo.GetLatestByJobId(jobId);

            Assert.NotNull(latest);
            Assert.Equal(new DateTime(2026, 8, 19, 10, 0, 0), latest!.StartedAt);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestByJobId_ReturnsNullWhenNoRuns()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var runRepo = new RunRepository(connectionString);

            Assert.Null(runRepo.GetLatestByJobId(999));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "JobRepositoryTests|RunRepositoryTests"`
Expected: FAIL to compile — `SparkVaultDatabase`/`JobRepository`/`RunRepository` do not exist yet.

- [ ] **Step 3: Write `SparkVaultDatabase.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public static class SparkVaultDatabase
{
    public static void EnsureCreated(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Jobs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                DestinationPath TEXT NOT NULL,
                ExcludePatterns TEXT NOT NULL,
                ScheduleType TEXT NOT NULL,
                IntervalHours INTEGER NULL,
                DailyAtTime TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Runs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                Status TEXT NOT NULL,
                FileCount INTEGER NOT NULL,
                TotalBytes INTEGER NOT NULL,
                ErrorMessage TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
```

- [ ] **Step 4: Write `JobRepository.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class JobRepository
{
    private readonly string _connectionString;

    public JobRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public int Add(BackupJob job)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Jobs (Name, SourcePath, DestinationPath, ExcludePatterns, ScheduleType, IntervalHours, DailyAtTime)
            VALUES ($name, $source, $dest, $exclude, $scheduleType, $intervalHours, $dailyAtTime);
            SELECT last_insert_rowid();
            """;
        BindJobParameters(command, job);

        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public void Update(BackupJob job)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Jobs
            SET Name = $name, SourcePath = $source, DestinationPath = $dest,
                ExcludePatterns = $exclude, ScheduleType = $scheduleType,
                IntervalHours = $intervalHours, DailyAtTime = $dailyAtTime
            WHERE Id = $id;
            """;
        BindJobParameters(command, job);
        command.Parameters.AddWithValue("$id", job.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
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
        return reader.Read() ? ReadJob(reader) : null;
    }

    public List<BackupJob> GetAll()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Jobs ORDER BY Name;";

        using var reader = command.ExecuteReader();
        var jobs = new List<BackupJob>();
        while (reader.Read())
            jobs.Add(ReadJob(reader));

        return jobs;
    }

    private static void BindJobParameters(SqliteCommand command, BackupJob job)
    {
        command.Parameters.AddWithValue("$name", job.Name);
        command.Parameters.AddWithValue("$source", job.SourcePath);
        command.Parameters.AddWithValue("$dest", job.DestinationPath);
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
            DestinationPath = reader.GetString(reader.GetOrdinal("DestinationPath")),
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

- [ ] **Step 5: Write `RunRepository.cs`**

```csharp
using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class RunRepository
{
    private readonly string _connectionString;

    public RunRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public int Add(BackupRun run)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Runs (JobId, StartedAt, EndedAt, Status, FileCount, TotalBytes, ErrorMessage)
            VALUES ($jobId, $startedAt, $endedAt, $status, $fileCount, $totalBytes, $error);
            SELECT last_insert_rowid();
            """;
        BindRunParameters(command, run);

        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public void Update(BackupRun run)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Runs
            SET JobId = $jobId, StartedAt = $startedAt, EndedAt = $endedAt, Status = $status,
                FileCount = $fileCount, TotalBytes = $totalBytes, ErrorMessage = $error
            WHERE Id = $id;
            """;
        BindRunParameters(command, run);
        command.Parameters.AddWithValue("$id", run.Id);
        command.ExecuteNonQuery();
    }

    public List<BackupRun> GetByJobId(int jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Runs WHERE JobId = $jobId ORDER BY StartedAt DESC;";
        command.Parameters.AddWithValue("$jobId", jobId);

        using var reader = command.ExecuteReader();
        var runs = new List<BackupRun>();
        while (reader.Read())
            runs.Add(ReadRun(reader));

        return runs;
    }

    public BackupRun? GetLatestByJobId(int jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Runs WHERE JobId = $jobId ORDER BY StartedAt DESC LIMIT 1;";
        command.Parameters.AddWithValue("$jobId", jobId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    private static void BindRunParameters(SqliteCommand command, BackupRun run)
    {
        command.Parameters.AddWithValue("$jobId", run.JobId);
        command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$endedAt", (object?)run.EndedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$fileCount", run.FileCount);
        command.Parameters.AddWithValue("$totalBytes", run.TotalBytes);
        command.Parameters.AddWithValue("$error", (object?)run.ErrorMessage ?? DBNull.Value);
    }

    private static BackupRun ReadRun(SqliteDataReader reader)
    {
        return new BackupRun
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            JobId = reader.GetInt32(reader.GetOrdinal("JobId")),
            StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("StartedAt"))),
            EndedAt = reader.IsDBNull(reader.GetOrdinal("EndedAt")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("EndedAt"))),
            Status = Enum.Parse<RunStatus>(reader.GetString(reader.GetOrdinal("Status"))),
            FileCount = reader.GetInt32(reader.GetOrdinal("FileCount")),
            TotalBytes = reader.GetInt64(reader.GetOrdinal("TotalBytes")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
        };
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter "JobRepositoryTests|RunRepositoryTests"`
Expected: PASS (7/7).

- [ ] **Step 7: Commit**

```bash
git add src/SparkVault.Core/SparkVaultDatabase.cs src/SparkVault.Core/JobRepository.cs src/SparkVault.Core/RunRepository.cs tests/SparkVault.Core.Tests/JobRepositoryTests.cs tests/SparkVault.Core.Tests/RunRepositoryTests.cs
git commit -m "Add SQLite schema and job/run repositories"
```

---

### Task 7: Schedule due-time calculator

**Files:**
- Create: `src/SparkVault.Core/ScheduleCalculator.cs`
- Test: `tests/SparkVault.Core.Tests/ScheduleCalculatorTests.cs`

**Interfaces:**
- Consumes: `BackupJob`, `ScheduleType` (Task 2).
- Produces: `ScheduleCalculator.IsDue(BackupJob job, DateTime? lastRunAt, DateTime now) -> bool`, used by `BackgroundScheduler` (Task 9).

- [ ] **Step 1: Write the failing tests**

```csharp
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class ScheduleCalculatorTests
{
    [Fact]
    public void None_IsNeverDue()
    {
        var job = new BackupJob { ScheduleType = ScheduleType.None };
        Assert.False(ScheduleCalculator.IsDue(job, null, DateTime.UtcNow));
    }

    [Fact]
    public void Interval_DueWhenNeverRun()
    {
        var job = new BackupJob { ScheduleType = ScheduleType.Interval, IntervalHours = 6 };
        Assert.True(ScheduleCalculator.IsDue(job, null, DateTime.UtcNow));
    }

    [Fact]
    public void Interval_NotDueBeforeElapsed()
    {
        var now = new DateTime(2026, 8, 19, 12, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Interval, IntervalHours = 6 };
        Assert.False(ScheduleCalculator.IsDue(job, now.AddHours(-3), now));
    }

    [Fact]
    public void Interval_DueAfterElapsed()
    {
        var now = new DateTime(2026, 8, 19, 12, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Interval, IntervalHours = 6 };
        Assert.True(ScheduleCalculator.IsDue(job, now.AddHours(-7), now));
    }

    [Fact]
    public void DailyAt_NotDueBeforeTargetTime()
    {
        var now = new DateTime(2026, 8, 19, 1, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.DailyAt, DailyAtTime = new TimeOnly(2, 0) };
        Assert.False(ScheduleCalculator.IsDue(job, null, now));
    }

    [Fact]
    public void DailyAt_DueAfterTargetTimeIfNotRunToday()
    {
        var now = new DateTime(2026, 8, 19, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.DailyAt, DailyAtTime = new TimeOnly(2, 0) };
        Assert.True(ScheduleCalculator.IsDue(job, now.AddDays(-1), now));
    }

    [Fact]
    public void DailyAt_NotDueIfAlreadyRunAfterTargetTimeToday()
    {
        var now = new DateTime(2026, 8, 19, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.DailyAt, DailyAtTime = new TimeOnly(2, 0) };
        var lastRun = new DateTime(2026, 8, 19, 2, 30, 0);
        Assert.False(ScheduleCalculator.IsDue(job, lastRun, now));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SparkVault.Core.Tests --filter ScheduleCalculatorTests`
Expected: FAIL to compile — `ScheduleCalculator` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
namespace SparkVault.Core;

public static class ScheduleCalculator
{
    public static bool IsDue(BackupJob job, DateTime? lastRunAt, DateTime now)
    {
        switch (job.ScheduleType)
        {
            case ScheduleType.Interval:
                if (job.IntervalHours is not { } hours)
                    return false;
                return lastRunAt is null || now - lastRunAt.Value >= TimeSpan.FromHours(hours);

            case ScheduleType.DailyAt:
                if (job.DailyAtTime is not { } targetTime)
                    return false;
                var todayTarget = now.Date + targetTime.ToTimeSpan();
                if (now < todayTarget)
                    return false;
                return lastRunAt is null || lastRunAt.Value < todayTarget;

            case ScheduleType.None:
            default:
                return false;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SparkVault.Core.Tests --filter ScheduleCalculatorTests`
Expected: PASS (7/7).

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/ScheduleCalculator.cs tests/SparkVault.Core.Tests/ScheduleCalculatorTests.cs
git commit -m "Add schedule due-time calculator"
```

---

### Task 8: BackupRunner (orchestration + logging)

**Files:**
- Create: `src/SparkVault.Core/BackupRunner.cs`
- Test: `tests/SparkVault.Core.Tests/BackupRunnerTests.cs`

**Interfaces:**
- Consumes: `FileScanner.Scan` (Task 4), `IBackupTarget`/`LocalTarget`/`TransferProgress` (Task 5), `RunRepository` (Task 6), `Serilog.ILogger`.
- Produces: `class BackupRunner(RunRepository runRepository, Serilog.ILogger logger)` with `Task<BackupRun> RunAsync(BackupJob job, IBackupTarget target, IProgress<TransferProgress>? progress, CancellationToken ct)`, used by `BackgroundScheduler` (Task 9) and the WPF windows (Tasks 10-13).

- [ ] **Step 1: Write the failing test**

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
    public async Task RunAsync_CopiesFilesAndRecordsSuccessfulRun()
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
                DestinationPath = destDir.FullName,
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var run = await runner.RunAsync(job, new LocalTarget(destDir.FullName), progress: null, CancellationToken.None);

            Assert.Equal(RunStatus.Success, run.Status);
            Assert.Equal(2, run.FileCount);
            Assert.Equal(11, run.TotalBytes);
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "b.txt")));

            var persisted = runRepo.GetByJobId(jobId).Single();
            Assert.Equal(RunStatus.Success, persisted.Status);
            Assert.NotNull(persisted.EndedAt);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_OnSourceMissing_RecordsFailedRun()
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
                DestinationPath = destDir.FullName,
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var run = await runner.RunAsync(job, new LocalTarget(destDir.FullName), progress: null, CancellationToken.None);

            Assert.Equal(RunStatus.Failed, run.Status);
            Assert.NotNull(run.ErrorMessage);
        }
        finally
        {
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SparkVault.Core.Tests --filter BackupRunnerTests`
Expected: FAIL to compile — `BackupRunner` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
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
```

**Note:** `RunStarted`/`RunCompleted` exist so the tray icon (Task 10) can reflect idle/läuft/Fehler for both manual and scheduled runs from one place — no dedicated test for the events themselves, they're covered indirectly by every `RunAsync` test already asserting the final `run.Status`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SparkVault.Core.Tests --filter BackupRunnerTests`
Expected: PASS (2/2).

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/BackupRunner.cs tests/SparkVault.Core.Tests/BackupRunnerTests.cs
git commit -m "Add BackupRunner orchestration"
```

---

### Task 9: BackgroundScheduler

**Files:**
- Create: `src/SparkVault.Core/BackgroundScheduler.cs`
- Test: `tests/SparkVault.Core.Tests/BackgroundSchedulerTests.cs`

**Interfaces:**
- Consumes: `JobRepository`, `RunRepository` (Task 6), `ScheduleCalculator.IsDue` (Task 7), `BackupRunner.RunAsync` (Task 8), `IBackupTarget` (Task 5).
- Produces: `class BackgroundScheduler(JobRepository jobRepository, RunRepository runRepository, BackupRunner runner, Func<BackupJob, IBackupTarget> targetFactory, TimeSpan pollInterval) : IAsyncDisposable`, used by `App.xaml.cs` composition root (Task 10).

- [ ] **Step 1: Write the failing test**

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
                DestinationPath = destDir.FullName,
                ScheduleType = ScheduleType.Interval,
                IntervalHours = 6,
            });

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            await using var scheduler = new BackgroundScheduler(
                jobRepo, runRepo, runner,
                job => new LocalTarget(job.DestinationPath),
                pollInterval: TimeSpan.FromMilliseconds(50));

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
Expected: FAIL to compile — `BackgroundScheduler` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
namespace SparkVault.Core;

public sealed class BackgroundScheduler : IAsyncDisposable
{
    private readonly JobRepository _jobRepository;
    private readonly RunRepository _runRepository;
    private readonly BackupRunner _runner;
    private readonly Func<BackupJob, IBackupTarget> _targetFactory;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    public BackgroundScheduler(
        JobRepository jobRepository,
        RunRepository runRepository,
        BackupRunner runner,
        Func<BackupJob, IBackupTarget> targetFactory,
        TimeSpan pollInterval)
    {
        _jobRepository = jobRepository;
        _runRepository = runRepository;
        _runner = runner;
        _targetFactory = targetFactory;
        _timer = new PeriodicTimer(pollInterval);
        _loopTask = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                foreach (var job in _jobRepository.GetAll())
                {
                    var lastRun = _runRepository.GetLatestByJobId(job.Id);
                    if (ScheduleCalculator.IsDue(job, lastRun?.StartedAt, DateTime.UtcNow))
                    {
                        await _runner.RunAsync(job, _targetFactory(job), progress: null, _cts.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disposal
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _timer.Dispose();
        try
        {
            await _loopTask;
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SparkVault.Core.Tests --filter BackgroundSchedulerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.Core/BackgroundScheduler.cs tests/SparkVault.Core.Tests/BackgroundSchedulerTests.cs
git commit -m "Add PeriodicTimer-based background scheduler"
```

---

### Task 10: App shell — composition root and tray icon

**Files:**
- Modify: `src/SparkVault.App/App.xaml.cs`
- Modify: `src/SparkVault.App/MainWindow.xaml.cs` (no visual change yet, just confirms wiring compiles)

**Interfaces:**
- Consumes: `SparkVaultDatabase.EnsureCreated`, `JobRepository`, `RunRepository`, `BackupRunner` (incl. `RunStarted`/`RunCompleted` events), `BackgroundScheduler`, `LocalTarget` (Tasks 6-9).
- Produces: `App` exposes `public static JobRepository JobRepository`, `public static RunRepository RunRepository`, `public static BackupRunner Runner` (static composition root, consumed by Tasks 11-13). Tray icon reflects idle/läuft/Fehler by subscribing to `Runner.RunStarted`/`RunCompleted`. No automated test — WPF startup path, verified by manual run.

- [ ] **Step 1: Write `App.xaml.cs`**

```csharp
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Serilog;
using SparkVault.Core;
using Application = System.Windows.Application;

namespace SparkVault.App;

public partial class App : Application
{
    public static JobRepository JobRepository { get; private set; } = null!;
    public static RunRepository RunRepository { get; private set; } = null!;
    public static BackupRunner Runner { get; private set; } = null!;

    private BackgroundScheduler? _scheduler;
    private NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SparkVault");
        Directory.CreateDirectory(appDataDir);

        var connectionString = $"Data Source={Path.Combine(appDataDir, "sparkvault.db")}";
        SparkVaultDatabase.EnsureCreated(connectionString);

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(appDataDir, "log.txt"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        JobRepository = new JobRepository(connectionString);
        RunRepository = new RunRepository(connectionString);
        Runner = new BackupRunner(RunRepository, Log.Logger);

        _scheduler = new BackgroundScheduler(
            JobRepository,
            RunRepository,
            Runner,
            job => new LocalTarget(job.DestinationPath),
            pollInterval: TimeSpan.FromMinutes(1));

        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "SparkVault - bereit",
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Beenden", null, (_, _) => Shutdown());
        _trayIcon.ContextMenuStrip = menu;

        Runner.RunStarted += job => Dispatcher.Invoke(() => SetTrayStatus(running: true, job.Name));
        Runner.RunCompleted += (job, status) => Dispatcher.Invoke(() => SetTrayStatus(running: false, job.Name, status));
    }

    private void SetTrayStatus(bool running, string jobName, RunStatus? status = null)
    {
        if (_trayIcon is null) return;

        if (running)
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            _trayIcon.Text = Truncate($"SparkVault - sichert \"{jobName}\"...");
        }
        else if (status == RunStatus.Failed)
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Warning;
            _trayIcon.Text = Truncate($"SparkVault - Fehler bei \"{jobName}\"");
        }
        else
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
            _trayIcon.Text = "SparkVault - bereit";
        }
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            MainWindow = new MainWindow();
        }
        MainWindow.Show();
        MainWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _scheduler?.DisposeAsync().AsTask().Wait();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
```

- [ ] **Step 2: Remove `StartupUri` from `App.xaml`** (the tray icon controls window visibility now, not automatic startup)

```xml
<Application x:Class="SparkVault.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
</Application>
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Manual verification**

Run: `dotnet run --project src/SparkVault.App`
Expected: No visible window appears, but a SparkVault tray icon shows up in the notification area. Double-clicking it opens the (still placeholder) main window. Right-click shows "Öffnen"/"Beenden". "Beenden" closes the app. Confirm `%AppData%\SparkVault\sparkvault.db` and `log.txt` were created.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.App/App.xaml src/SparkVault.App/App.xaml.cs
git commit -m "Wire composition root, SQLite/log setup, and tray icon"
```

---

### Task 11: Main window — job list

**Files:**
- Modify: `src/SparkVault.App/MainWindow.xaml`
- Modify: `src/SparkVault.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `App.JobRepository`, `App.RunRepository`, `App.Runner` (Task 10), `BackupJob`, `BackupRun`, `RunStatus` (Task 2), `LocalTarget` (Task 5).
- Produces: `MainWindow` with a `JobRow` view-model list bound to a `DataGrid`; opens `JobEditorWindow` (Task 12) and `LogWindow` (Task 13) by job id.

- [ ] **Step 1: Write `MainWindow.xaml`**

```xml
<Window x:Class="SparkVault.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="SparkVault" Height="450" Width="800"
        Closing="Window_Closing">
    <DockPanel Margin="12">
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
            <Button x:Name="AddJobButton" Content="Neuer Job" Padding="8,4" Click="AddJobButton_Click" />
            <Button x:Name="EditJobButton" Content="Bearbeiten" Padding="8,4" Margin="8,0,0,0" Click="EditJobButton_Click" />
            <Button x:Name="DeleteJobButton" Content="Löschen" Padding="8,4" Margin="8,0,0,0" Click="DeleteJobButton_Click" />
            <Button x:Name="RunNowButton" Content="Jetzt sichern" Padding="8,4" Margin="8,0,0,0" Click="RunNowButton_Click" />
            <Button x:Name="ShowLogButton" Content="Log anzeigen" Padding="8,4" Margin="8,0,0,0" Click="ShowLogButton_Click" />
            <ProgressBar x:Name="RunProgressBar" Width="160" Height="18" Margin="16,4,0,0" Minimum="0" Maximum="100" />
        </StackPanel>
        <DataGrid x:Name="JobsGrid" AutoGenerateColumns="False" IsReadOnly="True" SelectionMode="Single">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="150" />
                <DataGridTextColumn Header="Quelle" Binding="{Binding SourcePath}" Width="200" />
                <DataGridTextColumn Header="Ziel" Binding="{Binding DestinationPath}" Width="200" />
                <DataGridTextColumn Header="Letzter Lauf" Binding="{Binding LastRunDisplay}" Width="140" />
                <DataGridTextColumn Header="Nächster Lauf" Binding="{Binding NextRunDisplay}" Width="140" />
                <DataGridTextColumn Header="Status" Binding="{Binding LastStatusDisplay}" Width="80" />
            </DataGrid.Columns>
        </DataGrid>
    </DockPanel>
</Window>
```

- [ ] **Step 2: Write `MainWindow.xaml.cs`**

```csharp
using System.Collections.ObjectModel;
using System.Windows;
using SparkVault.Core;

namespace SparkVault.App;

public sealed class JobRow
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string DestinationPath { get; init; } = "";
    public string LastRunDisplay { get; init; } = "-";
    public string NextRunDisplay { get; init; } = "-";
    public string LastStatusDisplay { get; init; } = "-";
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<JobRow> _jobs = new();

    public MainWindow()
    {
        InitializeComponent();
        JobsGrid.ItemsSource = _jobs;
        ReloadJobs();
    }

    private void ReloadJobs()
    {
        _jobs.Clear();
        foreach (var job in App.JobRepository.GetAll())
        {
            var lastRun = App.RunRepository.GetLatestByJobId(job.Id);
            _jobs.Add(new JobRow
            {
                Id = job.Id,
                Name = job.Name,
                SourcePath = job.SourcePath,
                DestinationPath = job.DestinationPath,
                LastRunDisplay = lastRun?.StartedAt.ToLocalTime().ToString("g") ?? "-",
                NextRunDisplay = DescribeNextRun(job, lastRun),
                LastStatusDisplay = lastRun?.Status.ToString() ?? "-",
            });
        }
    }

    private static string DescribeNextRun(BackupJob job, BackupRun? lastRun)
    {
        switch (job.ScheduleType)
        {
            case ScheduleType.Interval when job.IntervalHours is { } hours:
                if (lastRun is null) return "fällig";
                return lastRun.StartedAt.ToLocalTime().AddHours(hours).ToString("g");

            case ScheduleType.DailyAt when job.DailyAtTime is { } time:
                var todayTarget = DateTime.Today + time.ToTimeSpan();
                var next = DateTime.Now < todayTarget ? todayTarget : todayTarget.AddDays(1);
                return next.ToString("g");

            default:
                return "-";
        }
    }

    private JobRow? SelectedJob => JobsGrid.SelectedItem as JobRow;

    private void AddJobButton_Click(object sender, RoutedEventArgs e)
    {
        var editor = new JobEditorWindow(jobId: null) { Owner = this };
        if (editor.ShowDialog() == true)
            ReloadJobs();
    }

    private void EditJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is null) return;
        var editor = new JobEditorWindow(jobId: SelectedJob.Id) { Owner = this };
        if (editor.ShowDialog() == true)
            ReloadJobs();
    }

    private void DeleteJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is null) return;
        var result = MessageBox.Show(this, $"Job \"{SelectedJob.Name}\" wirklich löschen?", "SparkVault",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            App.JobRepository.Delete(SelectedJob.Id);
            ReloadJobs();
        }
    }

    private async void RunNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is null) return;
        var job = App.JobRepository.GetById(SelectedJob.Id);
        if (job is null) return;

        RunNowButton.IsEnabled = false;
        RunProgressBar.Value = 0;
        var progress = new Progress<TransferProgress>(p =>
            RunProgressBar.Value = p.FilesTotal == 0 ? 0 : (double)p.FilesDone / p.FilesTotal * 100);

        try
        {
            await App.Runner.RunAsync(job, new LocalTarget(job.DestinationPath), progress, CancellationToken.None);
        }
        finally
        {
            RunNowButton.IsEnabled = true;
            RunProgressBar.Value = 0;
            ReloadJobs();
        }
    }

    private void ShowLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is null) return;
        var logWindow = new LogWindow(SelectedJob.Id, SelectedJob.Name) { Owner = this };
        logWindow.Show();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: Build fails — `JobEditorWindow` and `LogWindow` don't exist yet. This is expected; they're added in Tasks 12-13. Confirm the *only* errors are "type or namespace 'JobEditorWindow'/'LogWindow' could not be found" — everything else must compile clean.

- [ ] **Step 4: Commit**

```bash
git add src/SparkVault.App/MainWindow.xaml src/SparkVault.App/MainWindow.xaml.cs
git commit -m "Add main window job list"
```

---

### Task 12: Job editor window

**Files:**
- Create: `src/SparkVault.App/JobEditorWindow.xaml`
- Create: `src/SparkVault.App/JobEditorWindow.xaml.cs`

**Interfaces:**
- Consumes: `App.JobRepository` (Task 10), `BackupJob`, `ScheduleType` (Task 2).
- Produces: `JobEditorWindow(int? jobId)` — a modal dialog that sets `DialogResult = true` on save. Consumed by `MainWindow` (Task 11, already wired).

- [ ] **Step 1: Write `JobEditorWindow.xaml`**

```xml
<Window x:Class="SparkVault.App.JobEditorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Job bearbeiten" Height="480" Width="480" WindowStartupLocation="CenterOwner">
    <StackPanel Margin="12">
        <TextBlock Text="Name" />
        <TextBox x:Name="NameBox" Margin="0,0,0,8" />

        <TextBlock Text="Quellpfad" />
        <DockPanel Margin="0,0,0,8">
            <Button Content="..." Width="30" DockPanel.Dock="Right" Click="BrowseSource_Click" />
            <TextBox x:Name="SourcePathBox" />
        </DockPanel>

        <TextBlock Text="Zielpfad (lokal/UNC)" />
        <DockPanel Margin="0,0,0,8">
            <Button Content="..." Width="30" DockPanel.Dock="Right" Click="BrowseDestination_Click" />
            <TextBox x:Name="DestinationPathBox" />
        </DockPanel>

        <TextBlock Text="Ausschlussmuster (eine Zeile pro Muster, z. B. *.tmp)" />
        <TextBox x:Name="ExcludePatternsBox" Height="60" AcceptsReturn="True" TextWrapping="Wrap" Margin="0,0,0,8" />

        <TextBlock Text="Zeitplan" />
        <ComboBox x:Name="ScheduleTypeCombo" Margin="0,0,0,8" SelectionChanged="ScheduleTypeCombo_SelectionChanged">
            <ComboBoxItem Content="Manuell" Tag="None" />
            <ComboBoxItem Content="Intervall (Stunden)" Tag="Interval" />
            <ComboBoxItem Content="Täglich um" Tag="DailyAt" />
        </ComboBox>

        <TextBlock Text="Intervall in Stunden" x:Name="IntervalLabel" />
        <TextBox x:Name="IntervalHoursBox" Margin="0,0,0,8" />

        <TextBlock Text="Uhrzeit (HH:mm)" x:Name="DailyAtLabel" />
        <TextBox x:Name="DailyAtTimeBox" Margin="0,0,0,8" />

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Abbrechen" Padding="12,4" Click="Cancel_Click" />
            <Button Content="Speichern" Padding="12,4" Margin="8,0,0,0" Click="Save_Click" />
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Write `JobEditorWindow.xaml.cs`**

```csharp
using System.Windows;
using SparkVault.Core;

namespace SparkVault.App;

public partial class JobEditorWindow : Window
{
    private readonly int? _jobId;

    public JobEditorWindow(int? jobId)
    {
        InitializeComponent();
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
        DestinationPathBox.Text = job.DestinationPath;
        ExcludePatternsBox.Text = string.Join(Environment.NewLine, job.ExcludePatterns);
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
        var path = PickFolder();
        if (path is not null) SourcePathBox.Text = path;
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder();
        if (path is not null) DestinationPathBox.Text = path;
    }

    private static string? PickFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) ||
            string.IsNullOrWhiteSpace(SourcePathBox.Text) ||
            string.IsNullOrWhiteSpace(DestinationPathBox.Text))
        {
            MessageBox.Show(this, "Name, Quellpfad und Zielpfad sind Pflichtfelder.", "SparkVault",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
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
            DestinationPath = DestinationPathBox.Text.Trim(),
            ExcludePatterns = ExcludePatternsBox.Text
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            ScheduleType = scheduleType,
            IntervalHours = intervalHours,
            DailyAtTime = dailyAtTime,
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
Expected: Build succeeds (this also resolves the `JobEditorWindow` reference from Task 11). Folder picking uses `System.Windows.Forms.FolderBrowserDialog`, already available via `UseWindowsForms`.

- [ ] **Step 4: Manual verification**

Run: `dotnet run --project src/SparkVault.App`. Open main window, click "Neuer Job", fill in Name/Quellpfad/Zielpfad, pick "Intervall" with 6 hours, save. Confirm the job appears in the grid. Edit it, switch to "Täglich um" with `02:00`, save, confirm it persists after closing/reopening the app.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.App/JobEditorWindow.xaml src/SparkVault.App/JobEditorWindow.xaml.cs
git commit -m "Add job editor window"
```

---

### Task 13: Log window — run history per job

**Files:**
- Create: `src/SparkVault.App/LogWindow.xaml`
- Create: `src/SparkVault.App/LogWindow.xaml.cs`

**Interfaces:**
- Consumes: `App.RunRepository` (Task 10), `BackupRun`, `RunStatus` (Task 2).
- Produces: `LogWindow(int jobId, string jobName)`, consumed by `MainWindow` (Task 11, already wired).

- [ ] **Step 1: Write `LogWindow.xaml`**

```xml
<Window x:Class="SparkVault.App.LogWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Log" Height="400" Width="600" WindowStartupLocation="CenterOwner">
    <DataGrid x:Name="RunsGrid" Margin="12" AutoGenerateColumns="False" IsReadOnly="True">
        <DataGrid.Columns>
            <DataGridTextColumn Header="Start" Binding="{Binding StartedAt}" Width="140" />
            <DataGridTextColumn Header="Ende" Binding="{Binding EndedAt}" Width="140" />
            <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="80" />
            <DataGridTextColumn Header="Dateien" Binding="{Binding FileCount}" Width="70" />
            <DataGridTextColumn Header="Bytes" Binding="{Binding TotalBytes}" Width="90" />
            <DataGridTextColumn Header="Fehler" Binding="{Binding ErrorMessage}" Width="*" />
        </DataGrid.Columns>
    </DataGrid>
</Window>
```

- [ ] **Step 2: Write `LogWindow.xaml.cs`**

```csharp
using System.Windows;

namespace SparkVault.App;

public partial class LogWindow : Window
{
    public LogWindow(int jobId, string jobName)
    {
        InitializeComponent();
        Title = $"Log – {jobName}";
        RunsGrid.ItemsSource = App.RunRepository.GetByJobId(jobId);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Manual verification**

Run: `dotnet run --project src/SparkVault.App`. Run a job via "Jetzt sichern", then click "Log anzeigen" for that job. Confirm the run shows up with correct status/file count/bytes. Point a job's `Zielpfad` at a non-existent drive letter, run it, confirm the log shows `Failed` with an error message and the app doesn't crash.

- [ ] **Step 5: Commit**

```bash
git add src/SparkVault.App/LogWindow.xaml src/SparkVault.App/LogWindow.xaml.cs
git commit -m "Add log window with run history"
```

---

### Task 14: Full-solution verification

**Files:** none (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: All tests pass (should be ~26 tests across Exclusion/Scanner/LocalTarget/JobRepository/RunRepository/ScheduleCalculator/BackupRunner/BackgroundScheduler).

- [ ] **Step 2: Full manual smoke test**

Run: `dotnet run --project src/SparkVault.App`. Walk the whole MVP flow end to end:
1. Tray icon appears, no window pops up automatically.
2. Create a job with a real source folder and a local destination folder, an exclude pattern (e.g. `*.log`), and a 1-hour interval schedule.
3. Click "Jetzt sichern" — progress completes, files appear in destination, excluded files don't.
4. Kill-test: start a backup on a large-ish folder and close the app mid-copy (or use Task Manager) — confirm no `*.sparkvault-tmp` file was left renamed as a real file in the destination.
5. Wait for/simulate the schedule (or temporarily set a 1-minute interval) and confirm the scheduler triggers a run without manual interaction.
6. Open "Log anzeigen" — confirm run history is accurate.
7. Check `%AppData%\SparkVault\log.txt` has structured log entries for the runs above.
8. Quit via tray "Beenden" — confirm the process exits cleanly (no orphaned process in Task Manager).

- [ ] **Step 3: Commit** (only if Step 2 uncovered fixes)

```bash
git add -A
git commit -m "Fix issues found during MVP smoke test"
```
