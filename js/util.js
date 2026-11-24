/* Hilfsfunktionen: DOM-Aufbau ohne innerHTML, Formatierung, Dialoge, Toasts. */
(function () {
  "use strict";

  const U = {};

  /**
   * Erzeugt ein Element. Kinder werden als Text eingefügt – niemals als HTML.
   * attrs: class, text, dataset, on<event>, boolean-Attribute (true/false), sonst setAttribute.
   */
  U.el = function (tag, attrs, ...children) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v === null || v === undefined || v === false) continue;
      if (k === "class") e.className = v;
      else if (k === "text") e.textContent = v;
      else if (k === "dataset") Object.assign(e.dataset, v);
      else if (k.startsWith("on") && typeof v === "function") e.addEventListener(k.slice(2), v);
      else if (v === true) e.setAttribute(k, "");
      else e.setAttribute(k, String(v));
    }
    for (const c of children.flat(Infinity)) {
      if (c === null || c === undefined || c === false) continue;
      e.append(c instanceof Node ? c : document.createTextNode(String(c)));
    }
    return e;
  };

  U.$ = (sel, root) => (root || document).querySelector(sel);
  U.$$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));
  U.clear = (node) => { while (node.firstChild) node.removeChild(node.firstChild); return node; };

  U.uuid = function () {
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    const b = crypto.getRandomValues(new Uint8Array(16));
    b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
    const h = Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
  };

  U.nowIso = () => new Date().toISOString();
  U.todayIso = () => {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
  };

  const dateFmt = new Intl.DateTimeFormat("en-GB", { day: "2-digit", month: "short", year: "numeric" });
  const dateTimeFmt = new Intl.DateTimeFormat("en-GB", { day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });

  /** "2026-11-24" → "24 Nov 2026" (ohne Zeitzonenverschiebung) */
  U.fmtDate = function (iso) {
    if (!iso) return "";
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso);
    if (!m) return String(iso);
    return dateFmt.format(new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
  };
  U.fmtDateTime = (iso) => (iso ? dateTimeFmt.format(new Date(iso)) : "");

  U.fmtMoney = function (value, currency) {
    if (value === null || value === undefined || value === "") return "";
    try {
      return new Intl.NumberFormat(currency === "INR" ? "en-IN" : "en-GB", { style: "currency", currency: currency || "INR" }).format(value);
    } catch (e) {
      return `${Number(value).toFixed(2)} ${currency || ""}`.trim();
    }
  };
  U.fmtNumber = (n) => new Intl.NumberFormat("en-IN").format(n);
  U.fmtBytes = (n) => (n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`);

  /** Dateiname, der in Windows und SharePoint gültig ist. */
  U.safeFileName = function (s) {
    return String(s || "file")
      .normalize("NFKC")
      .replace(/[\\/:*?"<>|#%~&{}]/g, "_")
      .replace(/[\u0000-\u001f]/g, "")
      .replace(/\s+/g, " ")
      .trim()
      .replace(/^\.+|\.+$/g, "")
      .slice(0, 80) || "file";
  };

  U.extOf = (name) => {
    const m = /\.([a-z0-9]{1,8})$/i.exec(name || "");
    return m ? m[1].toLowerCase() : "";
  };

  U.downloadBlob = function (blob, fileName) {
    const url = URL.createObjectURL(blob);
    const a = U.el("a", { href: url, download: fileName });
    document.body.append(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
  };

  // ---------- Toasts ----------
  U.toast = function (message, type) {
    const host = U.$("#toasts");
    if (Array.from(host.children).some((c) => c.textContent === message && !c.classList.contains("toast-hide"))) return;
    const t =U.el("div", { class: `toast toast-${type || "info"}`, role: type === "error" ? "alert" : "status" }, message);
    host.append(t);
    setTimeout(() => t.classList.add("toast-hide"), type === "error" ? 7000 : 3500);
    setTimeout(() => t.remove(), type === "error" ? 7600 : 4100);
  };

  // ---------- Dialoge (statt alert/confirm) ----------
  /**
   * Zeigt einen modalen Dialog. buttons: [{label, value, primary, danger}]
   * Liefert den value des geklickten Buttons (Escape = null).
   */
  U.dialog = function ({ title, body, buttons }) {
    return new Promise((resolve) => {
      const dlg = U.el("dialog", { class: "modal" });
      const content = typeof body === "string" ? U.el("p", {}, body) : body;
      const btnRow = U.el("div", { class: "modal-actions" });
      (buttons || [{ label: "OK", value: true, primary: true }]).forEach((b) => {
        btnRow.append(U.el("button", {
          type: "button",
          class: `btn ${b.primary ? "btn-primary" : ""} ${b.danger ? "btn-danger" : ""}`,
          onclick: () => { dlg.close(); resolve(b.value); }
        }, b.label));
      });
      dlg.append(U.el("h2", { class: "modal-title" }, title || ""), content, btnRow);
      dlg.addEventListener("cancel", () => resolve(null));
      dlg.addEventListener("close", () => { resolve(null); setTimeout(() => dlg.remove(), 0); });
      document.body.append(dlg);
      dlg.showModal();
    });
  };

  U.confirm = (title, text, okLabel, danger) =>
    U.dialog({
      title,
      body: text,
      buttons: [
        { label: "Cancel", value: false },
        { label: okLabel || "OK", value: true, primary: !danger, danger: !!danger }
      ]
    });

  /**
   * Öffnet ein Fenster synchron im Klick-Handler (Popup-Blocker) und
   * setzt die URL, sobald sie asynchron ermittelt wurde.
   */
  U.openLater = function () {
    const w = window.open("about:blank", "_blank");
    if (w) { try { w.opener = null; } catch (e) { /* ignore */ } }
    return {
      go(url) { if (w) w.location.href = url; else window.open(url, "_blank", "noopener"); },
      cancel() { if (w) w.close(); }
    };
  };

  window.U = U;
})();
