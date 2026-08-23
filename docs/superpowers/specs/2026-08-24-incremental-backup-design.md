# Inkrementelles Backup & "Bei veränderten Daten" – Design Spec

Datum: 2026-08-24
Basis: `Projektdokumentation-Backup-Tool.md`, [2026-08-19-sparkvault-mvp-design.md](2026-08-19-sparkvault-mvp-design.md),
[2026-08-23-restore-feature-design.md](2026-08-23-restore-feature-design.md) (definiert `RunFiles`,
`RestoreRunner`, die dieser Spec erweitert).

## 1. Zielsetzung

Heute überträgt jeder Lauf **jede** Quelldatei erneut, unabhängig davon, ob sie sich seit dem
letzten Lauf geändert hat (`BackupRunner.RunForTargetAsync` lädt bedingungslos jede gescannte
Datei hoch). Bei großen Datenmengen und S3-Zielen bedeutet das unnötig lange Laufzeiten und
unnötigen Traffic (S3-Requests + Egress/Ingress-Kosten).

Ziel dieser Spec:
1. Unveränderte Dateien werden **nicht** erneut übertragen, bleiben aber weiterhin Teil des
   Datei-Katalogs jedes Laufs — Wiederherstellung eines beliebigen Standes bleibt ein
   vollständiger Snapshot.
2. Aus der Quelle gelöschte Dateien werden am Ziel in einen Quarantäne-Ordner verschoben statt
   liegen zu bleiben oder gelöscht zu werden — kein Datenverlust-Risiko, Ziel wird trotzdem nicht
   endlos mit "toten" Pfaden im normalen Katalog verschmutzt.
3. Neuer Zeitplan-Typ "Bei veränderten Daten": prüft **einmalig beim Programmstart**, ob sich
   seit dem letzten erfolgreichen Lauf irgendetwas geändert hat, und startet den Job nur dann.
4. Pro Job optional: vor jedem Lauf den tatsächlichen Ziel-Inhalt gegenprüfen (schützt vor
   Abweichungen durch externe Änderungen am Ziel), Standard aus.

## 2. Entschiedene Grundsatzfragen

| Frage | Entscheidung | Begründung |
|---|---|---|
| Änderungserkennung | Größe **und** Änderungszeit (`LastWriteTimeUtc`) der Quelldatei, verglichen mit dem beim letzten erfolgreichen Upload gespeicherten Wert | Rsync-Standard, liest nur Metadaten. Hashing würde bei großen Datenmengen genau die Last erzeugen, die vermieden werden soll. |
| Herkunft des "letzten bekannten Stands" | Der Datei-Katalog (`RunFiles`) des letzten **erfolgreichen** Laufs für dasselbe Ziel — keine neue Tabelle | `RunFiles` existiert schon (Restore-Feature); Wiederverwendung statt Duplikation. |
| Übersprungene Dateien im neuen Katalog | Ja, jede unveränderte Datei bleibt im Katalog des neuen Laufs (zeigt weiter auf denselben, unangetasteten Zielpfad) | Sonst würde Restore eines Laufs mit vielen übersprungenen Dateien nur einen Bruchteil des tatsächlichen Standes zurückspielen. |
| Gelöschte Dateien am Ziel | In einen Quarantäne-Unterordner **verschieben** (serverseitig, kein Download+Reupload) | Nutzerentscheidung — sicherer als Löschen, räumt aber trotzdem aus dem aktiven Katalog auf. |
| Wiederherstellung eines älteren Standes nach späterer Quarantäne | Automatischer Fallback: schlägt der Download am Originalpfad fehl, wird in einer kleinen Zuordnungstabelle nachgeschaut, wohin die Datei zuletzt verschoben wurde | Nutzerentscheidung — verschieben darf Restore älterer Stände nicht stillschweigend brechen. |
| Vertrauensmodell (Ziel-Inhalt vor jedem Lauf prüfen) | Pro Job per Checkbox, Standard **aus** (eigenem Datensatz vertrauen) | Nutzerentscheidung — passt zum Ziel, Traffic zu reduzieren; wer misstraut, schaltet es gezielt ein. |
| "Bei veränderten Daten"-Trigger | Nur einmalig beim Programmstart geprüft, kein laufender Datei-Watcher, keine periodische Prüfung durch den `BackgroundScheduler` | Nutzerentscheidung — explizit "beim Hochfahren", nicht fortlaufend. |
| Rename/Move-Fähigkeit pro Ziel-Typ | Lokal: `File.Move`. SFTP: `SftpClient.RenameFile` (bereits im Code für den Temp→Final-Rename verwendet). FTP: `AsyncFtpClient.Rename` (ebenfalls bereits verwendet). S3: `CopyObjectAsync`+`DeleteObjectAsync` (serverseitige Kopie, kein lokaler Umweg) | Alle vier APIs sind im bestehenden Code bereits nachweislich vorhanden/verwendet (Local/SFTP/FTP) bzw. Standard-AWS-SDK-Aufrufe (S3) — keine neue Abhängigkeit nötig. `CopyObjectAsync`/`DeleteObjectAsync` sind noch nicht gegen den echten MinIO-Testcontainer verifiziert (siehe Testkonzept, Abschnitt 9). |

## 3. Datenmodell

**`BackupFile`** (Record) bekommt ein zusätzliches Feld:
```csharp
public sealed record BackupFile(string FullPath, string RelativePath, long Size, DateTime LastWriteTimeUtc);
```
`FileScanner.Scan` liest `LastWriteTimeUtc` aus demselben bereits vorhandenen `FileInfo`-Aufruf,
der heute schon `Length` liefert (kein zusätzlicher Dateisystem-Zugriff).

**`RunFileRecord`** (Record) bekommt ein zusätzliches, nullable Feld (nullable wegen bestehender
Alt-Zeilen ohne diesen Wert — siehe Migration unten):
```csharp
public sealed record RunFileRecord(string RelativePath, long Size, DateTime? SourceModifiedUtc);
```
Jeder bestehende Aufrufer von `new RunFileRecord(...)` (aktuell nur `BackupRunner`) und jeder Test,
der den Record direkt konstruiert, muss auf die neue 3-Parameter-Form angepasst werden.

**Neuer gemeinsamer Typ** `ManifestEntry` (repräsentiert eine Zeile aus dem "letzten bekannten
Katalog" eines Ziels, unabhängig davon ob sie aus `RunFileRecord` oder direkt aus der DB kommt):
```csharp
public sealed record ManifestEntry(string RelativePath, long Size, DateTime? SourceModifiedUtc);
```

**Schema-Änderungen** (`SparkVaultDatabase.cs`) — sowohl in den `CREATE TABLE IF NOT EXISTS`-
Statements (Neuinstallation) als auch über die bestehende `EnsureColumn`-Migration (bestehende
Datenbanken, gleiches Muster wie `WeeklyDay`/`MonthlyDay`):
```sql
-- RunFiles: zusätzliche Spalte
ALTER TABLE RunFiles ADD COLUMN SourceModifiedUtc TEXT NULL;

-- Jobs: zusätzliche Spalte
ALTER TABLE Jobs ADD COLUMN VerifyTargetBeforeRun INTEGER NOT NULL DEFAULT 0;

-- Neue Tabelle: merkt sich, wohin eine quarantänisierte Datei zuletzt verschoben wurde
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
`EnsureColumn(connection, "RunFiles", "SourceModifiedUtc", "TEXT NULL");` und
`EnsureColumn(connection, "Jobs", "VerifyTargetBeforeRun", "INTEGER NOT NULL DEFAULT 0");` werden
neben den bestehenden `EnsureColumn`-Aufrufen in `EnsureCreated` ergänzt. `QuarantinedFiles` ist
eine neue Tabelle — `CREATE TABLE IF NOT EXISTS` reicht dafür (kein Migrationsproblem, siehe die
bereits behobene Lücke bei `Jobs`).

**`BackupJob`** bekommt ein neues Feld:
```csharp
public bool VerifyTargetBeforeRun { get; set; }
```

**`ScheduleType`** bekommt einen neuen Wert, **am Ende angehängt** (bestehende Enum-Werte/Combo-
Indizes bleiben unverändert, kein Renumbering nötig):
```csharp
public enum ScheduleType { None, Interval, DailyAt, Weekdays, Weekly, Monthly, OnChange }
```

## 4. Geteilte Vergleichs-Logik

**`JobFileScanner`** (neue statische Klasse, `src/SparkVault.Core/JobFileScanner.cs`) — extrahiert
aus `BackupRunner.RunAsync`, damit sowohl `BackupRunner` als auch der neue Programmstart-Check
(Abschnitt 7) exakt dieselbe Scan+Präfix-Logik verwenden:
```csharp
public static class JobFileScanner
{
    public static IReadOnlyList<BackupFile> Scan(BackupJob job)
    {
        var scanned = FileScanner.Scan(job.SourcePath, job.ExcludePatterns);
        var jobFolder = SanitizeForPath(job.Name);
        return scanned.Select(f => f with { RelativePath = $"{jobFolder}\\{f.RelativePath}" }).ToList();
    }

    // Aus BackupRunner verschoben (dort bisher private).
    internal static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
```
`BackupRunner.RunAsync` ruft künftig `JobFileScanner.Scan(job)` statt der bisherigen Inline-Logik
auf; die private `SanitizeForPath`/das Inline-`scanned.Select(...)` entfallen dort. `RestoreRunner`
verwendet weiterhin seine eigene, unabhängige `SanitizeForPath`-Kopie (Restore braucht nur das
Präfix-Abschneiden, nicht den vollen Scan) — keine Änderung an `RestoreRunner` in diesem Punkt.

**`IncrementalPlanner`** (neue statische Klasse, `src/SparkVault.Core/IncrementalPlanner.cs`) —
reine, leicht testbare Funktion ohne DB-/Dateisystem-Zugriff:
```csharp
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

    public static bool HasChanges(IReadOnlyList<BackupFile> currentFiles, IReadOnlyList<ManifestEntry> previousManifest) =>
        Compute(currentFiles, previousManifest) is { } plan && (plan.ToUpload.Count > 0 || plan.ToQuarantine.Count > 0);
}
```
Ein `previousManifest`-Eintrag mit `SourceModifiedUtc == null` (Alt-Zeile aus der Zeit vor dieser
Migration) matcht nie exakt gegen eine aktuelle Datei (die immer einen konkreten Wert hat) — diese
Dateien fallen einmalig zurück in `ToUpload`, heilen sich beim nächsten Lauf danach selbst.

## 5. Letzten bekannten Katalog laden

**`RunRepository`** — neue Methode:
```csharp
public BackupRun? GetLatestSuccessfulRun(int jobId, int targetId)
{
    // SELECT * FROM Runs WHERE JobId = $jobId AND TargetId = $targetId AND Status = 'Success'
    // ORDER BY StartedAt DESC LIMIT 1;
}
```

**`RunFileRepository.GetByRunId`** liefert bereits alle Felder des Manifests; sie wird lediglich um
`SourceModifiedUtc` erweitert (SELECT-Spalte + `RunFileRecord`-Konstruktor-Parameter, `IsDBNull`-
Prüfung wie bei den übrigen nullable Spalten in diesem Projekt).

**`BackupRunner`** lädt den Katalog pro Ziel:
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

## 6. `BackupRunner` — Upload überspringen, gelöschte Dateien quarantänisieren

`RunForTargetAsync` (siehe aktueller Code, `src/SparkVault.Core/BackupRunner.cs:88-154`) wird so
erweitert:

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
        // Nicht fatal: das eigentliche Backup ist wichtiger als das Aufräumen gelöschter Dateien.
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

Wichtige Punkte:
- `totalBytes`/die Fortschrittsanzeige zählen nur `plan.ToUpload` — übersprungene Dateien sind
  keine sichtbare "Bewegung" und würden den Fortschrittsring nur künstlich vorspulen.
- `plan.Unchanged`-Einträge werden **vor** der Upload-Schleife in `uploaded` aufgenommen, damit
  `RunFileRepository.AddRange` am Ende exakt einen vollständigen Katalog schreibt (unverändert +
  neu/geändert) — identisch zum heutigen Verhalten aus Restore-Sicht.
- Ein Fehler beim Quarantänisieren einer einzelnen Datei bricht den Lauf nicht ab (analog zum
  bestehenden Kommentar zum FTP-Rename-Race in `FtpTarget.cs`).
- `_quarantineRepository` ist eine neue Konstruktor-Abhängigkeit von `BackupRunner`
  (`QuarantineRepository`, Abschnitt 8) — Konstruktions-Stelle in `App.xaml.cs` entsprechend
  anpassen.

**Konstante `TotalBytes`/`FileCount` auf `run`**: `run.FileCount`/`run.TotalBytes` (siehe
`finally`-Block, `BackupRunner.cs:144-151`) sollen weiterhin die **tatsächlich übertragene** Menge
zeigen (`done`/`bytesDone`, unverändert), nicht die Gesamtgröße des Katalogs — passt zur
bestehenden Bedeutung dieser Felder in der Verlaufs-Anzeige ("was ist in diesem Lauf passiert").

## 7. `IBackupTarget.MoveAsync` — vier Implementierungen

Neue Interface-Methode:
```csharp
Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct);
```

**LocalTarget**:
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

**SftpTarget** (`RenameFile(string, string, bool isPosix)` bereits verwendet in `UploadAsync`,
`CreateDirectoryRecursive` bereits vorhanden):
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

**FtpTarget** (`Rename(string, string, CancellationToken)` bereits verwendet in `UploadAsync`):
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
Zu verifizieren während der Umsetzung (gegen den echten FTP-Testcontainer, wie beim
Restore-Feature): ob `CreateDirectory` rekursiv fehlende Zwischenordner mit anlegt, oder ob dafür
mehrere Aufrufe (ein Aufruf je Pfadsegment, analog `SftpTarget.CreateDirectoryRecursive`) nötig
sind.

**S3Target** (serverseitige Kopie, kein lokaler Download/Reupload):
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
            SourceBucket = _bucket, SourceKey = fromKey,
            DestinationBucket = _bucket, DestinationKey = toKey,
        }, ct);
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return; // Quelle existiert nicht mehr — nichts zu tun.
    }
    await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = fromKey }, ct);
}
```
**Noch nicht gegen den echten MinIO-Testcontainer verifiziert** — im Gegensatz zu allen anderen in
dieser Spec zitierten API-Aufrufen, die entweder aus bereits laufendem Code stammen oder (Restore-
Feature) explizit gegen die Testcontainer geprüft wurden. Erster Implementierungsschritt für S3
`MoveAsync` muss diesen Roundtrip (hochladen, `MoveAsync`, prüfen dass Quelle weg und Ziel exakt
gleich groß ist) gegen MinIO verifizieren, bevor der Rest der Spec darauf aufbaut.

## 8. `QuarantineRepository` und Restore-Fallback

**Neues Repository** `src/SparkVault.Core/QuarantineRepository.cs`:
```csharp
public sealed class QuarantineRepository
{
    public QuarantineRepository(string connectionString);

    public void Add(int jobId, int targetId, string originalRelativePath, string quarantinePath, int quarantinedAtRunId, DateTime quarantinedAtUtc);

    // Neuester Eintrag zuerst (ORDER BY QuarantinedAtUtc DESC LIMIT 1) — deckt den Regelfall ab
    // (eine Datei wird höchstens einmal quarantänisiert); bei mehrfacher Quarantäne derselben
    // Datei (selten: Datei neu angelegt, wieder gelöscht) liefert das den zuletzt verschobenen Stand.
    public string? GetLatestQuarantinePath(int jobId, int targetId, string originalRelativePath);
}
```

**`RestoreRunner.RestoreAsync`** (`src/SparkVault.Core/RestoreRunner.cs:42-67`) bekommt einen
Fallback um den bestehenden `DownloadAsync`-Aufruf. Neue Konstruktor-Abhängigkeit
`QuarantineRepository`:
```csharp
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
        throw primaryEx; // die ursprüngliche, aussagekräftigere Fehlermeldung durchreichen
    }
}
File.Move(tempDestination, localDestination, overwrite: true);
```
Der äußere `try`/`catch` (Aufräumen von `tempDestination` bei Fehlschlag) bleibt wie heute um den
gesamten Block herum bestehen. Kein Sniffen konkreter Exception-Typen pro Ziel-Implementierung
nötig: schlägt der Fallback ebenfalls fehl, wird der **ursprüngliche** Fehler weitergereicht, nie
der des Fallback-Versuchs — bei einem echten Verbindungsproblem (nicht: Datei verschoben) scheitert
der Fallback-Versuch aus demselben Grund und der Nutzer sieht trotzdem die richtige Fehlermeldung.

`App.xaml.cs`/wo auch immer `RestoreRunner` konstruiert wird: neuer `QuarantineRepository`-Parameter.

## 9. Neuer Zeitplan-Typ "Bei veränderten Daten"

**`ScheduleCalculator.IsDue`** (`src/SparkVault.Core/ScheduleCalculator.cs`) — neuer Fall, immer
`false`: dieser Zeitplan-Typ wird nie über den periodischen `BackgroundScheduler`-Takt ausgelöst,
nur über den einmaligen Programmstart-Check unten.
```csharp
case ScheduleType.OnChange:
    return false;
```

**Neue Klasse** `src/SparkVault.Core/OnChangeJobChecker.cs`:
```csharp
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
            // Quelle nicht lesbar: kein erzwungener, zum Scheitern verurteilter Lauf hier —
            // der reguläre Lauf (falls je manuell/zeitbasiert gestartet) meldet den echten Fehler.
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
Rein lokale Prüfung (nur `FileScanner`/DB, kein Netzwerkzugriff zum Ziel) — schnell, kein
zusätzlicher API-Traffic nur um zu entscheiden, ob überhaupt gesichert werden muss.

**`App.xaml.cs`** — Aufruf **am Ende** von `OnStartup`, nachdem `Runner`, `_trayIcon` und die
`Runner.RunStarted`/`RunCompleted`-Ereignisse verdrahtet sind (Zeile ~110 im aktuellen Code), damit
ein durch diesen Check ausgelöster Lauf den Tray-Status korrekt widerspiegelt. Als Hintergrund-Task,
blockiert den UI-Start nicht:
```csharp
_ = Task.Run(() => OnChangeJobChecker.RunDueJobsAsync(
    JobRepository, RunRepository, RunFileRepository, Runner, Log.Logger, CancellationToken.None));
```

## 10. UI

**`MainWindow.xaml`** — Zeitplan-ComboBox (`src/SparkVault.App/MainWindow.xaml:310-317`), neuer
Eintrag **am Ende** (Index 6, kein Renumbering bestehender Indizes):
```xml
<ComboBoxItem Content="Bei veränderten Daten"/>
```
Neue Checkbox direkt unter der Zeitplan-ComboBox (immer sichtbar, unabhängig vom gewählten
Zeitplan-Typ — gilt für jeden Lauf dieses Jobs):
```xml
<CheckBox x:Name="SettingsVerifyTargetCheckBox" Content="Vor jedem Lauf Ziel-Inhalt prüfen" Margin="0,0,0,14"/>
```
Keine eigene Vorlage nötig (`FolderCheckBox` im Dateiauswahl-Tab nutzt ebenfalls die
Standard-`CheckBox`-Optik ohne Kontrastproblem, da `Foreground` normal vom Fenster geerbt wird).

**`MainWindow.xaml.cs`**:
- `LoadSettings()` (`:556-568`): `SelectedIndex`-Switch bekommt `ScheduleType.OnChange => 6,`;
  `SettingsVerifyTargetCheckBox.IsChecked = job.VerifyTargetBeforeRun;` im Job-Zweig, `= false` im
  Entwurfs-Zweig (`:577-581`).
- `SettingsScheduleTypeCombo_SelectionChanged` (`:621-637`): **keine Änderung nötig** — Index 6
  fällt bei allen bestehenden `is 2 or 3 or 4 or 5`/`== 4`/`== 5`-Prüfungen automatisch durch
  (zeigt korrekt keine Zusatzfelder).
- `SettingsSave_Click` (`:673-681`): Switch bekommt `6 => ScheduleType.OnChange,`; der konstruierte
  `BackupJob` (`:732-746`) bekommt `VerifyTargetBeforeRun = SettingsVerifyTargetCheckBox.IsChecked == true,`.
- `DescribeNextRun` (`:99-137`): neuer Fall:
  ```csharp
  case ScheduleType.OnChange:
      return "Beim nächsten Programmstart, falls Änderungen";
  ```

## 11. Bewusst außen vor (YAGNI für diesen Wurf)

- Kein Inhalts-Hashing (Nutzerentscheidung, Abschnitt 2).
- Kein automatisches Aufräumen/Ablaufen der Quarantäne-Ordner — der Nutzer räumt bei Bedarf
  manuell auf.
- Kein laufender Datei-Watcher (`FileSystemWatcher`) für "Bei veränderten Daten" — nur der
  einmalige Programmstart-Check.
- Keine UI-Anzeige der Quarantäne-Historie innerhalb von SparkVault — der Ordner selbst
  (`_deleted\<Zeitstempel>\...`) ist über jeden Datei-Browser/FTP-Client einsehbar.
- `VerifyTargetBeforeRun` bleibt ein einfacher Vor-Lauf-Abgleich der Pfadliste
  (`ListExistingAsync`) — kein Abgleich von Größe/Zeit gegen den tatsächlichen Ziel-Inhalt (dafür
  müsste `RemoteFileInfo` um Zeitstempel erweitert werden, die nicht jeder Ziel-Typ zuverlässig
  liefert — spätere Iteration, falls gebraucht).

## 12. Testkonzept

- `IncrementalPlannerTests`: reine Unit-Tests ohne DB/Dateisystem — neue Datei, geänderte Datei
  (Größe gleich/Zeit anders, Größe anders/Zeit gleich), unveränderte Datei, gelöschte Datei
  (→ `ToQuarantine`), leerer `previousManifest` (Erstlauf), `previousManifest`-Eintrag mit
  `SourceModifiedUtc == null` (Alt-Zeile, muss immer in `ToUpload` landen).
- `BackupRunnerTests`: zweiter Lauf desselben Jobs ohne Quelländerung überträgt keine Bytes
  (`TotalBytes == 0`), aber `RunFileRepository.GetByRunId` liefert weiterhin den vollständigen
  Katalog; eine geänderte Datei wird erneut übertragen, eine gelöschte landet über
  `LocalTarget.MoveAsync` im Quarantäne-Unterordner und taucht nicht mehr im neuen Katalog auf.
- `LocalTargetTests`/`SftpTargetTests`/`FtpTargetTests`/`S3TargetTests`: je ein neuer
  `MoveAsync`-Test — hochladen, verschieben, prüfen dass die Datei am alten Pfad weg und am neuen
  Pfad mit identischem Inhalt vorhanden ist. FTP/SFTP/S3 Docker-gated wie die bestehenden Tests;
  der S3-Test ist gleichzeitig die in Abschnitt 7 geforderte Verifikation von
  `CopyObjectAsync`/`DeleteObjectAsync` gegen MinIO.
- `RestoreRunnerTests`: neuer Test — Lauf 1 sichert Datei A, Datei A wird aus der Quelle gelöscht,
  Lauf 2 quarantänisiert sie, `RestoreRunner.RestoreAsync(..., runId: <Lauf 1>)` stellt Datei A
  trotzdem korrekt wieder her (Fallback über `QuarantineRepository`).
- `OnChangeJobCheckerTests`: kein Vorlauf (Erstlauf) → gilt als "geändert"; identischer Zustand wie
  beim letzten erfolgreichen Lauf → kein Trigger; eine geänderte Datei → Trigger; Job ohne Targets
  → nie fällig.
- `ScheduleCalculatorTests`: `ScheduleType.OnChange` ist über `IsDue` nie fällig (deckt ab, dass der
  periodische `BackgroundScheduler`-Pfad diesen Typ korrekt ignoriert).
