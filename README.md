# SalesofLoga – Sales Dashboard CT India

Erfassung und Suche von **Quotations**, **Customer POs**, **German POs** und **Lagerbestand (CT India Stocks)**,
inklusive PDF-/Bild-Anhängen. Reines HTML/CSS/JavaScript, kein Build-Schritt, keine externen CDNs.

Drei austauschbare Speicher-Backends, wählbar in der Oberfläche:

| Backend | Wo liegen die Daten? | Mehrbenutzer | Voraussetzung |
| --- | --- | --- | --- |
| **Local folder** | `salesofloga-data.json` + `attachments/` in einem frei wählbaren Ordner, Tages-Backup in `backups/` | nein (1 Person) | Edge oder Chrome |
| **Browser storage** | IndexedDB dieses Browsers | nein | jeder moderne Browser |
| **SharePoint** | dieselbe Struktur in einer SharePoint-Bibliothek, Zugriff über Microsoft Graph | **ja** (eTag-Konflikterkennung) | Entra-App-Registrierung, Webserver |

Daten lassen sich jederzeit über **Settings → Copy all data to…** inkl. Anhängen in ein anderes Backend kopieren
(z. B. lokal → SharePoint).

---

## 1. Schnellstart lokal (ohne IT)

1. Ordner entpacken.
2. `index.html` per Doppelklick in **Edge** oder **Chrome** öffnen.
3. **Local folder** wählen → **Choose folder…** → Zielordner auswählen → Schreibzugriff erlauben.
4. Vollständigen Vor- und Nachnamen eingeben (wird an jedem Datensatz gespeichert).

Beim nächsten Öffnen fragt der Browser ggf. erneut nach der Ordnerfreigabe (**Allow folder access**).

> Hinweis: Lokal ist der Name nicht verifiziert. Für nachvollziehbare Zuordnung → SharePoint-Modus mit SSO.

---

## 2. SharePoint / SSO einrichten

### Checkliste

- [ ] SharePoint-Site + Dokumentbibliothek festgelegt (Versionierung aktiv lassen)
- [ ] App-Registrierung in Entra ID angelegt (Typ **Single-page application**)
- [ ] Redirect-URI(s) eingetragen (`…/redirect.html`)
- [ ] API-Berechtigungen gesetzt + Admin-Consent erteilt
- [ ] App auf die Site freigegeben (bei `Sites.Selected`)
- [ ] `js/config.js` ausgefüllt
- [ ] App auf einem Webserver bereitgestellt (http**s**; `localhost` geht für Tests)
- [ ] Test mit zwei Benutzern: Anlegen, Bearbeiten eigener/fremder Datensätze, gleichzeitiges Speichern

### 2.1 App-Registrierung (Entra Admin Center)

1. **Entra ID → App registrations → New registration**
   - Name: `SalesofLoga`
   - Supported account types: *Accounts in this organizational directory only (Single tenant)*
   - Redirect URI: Plattform **Single-page application (SPA)**, z. B.
     - Test: `http://localhost:8080/redirect.html`
     - Produktiv: `https://<ihr-host>/salesofloga/redirect.html`
2. **Overview**: *Application (client) ID* und *Directory (tenant) ID* notieren → `config.js`.
3. **Authentication**: keine Implicit-Grant-Häkchen setzen (MSAL nutzt Auth-Code-Flow mit PKCE).
4. **API permissions → Add → Microsoft Graph → Delegated**:
   - `User.Read`
   - `Sites.Selected` (empfohlen) – **keine** zusätzlichen `*.All`-Scopes, sie würden die Einschränkung aushebeln
   - → **Grant admin consent for <Tenant>**
5. **Enterprise applications → SalesofLoga → Properties**: *Assignment required = Yes* und die berechtigte Gruppe
   (z. B. `SG-SalesofLoga-Users`) unter *Users and groups* zuweisen.

Doku: [SPA-App registrieren](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-spa-app-registration) ·
[Graph-Berechtigungen](https://learn.microsoft.com/en-us/graph/permissions-reference) ·
[Selected-Berechtigungen](https://learn.microsoft.com/en-us/graph/permissions-selected-overview)

### 2.2 App auf die Site freigeben (`Sites.Selected`)

Mit `Sites.Selected` hat die App zunächst Zugriff auf **keine** Site. Ein SharePoint-/Global-Admin gibt sie gezielt frei
(z. B. im Graph Explorer, angemeldet mit Admin-Rechten):

```http
GET https://graph.microsoft.com/v1.0/sites/<tenant>.sharepoint.com:/sites/CTIndiaSales?$select=id

POST https://graph.microsoft.com/v1.0/sites/<site-id>/permissions
Content-Type: application/json

{
  "roles": ["write"],
  "grantedTo": { "application": { "id": "<client-id>", "displayName": "SalesofLoga" } }
}
```

Wirksam ist dann die **Schnittmenge**: Ein Benutzer kann nur schreiben, wenn (a) die App für die Site freigegeben ist
**und** (b) er selbst in SharePoint Schreibrechte auf die Bibliothek hat.

Einfachere Alternative ohne Site-Freigabe: `scopes: ["User.Read", "Sites.ReadWrite.All"]` – der Benutzer kommt dann
an alle Sites, auf die er ohnehin Rechte hat (delegiert), die App ist aber nicht auf eine Site begrenzt.

### 2.3 `js/config.js`

```js
sharepoint: {
  tenantId: "<Directory (tenant) ID>",
  clientId: "<Application (client) ID>",
  redirectPath: "redirect.html",
  siteHostname: "<tenant>.sharepoint.com",
  sitePath: "/sites/CTIndiaSales",
  libraryName: "",          // leer = Standardbibliothek ("Dokumente"/"Documents")
  folderPath: "SalesofLoga",
  driveId: "",              // optional, spart den Site-Lookup
  scopes: ["User.Read", "Sites.Selected"]
}
```

Außerdem: `admins` (UPNs, die fremde Datensätze ändern dürfen), `customers`, `statuses`, `currencies`.

### 2.4 Bereitstellen

`file://` reicht für SharePoint nicht (MSAL braucht http/https).

- **Test:** `start-local-server.ps1` (Rechtsklick → *Mit PowerShell ausführen*) → `http://localhost:8080/`
- **Produktiv** (Beispiele): interner IIS, Azure Static Web Apps, Azure App Service.
  Anforderungen: HTTPS, `redirect.html` **ohne** `Cross-Origin-Opener-Policy`-Header ausliefern (MSAL v5 Redirect-Bridge),
  Dateien unverändert inkl. `vendor/`.

Hinweis: Eine SharePoint-Bibliothek selbst liefert `.html` nicht als Webseite aus – die App braucht einen Webserver,
die **Daten** liegen in SharePoint.

Doku: [MSAL.js Redirect-Bridge (v5)](https://github.com/AzureAD/microsoft-authentication-library-for-js/blob/dev/lib/msal-browser/docs/redirect-bridge.md) ·
[Datei hochladen/ersetzen (If-Match)](https://learn.microsoft.com/en-us/graph/api/driveitem-put-content)

---

## 3. Funktionsweise

### Datenmodell (`salesofloga-data.json`)

```json
{
  "app": "SalesofLoga", "schemaVersion": 1,
  "quotations": [ { "id": "…", "qDate": "2026-11-20", "qNo": "QT-2026-001", "customer": "EMMVEE",
                    "status": "Open", "value": 12500.5, "currency": "INR",
                    "attachment": { "path": "attachments/quotations/QT-2026-001__a1b2c3d4.pdf", "name": "Angebot.pdf", "size": 48211, "type": "application/pdf" },
                    "createdAt": "…", "createdBy": { "id": "aad:<objectId>", "name": "Daniel König" },
                    "updatedAt": "…", "updatedBy": { … }, "deletedAt": null } ],
  "poGermany": [], "poCustomer": [], "stock": [],
  "audit": [ { "ts": "…", "user": { … }, "action": "update", "entity": "quotation", "recordId": "…",
               "label": "Quotation QT-2026-001", "changes": { "status": ["Open", "Closed"] } } ]
}
```

### Eigene Datensätze pflegen

- Jeder Datensatz trägt `createdBy` (SharePoint: Entra-Objekt-ID + Name aus dem SSO-Token).
- **Bearbeiten/Löschen** ist nur für den Ersteller oder Admins (`config.admins`) möglich, fremde Datensätze sind *read-only*.
- Löschen ist ein **Soft Delete** (`deletedAt`/`deletedBy`) – nichts geht verloren, das Audit-Log protokolliert jede Änderung feldgenau.
- ⚠️ Die Eigentümer-Prüfung erfolgt im Client. Wer Schreibrechte auf die Bibliothek hat, könnte die JSON-Datei theoretisch
  direkt ändern (sichtbar in der SharePoint-Versionshistorie). Für **serverseitig erzwungene** Rechte pro Datensatz → Abschnitt 4.

### Gleichzeitiges Speichern

Jede Änderung läuft als *lesen → anwenden → schreiben mit Versions-Check* (`StoreCommon.commit`):
SharePoint per `If-Match: <eTag>` (HTTP 412 bei Konflikt), lokal per Zeitstempel/Größe, Browser per Transaktion.
Bei Konflikt wird automatisch neu gelesen und die Änderung erneut angewendet (bis zu 4 Versuche) – Einträge von Kollegen gehen nicht verloren.

### Sicherheit

- Kein `innerHTML` mit Daten (Schutz vor XSS), strikte Content-Security-Policy, keine Inline-Skripte
- MSAL lokal eingebunden (`vendor/`, v5.30.0, MIT-Lizenz) – kein CDN
- Tokens nur im `sessionStorage`, delegierte Rechte, empfohlen `Sites.Selected`
- CSV-Export mit Schutz gegen Formel-Injection in Excel
- Validierung: Pflichtfelder, eindeutige Nummern, Beträge ≥ 0 mit max. 2 Nachkommastellen, Dateityp/-größe

---

## 4. Roadmap: Umstieg auf eine Datenbank

Die Oberfläche spricht nur mit der Store-Schnittstelle (`js/storage/store-common.js`). Ein DB-Backend ist ein weiterer Store:

1. **SharePoint-Liste je Bereich** (Quotations, POs, Stock): Item-Level-Berechtigungen („Benutzer dürfen nur eigene Elemente bearbeiten“), Versionierung, Power BI-fähig. Store über Graph `/sites/{id}/lists/{id}/items`.
2. **Dataverse / Power Apps**: Zeilenbasierte Sicherheit, Auditing out of the box.
3. **Eigene API + Azure SQL**: Azure Function/App Service, Entra-geschützt (On-Behalf-Of), Rechteprüfung serverseitig.

Migration: neuen Store implementieren → **Settings → Copy all data to…** nutzt dieselbe Schnittstelle.

---

## 5. Projektstruktur

```
index.html              App-Shell, CSP
redirect.html           MSAL v5 Redirect-Bridge (SSO)
css/app.css             Corporate Design (Rot #C90C0E)
js/config.js            ← Konfiguration
js/schema.js            Felder/Validierung je Bereich (neue Felder nur hier)
js/app.js               UI-Logik
js/auth.js              Entra ID / MSAL
js/storage/*.js         Speicher-Backends (lokal, Browser, SharePoint)
assets/                 Logo, Favicons
vendor/                 msal-browser + Redirect-Bridge (MIT)
start-local-server.ps1  Test-Webserver für Windows
```
