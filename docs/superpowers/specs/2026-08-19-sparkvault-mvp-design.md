# SparkVault MVP – Design Spec

Datum: 2026-08-19
Basis: `Projektdokumentation-Backup-Tool.md` (Volldokumentation, siehe Referenz unten)

## 1. Zielsetzung

SparkVault ist ein Windows-Backup-Tool, das Dateien/Ordner nach Zeitplan oder manuell
sichert. Langfristig auf drei Zieltypen ausgelegt (lokal/Netzlaufwerk, FTP/SFTP,
S3-kompatibel), aber dieser Spec beschreibt **nur den MVP**: erster lauffähiger Stand,
danach schrittweiser Ausbau gemäß Roadmap im Referenzdokument.

Zielgruppe: nicht nur der Entwickler selbst, sondern auch andere Nutzer – das
beeinflusst spätere Anforderungen an Fehlertoleranz/Installer, aber nicht den
MVP-Funktionsumfang selbst.

## 2. Entschiedene Grundsatzfragen

Diese vier Fragen waren im Referenzdokument (Abschnitt 15) offen und wurden im
Brainstorming geklärt:

| Frage | Entscheidung | Begründung |
|---|---|---|
| Umfang für ersten Spec/Plan | MVP zuerst | Kleinster Spec, schnellster lauffähiger Stand; Ziel-Interface aber von Anfang an erweiterbar geschnitten |
| Windows-Dienst oder Tray-App | Tray-App mit Autostart | Einfacher zu bauen/debuggen, keine erhöhten Rechte nötig |
| Zielgruppe | Auch andere Nutzer | Beeinflusst spätere Installer-/Fehlertoleranz-Anforderungen, nicht den MVP-Funktionsumfang |
| Tech-Stack | .NET 8 + WPF | Nativer Windows-Support, geringer Ressourcenverbrauch im Idle, etablierte Libs |

App-Titel: **SparkVault**.

## 3. MVP-Funktionsumfang

**Drin:**
- Jobs anlegen/bearbeiten/löschen. Mehrere Jobs werden von Anfang an unterstützt
  (Datenmodell trägt das ohnehin), aber Ausführung ist rein manuell.
- Nur Vollbackup (kein inkrementell/differenziell).
- Nur Ziel-Typ `Local` (lokaler Pfad oder UNC-Netzlaufwerk).
- Manueller Start eines Jobs per UI.
- Fortschrittsanzeige während des Laufs.
- Log je Lauf in SQLite (Start, Ende, Status, Dateianzahl, Datenmenge, Fehler).
- Tray-Icon mit Statusanzeige (idle / läuft / Fehler) und Schnellzugriff (Jetzt
  sichern, Öffnen, Beenden).
- Robustheit: Ziel wird unter temporärem Namen beschrieben, erst nach Verifikation
  (Dateigröße/Prüfsumme) final committed (umbenannt). Bei Abbruch bleibt kein
  halbfertiges Backup als "gültig" zurück.

**Draußen (spätere Phasen laut Roadmap im Referenzdokument):**
- Zeitplan/Scheduler → v0.2
- FTP/SFTP, S3 → v0.2 / v0.3
- Inkrementell/Differenziell → v0.4
- Komprimierung/Verschlüsselung → v0.4
- Restore-UI → v0.5
- Credential-Speicherung (Windows Credential Manager/DPAPI) → erst relevant sobald
  FTP/S3 dazukommt, MVP hat keine Zugangsdaten
- Installer (Inno Setup/MSIX) → erst wenn andere Nutzer tatsächlich testen sollen

## 4. Architektur

```
Tray-App + Hauptfenster (WPF)
        │
   Job-Engine          ← führt Jobs manuell aus (kein Scheduler im MVP)
        │
   Backup-Core          ← Dateiscan, Vollbackup-Kopie
        │
   IBackupTarget         ← Interface von Anfang an, MVP hat nur LocalTarget
        │
   SQLite (Config + Log)
```

`IBackupTarget` wird von Anfang an mit voller Signatur angelegt (wie im
Referenzdokument, Abschnitt 4):

```csharp
interface IBackupTarget
{
    Task<bool> TestConnectionAsync();
    Task UploadAsync(BackupFile file, IProgress<TransferProgress> progress, CancellationToken ct);
    Task<IEnumerable<RemoteFileInfo>> ListExistingAsync();
    Task DeleteAsync(string remotePath);
}
```

MVP implementiert nur `LocalTarget`. Die Job-Engine kennt ausschließlich das
Interface – `FtpTarget`/`S3Target` können in späteren Phasen ergänzt werden, ohne
Job-Engine oder Backup-Core anzufassen.

## 5. Datenmodell

**BackupJob**
- Id, Name
- Quellpfad, Ausschlussregeln (Dateitypen/Ordner/Muster – einfache Glob-Liste im MVP)
- Zielpfad (lokal/UNC)

**BackupTarget**
- Typ: im MVP nur `Local`
- Pfad

**BackupRun**
- JobId, Startzeit, Endzeit, Status (Erfolg/Fehler/Abgebrochen)
- Übertragene Dateianzahl, Datenmenge, Fehlermeldungen

Persistenz: SQLite (`Microsoft.Data.Sqlite`), eine lokale Datei unter
`%AppData%/SparkVault/`.

## 6. Ablauf eines Backup-Laufs

1. Nutzer startet Job manuell über Tray-Icon oder Hauptfenster.
2. Backup-Core scannt Quellpfad, wendet Ausschlussregeln an, ermittelt zu
   kopierende Dateien (MVP: immer Vollbackup, kein Änderungsvergleich).
3. Für jede Datei: `LocalTarget.UploadAsync()` kopiert unter temporärem Namen,
   meldet Fortschritt an UI.
4. Nach Kopie: Verifikation (Dateigröße vergleichen), dann finales Umbenennen.
5. Lauf wird in SQLite protokolliert (Status, Dauer, Datenmenge, Fehler).
6. Tray-Icon-Status aktualisiert sich (grün/rot), Hauptfenster zeigt letzten Lauf.

## 7. Fehlerbehandlung

- Abbruch während Kopie (Exception, Cancellation) → temporäre Datei wird nicht
  umbenannt/committed, Lauf wird als "Abgebrochen"/"Fehler" protokolliert.
- Zielpfad nicht erreichbar (Netzlaufwerk offline) → Lauf schlägt sofort fehl,
  klare Fehlermeldung im Log und UI (kein Retry/Backoff im MVP – das ist primär für
  FTP/S3-Netzwerkfehler relevant und kommt mit v0.2/v0.3).
- Strukturiertes Logging (`Serilog`) zusätzlich zur SQLite-Historie, für
  Support/Debugging als Textlog-Datei.

## 8. UI-Konzept (MVP)

- **Tray-Icon**: Status (idle/läuft/Fehler), Kontextmenü (Jetzt sichern je Job,
  Hauptfenster öffnen, Beenden).
- **Hauptfenster**: Job-Liste (Name, letzter Lauf, Status), Job-Editor (Name,
  Quellpfad, Ausschlussregeln, Zielpfad – kein Wizard nötig im MVP, einfaches
  Formular reicht), Log-Ansicht je Job.

Kein Wizard, keine Zeitplan-Konfiguration, keine Restore-Ansicht im MVP.

## 9. Testkonzept

- Unit-Tests: Dateiscan + Ausschlussregeln (Glob-Matching).
- Integrationstest `LocalTarget`: Kopie in Testverzeichnis, Verifikation.
- Abbruch-Test: Kopiervorgang während Lauf abbrechen (CancellationToken), prüfen
  dass Ziel keine als "fertig" markierte Halbkopie enthält.

## 10. Referenz

Vollständige Anforderungen, spätere Phasen und Technologie-Details:
`Projektdokumentation-Backup-Tool.md` (vom Nutzer bereitgestellt, Original bleibt
die Quelle für den Gesamtumfang jenseits des MVP).
