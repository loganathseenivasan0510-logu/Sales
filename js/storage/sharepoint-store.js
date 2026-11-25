/*
 * SharePointStore – Datendatei + Anhänge in einer SharePoint-Dokumentbibliothek,
 * Zugriff über Microsoft Graph mit dem Token des angemeldeten Nutzers (SSO, delegiert).
 *
 *   <Bibliothek>/<folderPath>/salesofloga-data.json
 *   <Bibliothek>/<folderPath>/attachments/<bereich>/<datei>
 *
 * Mehrbenutzer-Sicherheit: Jede Änderung wird mit "If-Match: <eTag>" geschrieben.
 * Hat jemand anders zwischenzeitlich gespeichert, antwortet Graph mit 412 –
 * StoreCommon.commit() liest dann neu und wendet die Änderung erneut an.
 * Versionierung der Bibliothek (Standard in SharePoint Online) ergibt zusätzlich
 * eine vollständige Versionshistorie der Datendatei.
 */
(function () {
  "use strict";
  const C = window.StoreCommon;
  const GRAPH = "https://graph.microsoft.com/v1.0";

  const enc = (path) => path.split("/").filter(Boolean).map(encodeURIComponent).join("/");

  class SharePointStore {
    /**
     * @param cfg       APP_CONFIG
     * @param getToken  async () => accessToken  (aus auth.js; im Test austauschbar)
     */
    constructor(cfg, getToken) {
      this.id = "sharepoint";
      this.cfg = cfg.sharepoint;
      this.fileName = cfg.dataFileName;
      this.getToken = getToken;
      this.driveId = this.cfg.driveId || null;
      this.itemId = null;
      this.siteName = "";
    }

    static isConfigured(cfg) {
      const s = cfg.sharepoint || {};
      return !!(s.tenantId && s.clientId && (s.driveId || (s.siteHostname && s.sitePath)));
    }

    describe() {
      return { label: "SharePoint", detail: `${this.siteName || this.cfg.sitePath}/${this.cfg.folderPath}` };
    }

    async _graph(path, opts) {
      const o = opts || {};
      for (let attempt = 0; attempt < 4; attempt++) {
        const token = await this.getToken();
        const headers = Object.assign({ Authorization: `Bearer ${token}` }, o.headers || {});
        const res = await fetch(path.startsWith("http") ? path : GRAPH + path, { method: o.method || "GET", headers, body: o.body });
        if ((res.status === 429 || res.status === 503 || res.status === 504) && attempt < 3) {
          const wait = Math.min(Number(res.headers.get("Retry-After") || 2), 30) * 1000;
          await new Promise((r) => setTimeout(r, wait));
          continue;
        }
        return res;
      }
      throw new Error("Microsoft Graph is not responding (throttled).");
    }

    async _json(res, what) {
      if (res.ok) return res.json();
      let msg = `${res.status}`;
      try { const j = await res.json(); msg = `${res.status} ${j.error && (j.error.code + ": " + j.error.message)}`; } catch (e) { /* ignore */ }
      if (res.status === 401 || res.status === 403) throw new C.PermissionError(`No permission for ${what} (${msg}). Ask IT to grant access to the SharePoint site.`);
      throw new Error(`${what} failed: ${msg}`);
    }

    async init() {
      if (!this.driveId) {
        const site = await this._json(await this._graph(`/sites/${this.cfg.siteHostname}:${this.cfg.sitePath}?$select=id,displayName,webUrl`), "Opening the SharePoint site");
        this.siteName = site.displayName;
        let drive;
        if (this.cfg.libraryName) {
          const list = await this._json(await this._graph(`/sites/${site.id}/drives?$select=id,name`), "Listing document libraries");
          drive = (list.value || []).find((d) => d.name.toLowerCase() === this.cfg.libraryName.toLowerCase());
          if (!drive) throw new Error(`Document library "${this.cfg.libraryName}" not found on ${site.displayName}.`);
        } else {
          drive = await this._json(await this._graph(`/sites/${site.id}/drive?$select=id,name`), "Opening the document library");
        }
        this.driveId = drive.id;
      }
      await this._ensureFolder(this.cfg.folderPath);
      return { ready: true };
    }

    async _ensureFolder(folderPath) {
      const parts = folderPath.split("/").filter(Boolean);
      let parent = "";
      for (const p of parts) {
        const path = parent ? `${parent}/${p}` : p;
        const res = await this._graph(`/drives/${this.driveId}/root:/${enc(path)}?$select=id,folder`);
        if (res.status === 404) {
          const parentRef = parent ? `/drives/${this.driveId}/root:/${enc(parent)}:/children` : `/drives/${this.driveId}/root/children`;
          const created = await this._graph(parentRef, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ name: p, folder: {}, "@microsoft.graph.conflictBehavior": "fail" })
          });
          if (!created.ok && created.status !== 409) await this._json(created, `Creating folder "${path}"`);
        } else {
          await this._json(res, `Opening folder "${path}"`);
        }
        parent = path;
      }
    }

    _dataPath() { return `${this.cfg.folderPath}/${this.fileName}`; }

    async read() {
      const res = await this._graph(`/drives/${this.driveId}/root:/${enc(this._dataPath())}`);
      if (res.status === 404) { this.itemId = null; return { data: C.emptyData(), version: null }; }
      const item = await this._json(res, "Reading the data file");
      this.itemId = item.id;
      const dl = item["@microsoft.graph.downloadUrl"];
      const content = await fetch(dl, { cache: "no-store" }); // vorab authentifizierte, kurzlebige URL
      if (!content.ok) throw new Error(`Downloading the data file failed (${content.status}).`);
      const text = await content.text();
      let raw;
      try { raw = text.trim() ? JSON.parse(text) : null; } catch (e) {
        throw new Error("The data file in SharePoint is damaged (invalid JSON). Restore a previous version via the library's version history.");
      }
      return { data: C.normalize(raw), version: item.eTag };
    }

    async write(data, version) {
      const body = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
      let res;
      if (version === null) {
        // Datei existiert noch nicht -> anlegen, aber nie überschreiben
        res = await this._graph(`/drives/${this.driveId}/root:/${enc(this._dataPath())}:/content?@microsoft.graph.conflictBehavior=fail`, {
          method: "PUT", headers: { "Content-Type": "application/json" }, body
        });
        if (res.status === 409) throw new C.ConflictError();
      } else {
        res = await this._graph(`/drives/${this.driveId}/items/${this.itemId}/content`, {
          method: "PUT", headers: { "Content-Type": "application/json", "If-Match": version }, body
        });
        if (res.status === 412) throw new C.ConflictError();
      }
      const item = await this._json(res, "Saving the data file");
      this.itemId = item.id;
      return item.eTag;
    }

    async putAttachment(path, blob) {
      // Einfacher Upload; Größe wird vorher in der App auf maxAttachmentMB begrenzt.
      const res = await this._graph(`/drives/${this.driveId}/root:/${enc(`${this.cfg.folderPath}/${path}`)}:/content?@microsoft.graph.conflictBehavior=fail`, {
        method: "PUT", headers: { "Content-Type": blob.type || "application/octet-stream" }, body: blob
      });
      if (res.status === 409) throw new C.ConflictError(`Attachment already exists: ${path}`);
      await this._json(res, "Uploading the attachment");
    }

    async _item(path) {
      return this._json(await this._graph(`/drives/${this.driveId}/root:/${enc(`${this.cfg.folderPath}/${path}`)}`), "Opening the attachment");
    }

    async getAttachmentBlob(path) {
      const item = await this._item(path);
      const res = await fetch(item["@microsoft.graph.downloadUrl"]);
      if (!res.ok) throw new Error(`Downloading the attachment failed (${res.status}).`);
      return res.blob();
    }

    async openAttachment(path, win) {
      const item = await this._item(path);
      win.go(item.webUrl); // öffnet in SharePoint (Viewer, Berechtigungen, SSO)
    }

    async deleteAttachment(path) {
      try {
        const item = await this._item(path);
        await this._graph(`/drives/${this.driveId}/items/${item.id}`, { method: "DELETE" });
      } catch (e) { /* best effort */ }
    }
  }

  window.SharePointStore = SharePointStore;
})();
