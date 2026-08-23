# Wiederherstellung (Restore) – Design Spec

Datum: 2026-08-23
Basis: `Projektdokumentation-Backup-Tool.md`, das laufende Claude-Design-UI-Redesign
(Sub-Projekt 5 von 7 — siehe Sitzungsverlauf), [2026-08-19-sparkvault-mvp-design.md](2026-08-19-sparkvault-mvp-design.md).

## 1. Zielsetzung

Ein Nutzer kann für einen Job einen früheren erfolgreichen Sicherungsstand (Version) auswählen
und die dort gesicherten Dateien zurück in den ursprünglichen Quellpfad spielen. Erstes,
funktionsfähiges Wiederherstellen — kein Restore-in-neuen-Ordner, keine Dateiauswahl innerhalb
eines Stands, kein Diff-Vorschau. Diese Erweiterungen sind spätere Iterationen, kein YAGNI-Verstoß
für den ersten Wurf.

## 2. Entschiedene Grundsatzfragen

| Frage | Entscheidung | Begründung |
|---|---|---|
| Datei-Manifest pro Lauf | Neue Tabelle `RunFiles`, befüllt bei jedem erfolgreichen Lauf | Nutzer hat sich explizit für echte Versionsauswahl entschieden (nicht nur "stelle den aktuellen Ziel-Stand wieder her") |
| Restore-Ziel-Ordner | Immer der ursprüngliche `BackupJob.SourcePath` (überschreibend) | Deckt sich mit dem Mockup ("Dateien werden an ihren ursprünglichen Ort zurückgespielt"); "Restore woanders hin" ist YAGNI für v1 |
| Ziel-Auswahl bei mehreren Targets | Dropdown nur sichtbar, wenn der Job >1 Ziel hat | Die meisten Jobs haben ein Ziel; ein Dropdown für den Regelfall wäre unnötige UI |
| Bestätigung vor dem Zurückspielen | Ja, MessageBox (Ja/Nein) — Restore überschreibt lokale Dateien | Destruktive Operation, gleiches Muster wie "Job wirklich löschen?" |
| Manifest nur bei Erfolg schreiben | Ja — nur `RunStatus.Success`-Läufe bekommen ein `RunFiles`-Manifest | Ein fehlgeschlagener/abgebrochener Lauf hat keinen vollständigen, wiederherstellbaren Stand |

Jede in diesem Spec verwendete Download-API-Aufrufsequenz (FluentFTP `DownloadStream`, SSH.NET
`DownloadFile`, AWSSDK.S3 `GetObjectAsync`) wurde vom Autor dieses Specs gegen die real
installierten Paketversionen (FluentFTP 54.2.0, SSH.NET 2026.0.0, AWSSDK.S3 4.0.102.3) verifiziert:
echter Upload+Download-Roundtrip gegen die laufenden Docker-Testcontainer (FTP, SFTP, MinIO),
Inhalt nach dem Download exakt geprüft.

## 3. Datenmodell

**Neue Tabelle `RunFiles`** (eine Zeile pro Datei pro erfolgreichem Lauf):
```sql
CREATE TABLE IF NOT EXISTS RunFiles (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    RunId INTEGER NOT NULL,
    RelativePath TEXT NOT NULL,
    Size INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_RunFiles_RunId ON RunFiles(RunId);
```
`RelativePath` speichert exakt den Wert, der schon an `UploadAsync` übergeben wurde — also
**inklusive** des Job-Ordner-Präfixes (`{JobFolder}\...`, siehe `BackupRunner.SanitizeForPath`).
Damit ist der Manifest-Eintrag identisch mit dem tatsächlichen Remote-Pfad; keine zweite
Pfad-Normalisierung nötig. Beim Zurückspielen wird das Präfix wieder abgeschnitten (Abschnitt 5).

**Neues Record** in `Models.cs`:
```csharp
public sealed record RunFileRecord(string RelativePath, long Size);
```
Kein `Id`/`RunId` im C#-Modell — beide sind reine Speicherdetails, nirgends im Code gebraucht
(gleiches Muster wie `BackupFile`/`RemoteFileInfo`, die auch keine DB-Ids tragen).

**Neues Repository** `RunFileRepository`:
```csharp
public int[] AddRange(int runId, IEnumerable<RunFileRecord> files) // ein Insert-Batch, eine Connection
public List<RunFileRecord> GetByRunId(int runId)
```

## 4. Manifest befüllen (`BackupRunner`)

In `RunForTargetAsync`'s Datei-Schleife wird zusätzlich zum bestehenden `done++`/`bytesDone +=`
jede erfolgreich hochgeladene Datei in einer lokalen `List<RunFileRecord>` gesammelt. **Nach**
dem Schleifenende, nur wenn `run.Status == RunStatus.Success`, wird die Liste als ein Batch via
`RunFileRepository.AddRange(run.Id, files)` geschrieben — nicht pro Datei, um bei tausenden
Dateien nicht tausende Einzel-Inserts zu erzeugen.

## 5. `IBackupTarget` — neue Methode `DownloadAsync`

```csharp
Task DownloadAsync(string remotePath, string localDestinationPath, CancellationToken ct);
```
Lädt die Datei am `remotePath` (identische Bedeutung wie bei `UploadAsync`/`DeleteAsync` — der
volle, Job-Ordner-präfigierte Pfad) herunter und schreibt sie nach `localDestinationPath` (ein
absoluter lokaler Dateisystempfad). Legt das Zielverzeichnis an, falls nötig. Kein Progress-Reporting
in dieser Methode (Restore meldet Fortschritt pro abgeschlossener Datei, wie beim Upload).

**LocalTarget** (`src/SparkVault.Core/LocalTarget.cs`):
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

**FtpTarget** (`src/SparkVault.Core/FtpTarget.cs`) — verifiziert: `AsyncFtpClient.DownloadStream(Stream, string)` gibt `bool` zurück:
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

**SftpTarget** (`src/SparkVault.Core/SftpTarget.cs`) — verifiziert: `SftpClient.DownloadFile(string, Stream)` ist synchron (kein `Async`-Suffix in SSH.NET), gleiches `Task.Run`-Wrapping-Muster wie die bestehenden Methoden dieser Klasse:
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

**S3Target** (`src/SparkVault.Core/S3Target.cs`) — verifiziert: `GetObjectAsync(GetObjectRequest)` liefert eine Response mit `.ResponseStream`:
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

## 6. Restore-Orchestrierung — neue Klasse `RestoreRunner`

Eigene Klasse statt Erweiterung von `BackupRunner`: andere Richtung (Download statt Upload),
andere Eingabe (ein `runId` statt ein frisch gescannter Dateibaum), kein Multi-Target-Loop nötig
(Restore läuft immer gegen genau ein gewähltes Ziel). Teilt sich `TransferProgress` mit
`BackupRunner` (gleiche Fortschritts-UI-Anzeige weiterverwendbar), aber `CurrentTarget` wird beim
Restore immer auf die eine gewählte Ziel-Beschreibung gesetzt.

```csharp
public sealed class RestoreRunner
{
    public RestoreRunner(RunFileRepository runFileRepository, ILogger logger);

    public async Task RestoreAsync(
        BackupJob job, BackupTarget targetConfig, int runId,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var files = _runFileRepository.GetByRunId(runId);
        await using var target = TargetFactory.Create(targetConfig);

        if (!await target.TestConnectionAsync(ct))
            throw new IOException($"Ziel nicht erreichbar: {targetConfig.Describe()}");

        var jobFolder = SanitizeForPath(job.Name); // identische Logik wie BackupRunner
        long totalBytes = files.Sum(f => f.Size);
        int done = 0; long bytesDone = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));

            var strippedPrefix = jobFolder + "\\";
            var originalRelative = file.RelativePath.StartsWith(strippedPrefix, StringComparison.Ordinal)
                ? file.RelativePath[strippedPrefix.Length..]
                : file.RelativePath;
            var localDestination = Path.Combine(job.SourcePath, originalRelative);

            await target.DownloadAsync(file.RelativePath, localDestination, ct);
            done++; bytesDone += file.Size;
            progress?.Report(new TransferProgress(done, files.Count, bytesDone, totalBytes, file.RelativePath, targetConfig.Describe()));
        }

        _logger.Information("Restore für Job {JobName} von Lauf {RunId} abgeschlossen: {FileCount} Dateien",
            job.Name, runId, done);
    }
}
```
`SanitizeForPath` wird `internal static` (statt `private static`) in `BackupRunner`, damit
`RestoreRunner` sie mitbenutzen kann — identische Job-Ordner-Sanitisierung darf nicht an zwei
Stellen leicht unterschiedlich implementiert werden.

`RestoreRunner` schreibt **keine** `BackupRun`/`RunFiles`-Zeilen — ein Restore-Vorgang ist kein
Backup-Lauf und soll nicht in "Letztes Backup erfolgreich" o.ä. auftauchen. Fehler werden als
Exception nach oben gereicht; die UI fängt sie und zeigt eine Fehlermeldung (kein eigenes
Statusmodell für Restores in v1).

## 7. UI — neue "Wiederherstellung"-Registerkarte in `JobDashboardWindow`

Sidebar-Reihenfolge: Übersicht, Dateiauswahl, Verlauf, **Wiederherstellung**, Einstellungen
(passt zur Mockup-Reihenfolge).

- Ziel-Dropdown, nur sichtbar wenn `job.Targets.Count > 1` — Standardauswahl: erstes Ziel.
- Versionsliste (links, wie im Mockup): alle `BackupRun`-Zeilen mit `Status == Success` für das
  gewählte Ziel, neueste zuerst, Radio-Auswahl. Label je Zeile: `{StartedAt:g}`.
- Detailbereich (rechts): gewählter Stand — Datum, Dateianzahl, Größe (aus dem `RunFiles`-Manifest
  bzw. dem `BackupRun.FileCount`/`TotalBytes`, die schon vorhanden sind), Hinweistext "Dateien
  werden an ihren ursprünglichen Ort zurückgespielt.", Button "Wiederherstellen"
  (`PrimaryButtonStyle`).
- Klick auf "Wiederherstellen": Bestätigungsdialog (`MessageBox`, Ja/Nein, Warnsymbol) — bei Ja:
  Fortschrittsanzeige (wiederverwendet das gleiche Progress-Muster wie die Übersicht-Running-View:
  Prozent, Fortschrittsbalken, aktuelle Datei — ohne Pause/Fortsetzen, das ist für Restore nicht
  angefragt), Button "Abbrechen" während des Laufs.
- Kein Eintrag, falls der Job noch keinen erfolgreichen Lauf hat: Leerzustand-Hinweis
  "Noch keine Sicherung zum Wiederherstellen vorhanden."

## 8. Testkonzept

- `RunFileRepositoryTests`: `AddRange`+`GetByRunId`-Roundtrip (mehrere Dateien, exakte Werte).
- `BackupRunnerTests`: neuer Test — nach einem erfolgreichen Lauf sind die hochgeladenen Dateien
  über `RunFileRepository.GetByRunId(run.Id)` abrufbar; ein Test, der einen fehlgeschlagenen Lauf
  simuliert (z. B. nicht erreichbares Ziel) und bestätigt, dass **kein** Manifest geschrieben wird.
- `LocalTargetTests`/`FtpTargetTests`/`SftpTargetTests`/`S3TargetTests`: je ein neuer
  `DownloadAsync`-Test — hochladen, herunterladen (an einen anderen lokalen Pfad), Byte-für-Byte-
  Vergleich mit der Originaldatei. FTP/SFTP/S3-Varianten Docker-gated wie die bestehenden Tests.
- `RestoreRunnerTests`: voller Roundtrip — Job mit Local-Ziel anlegen, `BackupRunner.RunAsync`
  ausführen, Quelldateien danach löschen/verändern, `RestoreRunner.RestoreAsync` ausführen,
  bestätigen dass die Originaldateien wieder exakt da sind.

## 9. Referenz

Vorherige Design-Spec dieser Session: [2026-08-21-s3-target-design.md](2026-08-21-s3-target-design.md)
(gleiches Verifikations-Vorgehen für externe Bibliotheks-APIs).
