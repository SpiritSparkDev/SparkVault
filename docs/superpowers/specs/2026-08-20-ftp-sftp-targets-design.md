# FTP/SFTP-Ziele – Design Spec

Datum: 2026-08-20
Basis: `Projektdokumentation-Backup-Tool.md` (v0.2 der Roadmap: "FTP/SFTP-Ziel, Zeitplan,
Tray-Icon") und [2026-08-19-sparkvault-mvp-design.md](2026-08-19-sparkvault-mvp-design.md) (MVP,
bereits umgesetzt auf Branch `feature/mvp`).

## 1. Zielsetzung

Der MVP unterstützt nur `LocalTarget`. Diese Erweiterung fügt zwei weitere Zieltypen hinzu –
FTP (inkl. FTPS/TLS) und SFTP – und stellt dabei die zugehörige Infrastruktur bereit
(Zugangsdaten-Verschlüsselung, mehrere Ziele pro Job). S3 folgt als eigener, kleinerer
Folge-Spec, der diese Infrastruktur wiederverwendet.

## 2. Entschiedene Grundsatzfragen

| Frage | Entscheidung | Begründung |
|---|---|---|
| Reihenfolge | Erst FTP/SFTP, S3 danach separat | Kleinere Schritte, S3 profitiert von der hier gebauten Infrastruktur |
| Branch-Basis | Neuer Branch direkt von `feature/mvp` (noch ungemergt) | Nutzer-Entscheidung, Merge nach `master` erfolgt später gemeinsam |
| Protokolle | SFTP, FTPS, reines FTP | Alle drei explizit gewünscht; FTP/FTPS über eine Library (FluentFTP) mit Verschlüsselungs-Option abgedeckt, SFTP separat (SSH.NET) |
| Credential-Speicher | DPAPI (`ProtectedData`), in der bestehenden SQLite-DB | Stdlib, keine neue Abhängigkeit, an den Windows-User gebunden |
| SFTP-Auth | Passwort **und** SSH-Key-Datei (+ optionale Passphrase) | Deckt beide gängigen Auth-Formen ab |
| Testing | Docker-Compose mit FTP-/SFTP-Testcontainern | Echte Integrationstests statt reiner Unit-Tests/Mocks |
| Ziel-Kardinalität | Mehrere Ziele pro Job (Liste statt einzelnem Pfad) | Nutzer-Entscheidung, vorgezogen aus Roadmap-Phase v0.5 |
| Log-Granularität | Ein `BackupRun`-Eintrag pro Ziel pro Lauf, verknüpft über `RunGroupId` | Zeigt Teilfehler bei Mehrfach-Zielen klar an |

## 3. Funktionsumfang dieser Erweiterung

**Drin:**
- Zieltypen `Ftp` (FTP/FTPS via FluentFTP) und `Sftp` (via SSH.NET), zusätzlich zu `Local`
- Mehrere Ziele pro Job (Hinzufügen/Entfernen/Bearbeiten in der UI)
- Zugangsdaten (Passwort, SSH-Key-Passphrase) verschlüsselt per DPAPI in SQLite
- `TestConnectionAsync` nutzt echten Verbindungs-/Login-Test
- Ein Log-Eintrag pro Ziel pro Lauf (`RunGroupId` gruppiert die Einträge eines Laufs)
- Temp-Name-Commit-Pattern auch für FTP/SFTP (soweit das Protokoll serverseitiges Rename erlaubt)

**Draußen (spätere Phasen):**
- S3 → eigener Folge-Spec
- Aufbewahrungsregeln, Benachrichtigungen → v0.3 laut Roadmap
- Inkrementell/Differenziell, Komprimierung, Verschlüsselung der Nutzdaten → v0.4
- Restore-UI, Installer → v0.5/später
- Parallele Uploads zu mehreren Zielen gleichzeitig (MVP: sequenziell, ein Lauf zur Zeit
  insgesamt, wie in der MVP-Spec per `SemaphoreSlim` bereits festgelegt)

## 4. Datenmodell

**BackupTarget** (neu, ersetzt `BackupJob.DestinationPath`)
- Id, JobId
- Type: `Local` | `Ftp` | `Sftp`
- Gemeinsame Felder je nach Typ:
  - `Local`: `DestinationPath`
  - `Ftp`: `Host`, `Port` (Standard 21), `Username`, `EncryptedPassword`, `EncryptionMode`
    (`None` | `Explicit` | `Implicit`), `RemotePath`
  - `Sftp`: `Host`, `Port` (Standard 22), `Username`, `EncryptedPassword` (optional),
    `PrivateKeyPath` (optional), `EncryptedKeyPassphrase` (optional), `RemotePath`
- Mindestens eine Auth-Methode bei SFTP erforderlich (Passwort oder Key-Datei); UI validiert das.

**BackupJob**
- `Targets: List<BackupTarget>` ersetzt das bisherige `DestinationPath`-Feld.
- Alle anderen Felder unverändert (Name, SourcePath, ExcludePatterns, ScheduleType,
  IntervalHours, DailyAtTime).

**BackupRun**
- Neues Feld `TargetId` (welches Ziel dieser Lauf betrifft).
- Neues Feld `RunGroupId` (Guid, gemeinsam für alle `BackupRun`-Zeilen eines
  Job-Ausführungs-Durchgangs) – ermöglicht der Log-Ansicht, "ein Klick = alle Ziele dieses
  Laufs" darzustellen.
- Alle anderen Felder unverändert (StartedAt, EndedAt, Status, FileCount, TotalBytes,
  ErrorMessage).

## 5. Credential-Speicher

Neue statische Klasse `CredentialProtector` in `SparkVault.Core`:
```csharp
public static class CredentialProtector
{
    public static string Protect(string plaintext);   // -> Base64(DPAPI-verschlüsselt)
    public static string Unprotect(string protectedValue);
}
```
Nutzt `System.Security.Cryptography.ProtectedData.Protect/Unprotect` mit
`DataProtectionScope.CurrentUser` (stdlib, Windows-only – für diese App unkritisch, da ohnehin
Windows-exklusiv). Passwörter/Passphrasen werden nie im Klartext geloggt (Serilog-Aufrufe dürfen
diese Felder nicht referenzieren) und nie im Klartext in SQLite gespeichert.

## 6. Ziel-Adapter

**FtpTarget** (`SparkVault.Core`, NuGet: `FluentFTP`)
- Implementiert `IBackupTarget` wie `LocalTarget`: Upload unter temporärem Namen, Verifikation
  über Dateigröße, serverseitiges Rename zum finalen Namen (`FtpClient.Rename`). Bei Abbruch
  während Upload: temporäre Remote-Datei wird gelöscht, bevor die Exception weitergereicht wird.
- `TestConnectionAsync`: Verbindungsaufbau + Login, `RemotePath`-Verzeichnis wird bei Bedarf
  angelegt (`CreateDirectory`, rekursiv).
- `EncryptionMode` steuert `FtpConfig.EncryptionMode` (`None`/`Explicit`/`Implicit`).

**SftpTarget** (`SparkVault.Core`, NuGet: `SSH.NET`)
- Gleiches Muster: Upload unter temporärem Namen (`SftpClient.UploadFile`), Verifikation über
  Dateigröße (`SftpClient.GetAttributes`), serverseitiges Rename (`SftpClient.RenameFile`) zum
  Commit, Cleanup bei Abbruch/Fehler.
- Authentifizierung: `PasswordAuthenticationMethod` und/oder
  `PrivateKeyAuthenticationMethod` (mit optionaler Passphrase), je nachdem was am
  `BackupTarget` konfiguriert ist – beide gleichzeitig möglich (SSH.NET probiert der Reihe
  nach).
- `TestConnectionAsync`: Verbindungsaufbau + Auth, `RemotePath` wird bei Bedarf angelegt.

Beide Adapter melden Fortschritt über das bestehende `IProgress<TransferProgress>` (Datei-Ebene,
wie `LocalTarget` – keine Byte-genaue Fortschrittsanzeige pro Datei nötig für diese Erweiterung).

## 7. BackupRunner-Änderungen

`RunAsync` ändert sich von "ein Ziel" zu "Liste von Zielen":
1. `RunStarted` feuert einmalig für den ganzen Job-Lauf (nicht pro Ziel).
2. Quelle wird **einmal** gescannt (`FileScanner.Scan`), Dateiliste wird für alle Ziele
   wiederverwendet.
3. Für jedes `BackupTarget` in `job.Targets` (sequenziell): eigener `BackupRun`-Eintrag
   (gleiche `RunGroupId`, jeweils eigene `TargetId`), `TestConnectionAsync` → Upload aller
   Dateien → Verifikation → Status/Log wie im MVP pro Ziel.
4. `RunCompleted` feuert einmalig nach Abschluss aller Ziele, mit dem aggregierten Status
   (`Success` nur wenn alle Ziele `Success` waren, sonst `Failed` – für die Tray-Anzeige/UI;
   die Einzel-Status bleiben in den jeweiligen `BackupRun`-Zeilen erhalten).
5. Ein Ziel-Fehlschlag bricht nicht die übrigen Ziele ab – jedes Ziel wird versucht, auch wenn
   ein vorheriges fehlgeschlagen ist.

Die bestehende `SemaphoreSlim`-Sperre (ein Lauf zur Zeit app-weit) bleibt unverändert – sie
sperrt den gesamten Job-Lauf (alle Ziele), nicht pro Ziel.

## 8. Job-Editor (UI)

Der bisherige einzelne Zielpfad-Bereich wird durch eine Ziel-Liste ersetzt:
- Liste der konfigurierten Ziele (Typ + Kurzbeschreibung, z.B. "SFTP: backup.example.com")
- "Ziel hinzufügen" öffnet einen Typ-Auswahl-Dialog (Local/FTP/SFTP), danach ein
  typspezifisches Unterformular:
  - Local: wie bisher (Ordner-Browser)
  - FTP: Host, Port, Username, Passwort, Verschlüsselung (Keine/Explizit/Implizit),
    Remote-Pfad, "Verbindung testen"-Button (ruft `TestConnectionAsync` auf)
  - SFTP: Host, Port, Username, Passwort und/oder Key-Datei (+ Passphrase), Remote-Pfad,
    "Verbindung testen"-Button
- Mindestens ein Ziel ist beim Speichern erforderlich (Validierung).
- Passwort-Felder sind maskierte Eingabefelder (`PasswordBox`), nie im Klartext im UI-State
  gehalten länger als zum Verschlüsseln nötig.

## 9. Fehlerbehandlung

- Netzwerkfehler (Server nicht erreichbar, Auth fehlgeschlagen) → Ziel-Lauf schlägt fehl,
  Fehlermeldung im jeweiligen `BackupRun`-Eintrag, andere Ziele werden trotzdem versucht.
- Kein automatisches Retry/Backoff in dieser Erweiterung (wie im MVP-Spec für lokale Ziele
  bereits so entschieden – bleibt für alle Zieltypen vorerst so, spätere Phase falls nötig).
- Verbindungsabbruch während Upload: temporäre Remote-Datei wird best-effort gelöscht (siehe
  Abschnitt 6); falls das Löschen selbst fehlschlägt (z.B. Verbindung bereits tot), wird das
  geloggt, aber der Ziel-Lauf bleibt als `Failed` markiert (kein zusätzlicher Fehlerzustand
  nötig – dieselbe Datei wird beim nächsten Lauf einfach überschrieben).

## 10. Testkonzept

- Unit-Tests: `CredentialProtector` (Roundtrip Protect/Unprotect), Datenmodell-Mapping
  (`BackupTarget` ↔ SQLite-Zeilen für alle drei Typen), Job-Editor-Validierung (mind. ein Ziel,
  SFTP mind. eine Auth-Methode).
- Integrationstests gegen Docker-Container (docker-compose, im Testprojekt orchestriert oder
  als dokumentierter manueller Vorlauf – Details im Implementierungsplan):
  - FTP-Testserver (z.B. `fauria/vsftpd` oder vergleichbar) – Upload, Verifikation,
    `TestConnectionAsync`, Abbruch-Szenario.
  - SFTP-Testserver (z.B. `atmoz/sftp`) – dieselben Szenarien, zusätzlich Key-Auth.
- Diese Integrationstests sind auf eine laufende Docker-Umgebung angewiesen; falls Docker beim
  Ausführen nicht verfügbar ist, werden sie übersprungen (nicht rot), damit `dotnet test` auf
  jeder Maschine lauffähig bleibt.

## 11. Referenz

MVP-Grundlage: [2026-08-19-sparkvault-mvp-design.md](2026-08-19-sparkvault-mvp-design.md).
Vollständige Anforderungen/spätere Phasen: `Projektdokumentation-Backup-Tool.md`.
