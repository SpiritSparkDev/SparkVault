# S3-Ziel – Design Spec

Datum: 2026-08-21
Basis: `Projektdokumentation-Backup-Tool.md` (v0.3 der Roadmap: "S3-Ziel, Aufbewahrungsregeln,
Benachrichtigungen") und [2026-08-20-ftp-sftp-targets-design.md](2026-08-20-ftp-sftp-targets-design.md)
(FTP/SFTP-Ziele, bereits umgesetzt und in `feature/mvp` gemergt).

## 1. Zielsetzung

Vierter Zieltyp neben Local/FTP/SFTP: S3-kompatibler Object Storage (AWS S3 und S3-kompatible
Anbieter wie Backblaze B2, MinIO, Hetzner, Wasabi über einen konfigurierbaren Custom-Endpoint).
Nutzt die mit den FTP/SFTP-Zielen bereits gebaute Infrastruktur (`IBackupTarget`,
`CredentialProtector`, `TargetFactory`, `BackupTargetRepository`, Multi-Target-Job-Modell,
`TargetEditorWindow`-Formularmuster) vollständig weiter – keine neue Infrastruktur nötig, nur ein
zusätzlicher Zieltyp.

## 2. Entschiedene Grundsatzfragen

| Frage | Entscheidung | Begründung |
|---|---|---|
| Anbieter | S3-kompatibel mit Custom Endpoint | Deckt AWS S3 (Endpoint leer) und S3-kompatible Anbieter (Endpoint gesetzt) mit einer Implementierung ab, wie in der ursprünglichen Projektdokumentation vorgesehen |
| Commit-Muster | Direkter `PutObject` + `HeadObject`-Verifikation | S3s `PutObject` ist bereits atomar (ein abgebrochener Request macht das Objekt nie sichtbar) – das Temp-Name-Rename-Muster von FTP/SFTP wäre unnötiger Zusatzaufwand für eine Garantie, die S3 schon liefert |
| Testing | MinIO-Docker-Container | Analog zu den FTP/SFTP-Testcontainern (`garethflowers/ftp-server`, `atmoz/sftp`) – echter Integrationstest, kein AWS-Konto nötig |
| Branch-Basis | Neuer Branch von `feature/mvp` (enthält FTP/SFTP bereits gemerged) | Gleiches Muster wie zuvor bei `feature/ftp-sftp-targets` |

Jede AWSSDK.S3-Aufrufsequenz in diesem Spec (Bucket-Existenzprüfung, `PutObject`,
`GetObjectMetadata`, `ListObjectsV2`, `DeleteObject`) wurde vom Autor dieses Specs gegen die real
installierte Paketversion `AWSSDK.S3` 4.0.102.3 verifiziert, inklusive eines echten Roundtrips
(Bucket anlegen, Objekt hochladen, Größe per Head verifizieren, auflisten, löschen, Löschung per
404 bestätigen) gegen einen laufenden `minio/minio`-Docker-Container.

## 3. Funktionsumfang dieser Erweiterung

**Drin:**
- Zieltyp `S3`, zusätzlich zu `Local`/`Ftp`/`Sftp`
- Access Key + Secret Key (verschlüsselt via `CredentialProtector`), Region, Bucket, Prefix
  (Bucket-relativer Unterordner, entspricht `RemotePath` bei FTP/SFTP), optionaler Custom Endpoint
- `TestConnectionAsync` prüft Bucket-Existenz, legt ihn bei Bedarf an
- Direkter Upload ohne Temp-Datei, Verifikation über `HeadObject`-Größenvergleich
- Docker-Integrationstests gegen MinIO, gleiches Skip-wenn-nicht-erreichbar-Muster wie
  `FtpTargetTests`/`SftpTargetTests`

**Draußen (spätere Phasen laut Roadmap):**
- Multipart-Upload für sehr große Dateien (>5 GB) – `PutObject` deckt bis 5 GB in einem Request ab,
  ausreichend für dieses MVP; Multipart ist eine spätere Erweiterung falls nötig
- Aufbewahrungsregeln, Benachrichtigungen → v0.3 laut Roadmap, eigener Folge-Spec
- Server-seitige Verschlüsselung (SSE) der hochgeladenen Objekte – optional, nicht Teil dieser
  Erweiterung (clientseitige Verschlüsselung ist ohnehin laut Roadmap erst v0.4)

## 4. Datenmodell

**BackupTarget** (Erweiterung um S3-Felder, analog zu den bestehenden FTP/SFTP-Feldern)
- `Endpoint` (string?, leer = echtes AWS S3, gesetzt = Custom-Endpoint für S3-kompatible Anbieter)
- `AccessKey` (string?)
- `EncryptedSecretKey` (string?)
- `Region` (string?, Pflichtfeld in der UI – auch S3-kompatible Anbieter erwarten meist einen Wert,
  ggf. Platzhalter wie `"us-east-1"`)
- `Bucket` (string?)

`Prefix` wird **nicht** als neues Feld angelegt, sondern das bereits vorhandene generische
`RemotePath`-Feld wiederverwendet (identische Semantik: "Unterordner innerhalb des Ziels").

**Kein neues `RunGroupId`/`TargetId`-Verhalten** – S3-Läufe verhalten sich in `BackupRunner`
identisch zu den anderen drei Zieltypen (ein `BackupRun`-Eintrag pro Lauf, `TestConnectionAsync`
vor dem Upload, gleiche Fehlerbehandlung).

## 5. S3Target

```csharp
Task<bool> TestConnectionAsync(ct)
    // Bucket-Existenz prüfen (GetBucketLocation, 404 abfangen), bei Bedarf PutBucket

Task UploadAsync(file, progress, ct)
    // PutObject direkt auf den finalen Key (Prefix + RelativePath, Backslash→Slash normalisiert),
    // danach GetObjectMetadata zur Größenverifikation. Kein Temp-Key, kein Rename – bei Abbruch
    // während PutObject bleibt einfach kein Objekt am Ziel-Key liegen (S3-Atomizität).

Task<IEnumerable<RemoteFileInfo>> ListExistingAsync(ct)
    // ListObjectsV2 mit Prefix-Filter

Task DeleteAsync(remotePath, ct)
    // DeleteObject
```

**Endpoint-Konfiguration:**
```csharp
var config = new AmazonS3Config
{
    ServiceURL = endpoint,               // nur gesetzt wenn Custom-Endpoint konfiguriert
    ForcePathStyle = !string.IsNullOrEmpty(endpoint),  // S3-kompatible Anbieter brauchen i.d.R.
                                          // Path-Style, echtes AWS S3 nutzt Virtual-Hosted-Style
    AuthenticationRegion = region,
};
```
`ForcePathStyle` ist bewusst an "Custom Endpoint gesetzt" gekoppelt: S3-kompatible Anbieter
(MinIO, Hetzner, Wasabi, Backblaze) verlangen praktisch immer Path-Style-Requests, während echtes
AWS S3 Path-Style für neue Buckets zunehmend einschränkt – die Kopplung an den Endpoint ist die
richtige Heuristik ohne zusätzliches UI-Feld.

Wie `FtpTarget`/`SftpTarget` implementiert `S3Target` `IBackupTarget` (inkl. `IAsyncDisposable` –
`AmazonS3Client` wird in `DisposeAsync` disposed) und normalisiert Pfade ausschließlich per
String-Konkatenation mit `/` (kein `Path.*`), gleiche Begründung wie bei den anderen Remote-Zielen.

## 6. Testkonzept

`docker-compose.test.yml` bekommt einen dritten Service:
```yaml
sparkvault-test-s3:
  image: minio/minio:latest
  command: server /data
  environment:
    MINIO_ROOT_USER: minioadmin
    MINIO_ROOT_PASSWORD: minioadmin
  ports:
    - "9000:9000"
```
`S3TargetTests` folgt exakt dem Muster von `FtpTargetTests`/`SftpTargetTests`: `DockerTestHelper.IsReachable`-Vorprüfung, echte Upload/Verify/List/Delete/TestConnection-Tests gegen den
laufenden Container, kein Fehlschlag wenn Docker nicht läuft (Test kehrt früh zurück).

Zusätzlich: ein Docker-gated Test in `BackupRunnerTests`, der `BackupRunner.RunAsync` gegen einen
Job mit allen vier Zieltypen zusammen (Local + FTP + SFTP + S3) laufen lässt – Analogon zum
entsprechenden Test aus dem FTP/SFTP-Plan.

## 7. UI (TargetEditorWindow)

Viertes Typ-Panel (S3): Endpoint (optional, Hinweistext "leer = AWS S3"), Access Key, Secret Key
(PasswordBox, gleiches "leer bei Bearbeiten = unverändert"-Muster wie FTP/SFTP-Passwörter), Region,
Bucket, Prefix (nutzt das bestehende `RemotePathBox`, keine neue XAML-Steuerung nötig), „Verbindung
testen".

## 8. Referenz

FTP/SFTP-Grundlage: [2026-08-20-ftp-sftp-targets-design.md](2026-08-20-ftp-sftp-targets-design.md).
MVP-Grundlage: [2026-08-19-sparkvault-mvp-design.md](2026-08-19-sparkvault-mvp-design.md).
Vollständige Anforderungen: `Projektdokumentation-Backup-Tool.md`.
