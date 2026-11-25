/*
 * LocalFolderStore – speichert in einem frei wählbaren Ordner auf dem PC
 * (File System Access API, Chrome/Edge).
 *
 * Ordnerstruktur:
 *   <Ordner>/salesofloga-data.json
 *   <Ordner>/attachments/<bereich>/<datei>
 *   <Ordner>/backups/salesofloga-data-YYYY-MM-DD.json   (ein Tagesstand, automatisch)
 *
 * Tipp: Liegt der Ordner in einem per OneDrive synchronisierten SharePoint-Ordner,
 * ist das ein einfacher Zwischenschritt. Bei gleichzeitigem Bearbeiten durch mehrere
 * Personen erzeugt der OneDrive-Client aber Konfliktkopien – für echten Mehrbenutzerbetrieb
 * den SharePoint-Modus (Graph + eTag) verwenden.
 */
(function () {
  "use strict";
  const C = window.StoreCommon;
  const HANDLE_KEY = "localFolder:handle";

  class LocalFolderStore {
    constructor(cfg) {
      this.id = "localFolder";
      this.fileName = cfg.dataFileName;
      this.dir = null;
    }

    static isSupported() { return typeof window.showDirectoryPicker === "function"; }

    async init() {
      if (!LocalFolderStore.isSupported()) {
        return { ready: false, action: "unsupported", message: "This browser cannot access local folders. Use Edge or Chrome, or choose Browser storage." };
      }
      const handle = await C.idbGet("kv", HANDLE_KEY);
      if (!handle) return { ready: false, action: "connect", message: "Choose a folder where the data file should be stored." };
      this.dir = handle;
      const perm = await this._permission(false);
      if (perm === "granted") return { ready: true };
      return { ready: false, action: "reconnect", message: `Allow access to the folder "${handle.name}" again.` };
    }

    /** Muss aus einem Klick heraus aufgerufen werden (Browser-Vorgabe). */
    async connect() {
      const handle = await window.showDirectoryPicker({ id: "salesofloga", mode: "readwrite" });
      this.dir = handle;
      if ((await this._permission(true)) !== "granted") throw new C.PermissionError("No write permission for the selected folder.");
      await C.idbPut("kv", HANDLE_KEY, handle);
      return { ready: true };
    }

    /** Muss aus einem Klick heraus aufgerufen werden. */
    async reconnect() {
      if (!this.dir) return this.connect();
      if ((await this._permission(true)) !== "granted") throw new C.PermissionError("Folder access was not granted.");
      return { ready: true };
    }

    async forget() { await C.idbDelete("kv", HANDLE_KEY); this.dir = null; }

    async _permission(request) {
      const h = this.dir;
      if (!h.queryPermission) return "granted"; // z. B. Origin Private File System
      const opts = { mode: "readwrite" };
      let p = await h.queryPermission(opts);
      if (p !== "granted" && request) p = await h.requestPermission(opts);
      return p;
    }

    describe() {
      return { label: "Local folder", detail: this.dir ? this.dir.name : "not connected" };
    }

    async _fileHandle(create) {
      try {
        return await this.dir.getFileHandle(this.fileName, { create: !!create });
      } catch (e) {
        if (e.name === "NotFoundError") return null;
        throw e;
      }
    }

    static versionOf(file) { return file ? `${file.lastModified}:${file.size}` : null; }

    async read() {
      const fh = await this._fileHandle(false);
      if (!fh) return { data: C.emptyData(), version: null };
      const file = await fh.getFile();
      const text = await file.text();
      let raw;
      try { raw = text.trim() ? JSON.parse(text) : null; } catch (e) {
        throw new Error(`The data file "${this.fileName}" is damaged (invalid JSON). Restore it from the backups folder.`);
      }
      return { data: C.normalize(raw), version: LocalFolderStore.versionOf(file) };
    }

    async write(data, version) {
      const existing = await this._fileHandle(false);
      const current = existing ? await existing.getFile() : null;
      if (LocalFolderStore.versionOf(current) !== version) throw new C.ConflictError();
      if (current && current.size > 0) await this._dailyBackup(current);

      const fh = existing || (await this._fileHandle(true));
      const w = await fh.createWritable();
      await w.write(new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }));
      await w.close();
      return LocalFolderStore.versionOf(await fh.getFile());
    }

    async _dailyBackup(file) {
      const dir = await this.dir.getDirectoryHandle("backups", { create: true });
      const name = `salesofloga-data-${new Date().toISOString().slice(0, 10)}.json`;
      try { await dir.getFileHandle(name, { create: false }); return; } catch (e) { if (e.name !== "NotFoundError") throw e; }
      const bh = await dir.getFileHandle(name, { create: true });
      const w = await bh.createWritable();
      await w.write(file);
      await w.close();
    }

    async _resolve(path, create) {
      const parts = path.split("/").filter(Boolean);
      let dir = this.dir;
      for (const p of parts.slice(0, -1)) dir = await dir.getDirectoryHandle(p, { create: !!create });
      return { dir, name: parts[parts.length - 1] };
    }

    async putAttachment(path, blob) {
      const { dir, name } = await this._resolve(path, true);
      const fh = await dir.getFileHandle(name, { create: true });
      const w = await fh.createWritable();
      await w.write(blob);
      await w.close();
    }

    async getAttachmentBlob(path) {
      const { dir, name } = await this._resolve(path, false);
      const fh = await dir.getFileHandle(name);
      return fh.getFile();
    }

    async openAttachment(path, win) {
      const file = await this.getAttachmentBlob(path);
      const url = URL.createObjectURL(file);
      win.go(url);
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    }

    async deleteAttachment(path) {
      try {
        const { dir, name } = await this._resolve(path, false);
        await dir.removeEntry(name);
      } catch (e) { /* Aufräumen ist best effort */ }
    }
  }

  window.LocalFolderStore = LocalFolderStore;
})();
