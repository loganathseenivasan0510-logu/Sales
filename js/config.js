/*
 * SalesofLoga – zentrale Konfiguration
 * -------------------------------------
 * Nur diese Datei muss für den Betrieb angepasst werden.
 * Siehe README.md (Abschnitt "SharePoint / SSO einrichten").
 */
window.APP_CONFIG = {
  appTitle: "Sales Dashboard – CT India",

  // Speicher-Backends, die in der Oberfläche angeboten werden.
  //   "localFolder" – Ordner auf dem PC (Chrome/Edge, File System Access API)
  //   "browser"     – Browser-Speicher (IndexedDB), nur dieser Browser/Rechner
  //   "sharepoint"  – Datei in einer SharePoint-Bibliothek via Microsoft Graph + SSO
  enabledStores: ["localFolder", "browser", "sharepoint"],

  // Stammdaten
  customers: ["TP Solar", "EMMVEE", "Premier", "Renew", "Websol", "Tata Bangalore", "Jupiter"],
  currencies: ["INR", "EUR", "USD"],
  defaultCurrency: "INR",
  statuses: {
    quotation: ["Open", "Closed"],
    po: ["Open", "In Progress", "Delivered", "Closed", "Cancelled"]
  },
  lowStockThreshold: 5,

  // Wer darf fremde Datensätze bearbeiten/löschen?
  // SharePoint-Modus: UPN (z. B. "max.mustermann@centrotherm.de"), lokal: voller Name.
  admins: [],

  maxAttachmentMB: 20,
  csvDelimiter: ";",

  // Dateiname der Datendatei (lokal und in SharePoint identisch)
  dataFileName: "salesofloga-data.json",

  sharepoint: {
    // Werte aus der Entra-App-Registrierung (siehe README)
    tenantId: "",            // Verzeichnis-ID (GUID) des Tenants
    clientId: "",            // Anwendungs-ID (GUID) der App-Registrierung
    // Redirect-URI muss 1:1 in der App-Registrierung (Typ "Single-page application") eingetragen sein
    redirectPath: "redirect.html",

    // Ziel in SharePoint
    siteHostname: "centrotherm.sharepoint.com",  // ohne https://
    sitePath: "/sites/CTIndiaSales",              // Server-relativer Pfad der Site
    libraryName: "",                              // leer = Standard-Dokumentbibliothek
    folderPath: "SalesofLoga",                    // Ordner in der Bibliothek
    driveId: "",                                  // optional: direkt angeben, spart den Site-Lookup

    // Delegierte Graph-Berechtigungen. Empfohlen: Sites.Selected – die App kommt dann NUR an die
    // Sites, für die IT sie per POST /sites/{id}/permissions freigegeben hat, und der Nutzer zusätzlich
    // nur an das, was er in SharePoint selbst darf. Keine weiteren *.All-Scopes ergänzen: die würden
    // Sites.Selected aushebeln. Fallback ohne Site-Freigabe: ["User.Read", "Sites.ReadWrite.All"].
    scopes: ["User.Read", "Sites.Selected"]
  }
};
