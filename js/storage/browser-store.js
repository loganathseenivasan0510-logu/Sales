/*
 * BrowserStore – speichert alles in IndexedDB dieses Browsers.
 * Fallback für Browser ohne File System Access API (Firefox, Safari).
 * Achtung: Daten verschwinden beim Löschen der Browserdaten -> regelmäßig "Export JSON".
 */
(function () {
  "use strict";
  const C = window.StoreCommon;
  const DATA_KEY = "browserStore:data";

  class BrowserStore {
    constructor() { this.id = "browser"; }

    async init() {
      await C.openDb();
      if (navigator.storage && navigator.storage.persist) {
        try { await navigator.storage.persist(); } catch (e) { /* optional */ }
      }
      return { ready: true };
    }

    describe() {
      return { label: "Browser storage", detail: "Only in this browser on this PC – export regularly" };
    }

    async read() {
      const rec = await C.idbGet("kv", DATA_KEY);
      if (!rec) return { data: C.emptyData(), version: 0 };
      return { data: C.normalize(JSON.parse(rec.json)), version: rec.version };
    }

    async write(data, version) {
      // Versions-Check und Schreiben in EINER Transaktion -> sicher auch bei mehreren Tabs
      const db = await C.openDb();
      const tx = db.transaction("kv", "readwrite");
      const store = tx.objectStore("kv");
      const current = await C.idbReq(store.get(DATA_KEY));
      const currentVersion = current ? current.version : 0;
      if (currentVersion !== version) {
        tx.abort();
        throw new C.ConflictError();
      }
      const next = currentVersion + 1;
      store.put({ version: next, json: JSON.stringify(data) }, DATA_KEY);
      await new Promise((resolve, reject) => { tx.oncomplete = resolve; tx.onerror = () => reject(tx.error); });
      return next;
    }

    async putAttachment(path, blob) { await C.idbPut("files", path, blob); }

    async getAttachmentBlob(path) {
      const blob = await C.idbGet("files", path);
      if (!blob) throw new Error(`Attachment not found: ${path}`);
      return blob;
    }

    async openAttachment(path, win) {
      const blob = await this.getAttachmentBlob(path);
      const url = URL.createObjectURL(blob);
      win.go(url);
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    }

    async deleteAttachment(path) { await C.idbDelete("files", path); }
  }

  window.BrowserStore = BrowserStore;
})();
