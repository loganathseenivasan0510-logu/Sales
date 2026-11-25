# SalesofLoga Desktop – Blazor + SQLite als einzelne EXE

`SalesofLoga.exe` ist die lokale Variante des Sales Dashboards: Doppelklick → die App startet einen Webserver
**nur auf 127.0.0.1** und öffnet sich in Edge im App-Fenster. Keine Installation, kein .NET nötig, keine Fremdbibliotheken.

| Thema | Umsetzung |
| --- | --- |
| UI | Blazor (Interactive Server, .NET 10 LTS), centrotherm-Design |
| Daten | **eine SQLite-Datei** `salesofloga.db` – Anhänge (PDF/Bilder) als BLOB in derselben Datei |
| SQLite-Engine | `winsqlite3.dll` aus Windows 10/11 (P/Invoke) – keine mitgelieferte DLL, Updates über Windows Update |
| Identität | angemeldeter Windows-Benutzer (Name/UPN aus AD bzw. Entra ID) – keine Eingabe, nicht fälschbar über die UI |
| Rechte | eigene Datensätze bearbeiten; Admins (Settings) alles; Löschen = Soft Delete; Audit-Tabelle |
| Logging | `%LOCALAPPDATA%\SalesofLoga\logs\salesofloga-YYYYMMDD.log` (14 Tage), Anzeige unter *Settings → Log* |
| Sicherheit | Security-Check-Seite (26 Prüfungen, Admin-Button) + externe Testskripte, Berichte als HTML |

## 1. Starten

1. `SalesofLoga.exe` in einen beliebigen Ordner kopieren und doppelklicken.
2. Beim ersten Start legt die App an:
   ```
   %LOCALAPPDATA%\SalesofLoga\
     salesofloga.db        Datenbank (SQLite)
     backups\              tägliche Sicherung, 30 Stück
     logs\                 Logdateien
     reports\              Security-Check-Berichte
   ```
3. Fenster schließen → die App beendet sich nach 90 Sekunden von selbst (oder *Settings → Quit SalesofLoga*).
   Ein zweiter Doppelklick öffnet die bereits laufende Instanz.

> **SmartScreen:** Die EXE ist nicht signiert. Für die Verteilung im Unternehmen per Code-Signing-Zertifikat signieren
> (`signtool sign /fd SHA256 /a SalesofLoga.exe`) oder über Intune als Win32-App bereitstellen.

### Kommandozeile

| Option | Zweck |
| --- | --- |
| `--data "D:\Pfad"` | anderer Datenordner (nur lokale Laufwerke – **kein** Netzlaufwerk/OneDrive-Ordner, siehe unten) |
| `--import "Ordner"` | Daten der HTML-Version übernehmen (`salesofloga-data.json` + `attachments\`) |
| `--security-check --report bericht.html` | alle 26 Sicherheitsprüfungen ohne Oberfläche, Exit-Code 0 = bestanden |
| `--selftest` | Datenbank-Selbsttest auf diesem PC (winsqlite3, BLOB, Backup) |
| `--no-browser` / `--port 5123` | für Tests/Automatisierung |

## 2. Daten aus der HTML-Version übernehmen

*Settings → Import from the HTML version* → Ordner mit `salesofloga-data.json` angeben → **Import**.
- Bereits vorhandene Datensätze (gleiche ID) werden übersprungen.
- Jeder Anhang durchläuft die Signaturprüfung (z. B. eine als `.pdf` umbenannte `.exe` wird abgelehnt und protokolliert).
- Ersteller, Zeitstempel und Audit-Einträge werden übernommen.

## 3. Die SQLite-Datei

Die `.db` ist eine Standard-SQLite-Datenbank (`application_id` = `0x534C4741`), lesbar z. B. mit *DB Browser for SQLite*.

| Tabelle / View | Inhalt |
| --- | --- |
| `quotations`, `po_germany`, `po_customer`, `stock` | Datensätze inkl. `created_*`, `updated_*`, `deleted_*` |
| `attachments` | Dateien als BLOB + SHA-256, MIME (aus Signatur), Uploader |
| `audit` | jede Änderung mit Benutzer, Zeit und geänderten Feldern (JSON) |
| `settings` | Stammdaten (Kunden, Status, Admins, Schwellen) |
| `v_quotations`, `v_po_germany`, … | lesbare Sichten (Beträge in Hauptwährung, nur aktive Datensätze) |

Beträge werden exakt als Ganzzahl in Paise/Cent gespeichert (`value_minor`). Eine spätere Migration in SQL Server/PostgreSQL
ist damit direkt möglich (gleiche Tabellen, BLOBs → `varbinary`/`bytea`).

> ⚠️ **Nicht in OneDrive/SharePoint-synchronisierte Ordner oder Netzlaufwerke legen.** SQLite-Sperren funktionieren dort nicht
> zuverlässig – bei gleichzeitiger Nutzung droht Datenverlust. Mehrbenutzerbetrieb = zentrale Datenbank (Roadmap).

## 4. Sicherheit prüfen und vorzeigen

### In der App – Seite „Security Check“
*Security Check* im Menü → **Run security check** (Button nur für Admins). Ergebnis nach OWASP Top 10 mit Erwartet/Ist,
**Open report** bzw. **Download HTML** für die Präsentation. Jeder Lauf landet im Audit-Log und unter `reports\`.

Ohne konfigurierte Admins ist der lokale Benutzer Admin (Einzelplatz). Sobald unter *Settings → Master data → Administrators*
Konten eingetragen sind, sehen nur diese den Button.

### Als Skript (Black-Box von außen)
```powershell
# im entpackten Repository
powershell -ExecutionPolicy Bypass -File tests\security\Invoke-SecurityTests.ps1 -ExePath .\publish\SalesofLoga.exe -Open
```
Startet die EXE mit einem Wegwerf-Datenordner und präparierten Angriffsdaten, prüft 20 Punkte von außen
(Ports, Dateirechte, HTTP, XSS, CSV-Injection, SQL-Injection, Upload, Backup, Logging) und erzeugt
`security-tests-external.html` + den Bericht des eingebauten Checks. Echte Daten bleiben unberührt.

### Automatisch bei jedem Build
GitHub Actions (`.github/workflows/desktop.yml`) baut die EXE auf Windows und führt Selbsttest, beide Sicherheitstests und
die UI-Tests (Playwright) aus. Die Berichte liegen als Artefakt `test-reports` am Workflow-Lauf.

## 5. Selbst bauen

```cmd
desktop\build.cmd
```
Voraussetzung: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Ergebnis: `publish\SalesofLoga.exe` (self-contained, single file).

## Doku-Links
- [Blazor-Rendermodi (.NET 10)](https://learn.microsoft.com/aspnet/core/blazor/components/render-modes)
- [Single-File-Deployment](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)
- [SQLite – Dateiformat und `application_id`](https://www.sqlite.org/fileformat.html)
- [SQLite über Netzlaufwerke](https://www.sqlite.org/useovernet.html)
- [OWASP Top 10 (2021)](https://owasp.org/Top10/)
