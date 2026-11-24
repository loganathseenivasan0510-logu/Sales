/*
 * Gemeinsame Bausteine aller Speicher-Backends.
 *
 * Jedes Backend implementiert dieselbe Schnittstelle:
 *   init()                       -> { ready: boolean, action?: "connect" | "reconnect" | "signin", message?: string }
 *   read()                       -> { data, version }      (version: undurchsichtiger Token für Konflikterkennung)
 *   write(data, version)         -> newVersion             (wirft ConflictError, wenn sich die Datei inzwischen geändert hat)
 *   putAttachment(path, blob)    -> void
 *   getAttachmentBlob(path)      -> Blob
 *   openAttachment(path, win)    -> void                   (win = Rückgabe von U.openLater())
 *   deleteAttachment(path)       -> void                   (nur Aufräumen nach fehlgeschlagenem Speichern)
 *   describe()                   -> { label, detail }
 *
 * Damit lässt sich später ohne UI-Änderung auf eine Datenbank/API umstellen:
 * neues Backend schreiben, in app.js registrieren – fertig.
 */
(function () {
  "use strict";

  class ConflictError extends Error {
    constructor(msg) { super(msg || "The data file was changed by someone else."); this.name = "ConflictError"; }
  }
  class ValidationError extends Error {
    constructor(errors) { super("Validation failed"); this.name = "ValidationError"; this.errors = errors || {}; }
  }
  class PermissionError extends Error {
    constructor(msg) { super(msg); this.name = "PermissionError"; }
  }

  const SCHEMA_VERSION = 1;
  const COLLECTIONS = ["quotations", "poGermany", "poCustomer", "stock"];

  function emptyData() {
    const now = new Date().toISOString();
    return { app: "SalesofLoga", schemaVersion: SCHEMA_VERSION, createdAt: now, updatedAt: now, quotations: [], poGermany: [], poCustomer: [], stock: [], audit: [] };
  }

  /** Bringt geladene Daten auf das aktuelle Schema (fehlende Listen ergänzen usw.). */
  function normalize(raw) {
    if (!raw || typeof raw !== "object") return emptyData();
    if (raw.app && raw.app !== "SalesofLoga") throw new Error("This file is not a SalesofLoga data file.");
    const d = Object.assign(emptyData(), raw);
    COLLECTIONS.concat(["audit"]).forEach((k) => { if (!Array.isArray(d[k])) d[k] = []; });
    if ((d.schemaVersion || 0) > SCHEMA_VERSION) {
      throw new Error(`Data file was written by a newer app version (schema ${d.schemaVersion}). Please update the app.`);
    }
    d.schemaVersion = SCHEMA_VERSION;
    return d;
  }

  function isEmpty(data) {
    return COLLECTIONS.every((k) => !data[k] || data[k].length === 0);
  }

  /**
   * Optimistisches Speichern: frisch lesen → Änderung anwenden → mit Versions-Check schreiben.
   * Bei Konflikt (jemand anders hat zwischenzeitlich gespeichert) wird automatisch neu gelesen
   * und die Änderung erneut angewendet. Der Mutator muss daher idempotent auf frischen Daten laufen
   * und darf Validierungsfehler (ValidationError) werfen.
   */
  async function commit(store, mutator, attempts) {
    const max = attempts || 4;
    let lastErr;
    for (let i = 0; i < max; i++) {
      const { data, version } = await store.read();
      const result = mutator(data);
      data.updatedAt = new Date().toISOString();
      try {
        const newVersion = await store.write(data, version);
        return { data, version: newVersion, result };
      } catch (e) {
        if (!(e instanceof ConflictError)) throw e;
        lastErr = e;
        await new Promise((r) => setTimeout(r, 150 + Math.random() * 350 * (i + 1)));
      }
    }
    throw lastErr || new ConflictError();
  }

  // ---------- Kleine IndexedDB-Hilfe (Browser-Store, Ordner-Handle, Einstellungen) ----------
  const DB_NAME = "salesofloga";
  const DB_VERSION = 1;
  let dbPromise = null;

  function openDb() {
    if (dbPromise) return dbPromise;
    dbPromise = new Promise((resolve, reject) => {
      const req = indexedDB.open(DB_NAME, DB_VERSION);
      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains("kv")) db.createObjectStore("kv");
        if (!db.objectStoreNames.contains("files")) db.createObjectStore("files");
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
    return dbPromise;
  }

  function idbReq(req) {
    return new Promise((resolve, reject) => { req.onsuccess = () => resolve(req.result); req.onerror = () => reject(req.error); });
  }

  async function idbGet(storeName, key) {
    const db = await openDb();
    return idbReq(db.transaction(storeName, "readonly").objectStore(storeName).get(key));
  }
  async function idbPut(storeName, key, value) {
    const db = await openDb();
    const tx = db.transaction(storeName, "readwrite");
    tx.objectStore(storeName).put(value, key);
    return new Promise((resolve, reject) => { tx.oncomplete = () => resolve(); tx.onerror = () => reject(tx.error); tx.onabort = () => reject(tx.error); });
  }
  async function idbDelete(storeName, key) {
    const db = await openDb();
    const tx = db.transaction(storeName, "readwrite");
    tx.objectStore(storeName).delete(key);
    return new Promise((resolve, reject) => { tx.oncomplete = () => resolve(); tx.onerror = () => reject(tx.error); });
  }

  window.StoreCommon = {
    ConflictError, ValidationError, PermissionError,
    SCHEMA_VERSION, COLLECTIONS,
    emptyData, normalize, isEmpty, commit,
    openDb, idbReq, idbGet, idbPut, idbDelete
  };
})();
