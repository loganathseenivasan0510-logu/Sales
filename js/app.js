/*
 * SalesofLoga – Anwendungslogik
 * Rendering ausschließlich über DOM-APIs (U.el / textContent) – keine Daten in innerHTML.
 */
(function () {
  "use strict";

  const cfg = window.APP_CONFIG;
  const SC = window.SCHEMAS;
  const C = window.StoreCommon;
  const { el, $, $$, clear } = window.U;
  const U = window.U;

  const LS = {
    store: "salesofloga.store",
    user: "salesofloga.localUser"
  };

  const state = {
    store: null,
    storeId: null,
    ready: false,
    user: null,
    data: C.emptyData(),
    version: null,
    editing: {},       // schemaKey -> recordId
    lastResults: {},   // schemaKey -> records (für CSV-Export)
    busy: false
  };

  // ======================================================================
  // Identität & Berechtigungen
  // ======================================================================

  function localUser() {
    const name = (localStorage.getItem(LS.user) || "").trim();
    return name ? { id: `local:${name.toLowerCase()}`, name, upn: "" } : null;
  }

  function isAdmin(user) {
    if (!user) return false;
    const list = (cfg.admins || []).map((a) => String(a).toLowerCase());
    return list.includes((user.upn || "").toLowerCase()) || list.includes((user.name || "").toLowerCase());
  }

  function canEdit(rec) {
    if (!state.user || !rec) return false;
    return isAdmin(state.user) || (rec.createdBy && rec.createdBy.id === state.user.id);
  }

  const who = () => ({ id: state.user.id, name: state.user.name });

  // ======================================================================
  // Speicher-Backends
  // ======================================================================

  function makeStore(id) {
    if (id === "localFolder") return new window.LocalFolderStore(cfg);
    if (id === "browser") return new window.BrowserStore(cfg);
    if (id === "sharepoint") return new window.SharePointStore(cfg, () => window.Auth.getToken());
    throw new Error(`Unknown storage "${id}"`);
  }

  function storeOptions() {
    const all = [
      {
        id: "localFolder", title: "Local folder", icon: "📁",
        text: "A JSON file plus attachments in a folder of your choice. Daily backup included. Edge or Chrome required.",
        available: window.LocalFolderStore.isSupported(),
        why: "Not supported in this browser – use Edge or Chrome."
      },
      {
        id: "browser", title: "Browser storage", icon: "🗄️",
        text: "Stored inside this browser only. Good for testing; export a backup regularly.",
        available: typeof indexedDB !== "undefined", why: "IndexedDB not available."
      },
      {
        id: "sharepoint", title: "SharePoint (Microsoft 365 sign-in)", icon: "☁️",
        text: "Shared data file in a SharePoint library. Sign-in with your company account (SSO). Every user maintains their own records.",
        available: window.SharePointStore.isConfigured(cfg) && location.protocol !== "file:",
        why: location.protocol === "file:" ? "Needs the app to run on a web server (see README)." : "Not configured yet – IT has to fill in js/config.js (see README)."
      }
    ];
    return all.filter((o) => (cfg.enabledStores || []).includes(o.id));
  }

  async function chooseStorage(allowCancel) {
    const opts = storeOptions();
    let selected = state.storeId || (opts.find((o) => o.available) || {}).id;
    const list = el("div", { class: "store-options", role: "radiogroup" },
      opts.map((o) => el("label", { class: `store-option ${o.available ? "" : "disabled"}` },
        el("input", { type: "radio", name: "store", value: o.id, checked: o.id === selected, disabled: !o.available, onchange: () => { selected = o.id; } }),
        el("span", { class: "store-icon", "aria-hidden": "true" }, o.icon),
        el("span", { class: "store-text" },
          el("strong", {}, o.title),
          el("span", {}, o.text),
          o.available ? null : el("em", {}, o.why))
      )));
    const body = el("div", {}, el("p", {}, "Where should the data be stored? You can change this later in Settings and copy existing data over."), list);
    const buttons = [{ label: "Continue", value: "ok", primary: true }];
    if (allowCancel) buttons.unshift({ label: "Cancel", value: null });
    const res = await U.dialog({ title: "Choose storage", body, buttons });
    if (res !== "ok" || !selected) return null;
    return selected;
  }

  async function activateStore(id) {
    state.ready = false;
    state.storeId = id;
    localStorage.setItem(LS.store, id);
    state.store = makeStore(id);
    renderStatus();

    if (id === "sharepoint") {
      const signedIn = await window.Auth.init(cfg);
      if (!signedIn) { showGate("signin", "Sign in with your company account to open the shared data."); return; }
      state.user = window.Auth.user();
    } else {
      state.user = localUser();
    }

    const r = await state.store.init();
    if (!r.ready) { showGate(r.action, r.message); return; }
    await afterReady();
  }

  async function afterReady() {
    if (!state.user) {
      const ok = await askLocalName(false);
      if (!ok) { showGate("name", "Please enter your full name – it is stored with every record you create."); return; }
    }
    await reload();
    state.ready = true;
    hideGate();
    renderStatus();
    route();
  }

  async function reload(silent) {
    try {
      const { data, version } = await state.store.read();
      state.data = data;
      state.version = version;
      renderAll();
    } catch (e) {
      if (!silent) handleError(e);
    }
  }

  // ---------- Gate: Speicher noch nicht verbunden ----------
  function showGate(action, message) {
    const gate = $("#gate");
    clear(gate);
    const btn = (label, fn, primary) => el("button", { type: "button", class: `btn ${primary ? "btn-primary" : ""}`, onclick: fn }, label);
    const actions = el("div", { class: "gate-actions" });

    if (action === "connect") actions.append(btn("Choose folder…", guarded(async () => { await state.store.connect(); await afterReady(); }), true));
    if (action === "reconnect") actions.append(btn("Allow folder access", guarded(async () => { await state.store.reconnect(); await afterReady(); }), true),
      btn("Choose another folder…", guarded(async () => { await state.store.connect(); await afterReady(); })));
    if (action === "signin") actions.append(btn("Sign in with Microsoft", guarded(async () => {
      const done = await window.Auth.signIn();
      if (!done) return; // Redirect läuft
      state.user = window.Auth.user();
      const r = await state.store.init();
      if (!r.ready) { showGate(r.action, r.message); return; }
      await afterReady();
    }), true));
    if (action === "name") actions.append(btn("Enter name", guarded(async () => { if (await askLocalName(false)) await afterReady(); }), true));
    actions.append(btn("Change storage…", guarded(switchStorage)));

    gate.append(el("div", { class: "card gate-card" },
      el("h2", {}, "Storage not connected"),
      el("p", {}, message || ""),
      actions));
    gate.hidden = false;
    $("#pages").hidden = true;
  }

  function hideGate() {
    $("#gate").hidden = true;
    $("#pages").hidden = false;
  }

  /** Wrappt Klick-Handler: Fehler anzeigen statt verschlucken. */
  function guarded(fn) {
    return async (ev) => {
      try { await fn(ev); } catch (e) { handleError(e); }
    };
  }

  function handleError(e) {
    console.error(e);
    if (e && e.name === "AbortError") return; // Dialog abgebrochen
    if (e instanceof C.ValidationError) { U.toast("Please correct the highlighted fields.", "error"); return; }
    if (e instanceof C.ConflictError) { U.toast("Someone else saved at the same time. Please try again.", "error"); return; }
    U.toast((e && e.message) || String(e), "error");
  }

  async function askLocalName(allowCancel) {
    const input = el("input", { type: "text", id: "local-name", autocomplete: "name", maxlength: "80", value: (localUser() || {}).name || "", placeholder: "First name Last name" });
    const err = el("div", { class: "field-error", id: "local-name-err" });
    const body = el("div", {},
      el("p", {}, "Your full name (first and last name) is saved with each record for traceability. Without Microsoft sign-in this is not verified."),
      el("div", { class: "field" }, el("label", { for: "local-name" }, "Full name"), input, err));
    for (;;) {
      const buttons = [{ label: "Save", value: "ok", primary: true }];
      if (allowCancel) buttons.unshift({ label: "Cancel", value: null });
      const res = await U.dialog({ title: "Who is working?", body, buttons });
      if (res !== "ok") return false;
      const name = input.value.trim().replace(/\s+/g, " ");
      if (name.split(" ").length < 2 || name.length < 5 || /\./.test(name.split(" ").pop())) {
        err.textContent = "Please enter first and last name in full (no abbreviations).";
        continue;
      }
      localStorage.setItem(LS.user, name);
      if (state.storeId !== "sharepoint") state.user = localUser();
      renderStatus();
      return true;
    }
  }

  // ======================================================================
  // Layout, Navigation
  // ======================================================================

  const PAGES = [{ key: "dashboard", page: "dashboard", nav: "Overview", icon: "◧" }]
    .concat(Object.entries(SC).map(([key, s]) => ({ key, page: s.page, nav: s.nav, icon: { quotation: "✎", poGermany: "⇄", poCustomer: "☰", stock: "▦" }[key] || "•" })));

  function buildShell() {
    const nav = $("#nav");
    PAGES.forEach((p) => {
      nav.append(el("a", { href: `#/${p.page}`, class: "nav-item", dataset: { page: p.page } },
        el("span", { class: "nav-icon", "aria-hidden": "true" }, p.icon), p.nav));
    });

    const pages = $("#pages");
    pages.append(el("section", { class: "page", id: "page-dashboard", "aria-labelledby": "h-dashboard" }));
    Object.entries(SC).forEach(([key, s]) => pages.append(buildEntityPage(key, s)));

    $("#btn-settings").addEventListener("click", guarded(openSettings));
    $("#btn-refresh").addEventListener("click", guarded(async () => { if (state.ready) { await reload(); U.toast("Data refreshed", "info"); } }));
    window.addEventListener("hashchange", route);
    window.addEventListener("focus", () => { if (state.ready && !state.busy) reload(true); });
    setInterval(() => { if (state.ready && !state.busy && document.visibilityState === "visible" && state.storeId === "sharepoint") reload(true); }, 60000);
  }

  function route() {
    const page = (location.hash.replace(/^#\/?/, "") || "dashboard").split("?")[0];
    const known = PAGES.find((p) => p.page === page) || PAGES[0];
    $$(".page").forEach((s) => { s.hidden = s.id !== `page-${known.page}`; });
    $$(".nav-item").forEach((a) => {
      if (a.dataset.page === known.page) a.setAttribute("aria-current", "page"); else a.removeAttribute("aria-current");
    });
    $("#page-title").textContent = known.page === "dashboard" ? "Overview" : known.nav.replace(/ Entry$/, "");
    document.title = `${known.nav} · ${cfg.appTitle}`;
  }

  function renderStatus() {
    const box = $("#status");
    clear(box);
    if (state.store) {
      const d = state.store.describe();
      box.append(el("span", { class: `badge badge-${state.storeId}`, title: d.detail }, `${d.label}: ${d.detail}`));
    }
    if (state.user) box.append(el("span", { class: "user-chip", title: state.user.upn || "local user" }, state.user.name, isAdmin(state.user) ? " (admin)" : ""));
  }

  function renderAll() {
    renderDashboard();
    Object.keys(SC).forEach((k) => { refreshSuggestions(k); runSearch(k); });
  }

  // ======================================================================
  // Entity-Seiten (Formular + Suche + Ergebnisse) – generisch aus SCHEMAS
  // ======================================================================

  const fid = (k, name) => `${k}-${name}`;
  const sid = (k, name) => `${k}-s-${name}`;

  function optionsOf(f) { return typeof f.options === "function" ? f.options() : f.options || []; }

  function buildField(k, f) {
    const id = fid(k, f.name);
    const describedBy = `${id}-err`;
    let control;
    if (f.type === "select") {
      control = el("select", { id, name: f.name, required: f.required, "aria-describedby": describedBy },
        el("option", { value: "" }, `Select ${f.label.toLowerCase()}`),
        optionsOf(f).map((o) => el("option", { value: o }, o)));
    } else if (f.type === "money") {
      control = el("div", { class: "money" },
        el("input", { id, name: f.name, type: "number", inputmode: "decimal", step: "0.01", min: "0", required: f.required, "aria-describedby": describedBy }),
        el("select", { id: `${id}-currency`, name: `${f.name}Currency`, "aria-label": "Currency" },
          cfg.currencies.map((c) => el("option", { value: c, selected: c === cfg.defaultCurrency }, c))));
    } else if (f.type === "int") {
      control = el("input", { id, name: f.name, type: "number", inputmode: "numeric", step: "1", min: String(f.min ?? 0), max: f.max ? String(f.max) : null, required: f.required, "aria-describedby": describedBy });
    } else if (f.type === "file") {
      control = el("div", { class: "file-field" },
        el("input", { id, name: f.name, type: "file", accept: f.accept, "aria-describedby": describedBy }),
        el("div", { class: "file-current", id: `${id}-current`, hidden: true }));
    } else {
      control = el("input", {
        id, name: f.name, type: f.type === "date" ? "date" : "text", required: f.required,
        maxlength: f.maxLength ? String(f.maxLength) : null, placeholder: f.placeholder || null,
        list: f.suggestFrom ? `${id}-list` : null, autocomplete: "off", "aria-describedby": describedBy
      });
    }
    return el("div", { class: `field field-${f.type}` },
      el("label", { for: id }, f.label, f.required ? el("span", { class: "req", "aria-hidden": "true" }, " *") : null),
      control,
      f.suggestFrom ? el("datalist", { id: `${id}-list` }) : null,
      f.hint ? el("div", { class: "field-hint" }, f.hint) : null,
      el("div", { class: "field-error", id: describedBy, "aria-live": "polite" }));
  }

  function buildEntityPage(k, s) {
    const form = el("form", { class: "card form-card", id: `form-${k}`, novalidate: true },
      el("div", { class: "card-head" },
        el("h2", { id: `form-title-${k}` }, `New ${s.singular}`),
        el("button", { type: "button", class: "btn btn-link", id: `cancel-edit-${k}`, hidden: true, onclick: () => resetForm(k) }, "Cancel editing")),
      s.fields.map((f) => buildField(k, f)),
      el("div", { class: "actions" },
        el("button", { type: "button", class: "btn", onclick: () => resetForm(k) }, "Clear"),
        el("button", { type: "submit", class: "btn btn-primary", id: `submit-${k}` }, "Submit")));
    form.addEventListener("submit", (ev) => { ev.preventDefault(); guarded(() => saveRecord(k))(); });

    // Auto-Fill Kunde aus verknüpftem Angebot
    const qField = s.fields.find((f) => f.suggestFrom === "quotations");
    if (qField) {
      form.addEventListener("change", (ev) => {
        if (ev.target.id !== fid(k, qField.name)) return;
        const q = state.data.quotations.find((r) => !r.deletedAt && r.qNo.toLowerCase() === ev.target.value.trim().toLowerCase());
        const cust = $(`#${fid(k, "customer")}`);
        if (q && cust && !cust.value) cust.value = q.customer;
      });
    }

    const searchFields = s.fields.filter((f) => f.search);
    const search = el("form", { class: "card search-card", id: `search-${k}`, novalidate: true },
      el("div", { class: "card-head" }, el("h2", {}, "Search")),
      searchFields.map((f) => {
        const id = sid(k, f.name);
        const control = f.type === "select"
          ? el("select", { id, name: f.name }, el("option", { value: "" }, "Any"), optionsOf(f).map((o) => el("option", { value: o }, o)))
          : el("input", { id, name: f.name, type: "search", placeholder: `Contains…`, autocomplete: "off" });
        return el("div", { class: "field" }, el("label", { for: id }, f.label), control);
      }),
      el("label", { class: "check" }, el("input", { type: "checkbox", id: sid(k, "mine") }), " Only my records"),
      el("div", { class: "actions" },
        el("button", { type: "button", class: "btn", onclick: () => { $(`#search-${k}`).reset(); runSearch(k); } }, "Reset"),
        el("button", { type: "button", class: "btn", onclick: () => exportCsv(k) }, "Export CSV"),
        el("button", { type: "submit", class: "btn btn-primary" }, "Search")));
    search.addEventListener("submit", (ev) => { ev.preventDefault(); runSearch(k); });

    const results = el("div", { class: "card results-card" },
      el("div", { class: "card-head" }, el("h2", {}, "Records"), el("span", { class: "muted", id: `count-${k}` }, "")),
      el("div", { class: "table-wrap" }, el("table", { class: "table", id: `table-${k}` })));

    return el("section", { class: "page", id: `page-${s.page}`, hidden: true },
      el("div", { class: "grid-2" }, form, search),
      results);
  }

  function refreshSuggestions(k) {
    SC[k].fields.filter((f) => f.suggestFrom).forEach((f) => {
      const list = $(`#${fid(k, f.name)}-list`);
      clear(list);
      state.data[f.suggestFrom].filter((r) => !r.deletedAt)
        .forEach((r) => list.append(el("option", { value: r.qNo }, `${r.customer} · ${U.fmtDate(r.qDate)}`)));
    });
  }

  // ---------- Formular lesen / befüllen ----------
  function readForm(k) {
    const s = SC[k];
    const values = {};
    let file = null;
    s.fields.forEach((f) => {
      const node = $(`#${fid(k, f.name)}`);
      if (f.type === "file") { file = node.files && node.files[0] ? node.files[0] : null; return; }
      if (f.type === "money") {
        values[f.name] = node.value.trim();
        values.currency = $(`#${fid(k, f.name)}-currency`).value;
        return;
      }
      values[f.name] = node.value.trim();
    });
    const removeBox = $(`#${fid(k, "attachment")}-remove`);
    return { values, file, removeAttachment: !!(removeBox && removeBox.checked) };
  }

  function resetForm(k) {
    const form = $(`#form-${k}`);
    form.reset();
    delete state.editing[k];
    $$(".field-error", form).forEach((e) => { e.textContent = ""; });
    $$("[aria-invalid]", form).forEach((e) => e.removeAttribute("aria-invalid"));
    const cur = $(`#${fid(k, "attachment")}-current`);
    if (cur) { clear(cur); cur.hidden = true; }
    $(`#form-title-${k}`).textContent = `New ${SC[k].singular}`;
    $(`#submit-${k}`).textContent = "Submit";
    $(`#cancel-edit-${k}`).hidden = true;
    form.classList.remove("editing");
  }

  function startEdit(k, rec) {
    resetForm(k);
    const s = SC[k];
    state.editing[k] = rec.id;
    s.fields.forEach((f) => {
      const node = $(`#${fid(k, f.name)}`);
      if (f.type === "file") {
        const cur = $(`#${fid(k, f.name)}-current`);
        if (rec.attachment) {
          cur.append(
            el("span", {}, `Current: ${rec.attachment.name} (${U.fmtBytes(rec.attachment.size || 0)}) – choose a file to replace it.`),
            el("label", { class: "check" }, el("input", { type: "checkbox", id: `${fid(k, f.name)}-remove` }), " Remove attachment"));
          cur.hidden = false;
        }
        return;
      }
      if (f.type === "money") {
        node.value = rec[f.name] ?? "";
        $(`#${fid(k, f.name)}-currency`).value = rec.currency || cfg.defaultCurrency;
        return;
      }
      if (f.type === "select" && rec[f.name] && !optionsOf(f).includes(rec[f.name])) {
        node.append(el("option", { value: rec[f.name] }, rec[f.name])); // Altwert nicht verlieren
      }
      node.value = rec[f.name] ?? "";
    });
    $(`#form-title-${k}`).textContent = `Edit ${s.singular} ${rec[s.key]}`;
    $(`#submit-${k}`).textContent = "Save changes";
    $(`#cancel-edit-${k}`).hidden = false;
    const form = $(`#form-${k}`);
    form.classList.add("editing");
    form.scrollIntoView({ behavior: "smooth", block: "start" });
    $(`#${fid(k, s.fields[0].name)}`).focus({ preventScroll: true });
  }

  // ---------- Validierung ----------
  function validate(k, values, file, data, editingId) {
    const s = SC[k];
    const errors = {};
    const maxBytes = (cfg.maxAttachmentMB || 20) * 1048576;
    s.fields.forEach((f) => {
      const v = values[f.name];
      if (f.type === "file") {
        if (!file) return;
        const ext = U.extOf(file.name);
        if (!(f.mimes.includes(file.type) || f.exts.includes(ext))) errors[f.name] = `Only ${f.exts.join(", ").toUpperCase()} files are allowed.`;
        else if (file.size > maxBytes) errors[f.name] = `File is larger than ${cfg.maxAttachmentMB} MB.`;
        else if (file.size === 0) errors[f.name] = "File is empty.";
        return;
      }
      if (v === "" || v === undefined) { if (f.required) errors[f.name] = "Required"; return; }
      switch (f.type) {
        case "date":
          if (!/^\d{4}-\d{2}-\d{2}$/.test(v) || isNaN(Date.parse(v))) errors[f.name] = "Invalid date";
          else if (v < "2000-01-01" || v > "2100-12-31") errors[f.name] = "Date out of range";
          break;
        case "money": {
          const n = Number(v);
          if (!isFinite(n) || n < 0) errors[f.name] = "Enter a positive amount";
          else if (!/^\d+(\.\d{1,2})?$/.test(v)) errors[f.name] = "Max. 2 decimals, no thousands separators";
          else if (n > 1e12) errors[f.name] = "Amount too large";
          else if (!cfg.currencies.includes(values.currency)) errors[f.name] = "Invalid currency";
          break;
        }
        case "int":
          if (!/^\d+$/.test(v)) errors[f.name] = "Whole number ≥ 0";
          else if (f.max && Number(v) > f.max) errors[f.name] = `Max. ${f.max}`;
          break;
        case "select":
          if (!optionsOf(f).includes(v)) {
            const rec = editingId && data[s.collection].find((r) => r.id === editingId);
            if (!(rec && rec[f.name] === v)) errors[f.name] = "Invalid selection";
          }
          break;
        default:
          if (f.maxLength && v.length > f.maxLength) errors[f.name] = `Max. ${f.maxLength} characters`;
          else if (f.pattern && !f.pattern.test(v)) errors[f.name] = "Only letters, digits, space and . _ - / allowed";
      }
      if (!errors[f.name] && f.unique) {
        const dup = data[s.collection].find((r) => !r.deletedAt && r.id !== editingId && String(r[f.name]).toLowerCase() === v.toLowerCase());
        if (dup) errors[f.name] = `${f.label} already exists (created by ${dup.createdBy ? dup.createdBy.name : "unknown"})`;
      }
    });
    return errors;
  }

  function showErrors(k, errors) {
    const form = $(`#form-${k}`);
    SC[k].fields.forEach((f) => {
      const msg = errors[f.name] || "";
      $(`#${fid(k, f.name)}-err`).textContent = msg;
      const node = $(`#${fid(k, f.name)}`);
      if (msg) node.setAttribute("aria-invalid", "true"); else node.removeAttribute("aria-invalid");
    });
    const first = SC[k].fields.find((f) => errors[f.name]);
    if (first) $(`#${fid(k, first.name)}`, form).focus();
  }

  function toRecordValues(k, values) {
    const out = {};
    SC[k].fields.forEach((f) => {
      if (f.type === "file") return;
      const v = values[f.name];
      if (f.type === "money") { out[f.name] = v === "" ? null : Math.round(Number(v) * 100) / 100; out.currency = values.currency; }
      else if (f.type === "int") out[f.name] = v === "" ? null : Number(v);
      else out[f.name] = v;
    });
    return out;
  }

  function audit(data, action, k, rec, changes) {
    data.audit.push({
      ts: U.nowIso(), user: who(), action, entity: k, recordId: rec.id,
      label: `${SC[k].singular} ${rec[SC[k].key]}`, changes: changes || undefined
    });
  }

  function setBusy(k, busy, label) {
    state.busy = busy;
    const btn = $(`#submit-${k}`);
    $$(`#form-${k} button`).forEach((b) => { b.disabled = busy; });
    if (busy) { btn.dataset.label = btn.textContent; btn.textContent = label || "Saving…"; }
    else if (btn.dataset.label) { btn.textContent = btn.dataset.label; delete btn.dataset.label; }
  }

  // ---------- Speichern ----------
  async function saveRecord(k) {
    if (state.busy || !state.ready) return;
    const s = SC[k];
    const editingId = state.editing[k] || null;
    const { values, file, removeAttachment } = readForm(k);

    const errors = validate(k, values, file, state.data, editingId);
    showErrors(k, errors);
    if (Object.keys(errors).length) { U.toast("Please correct the highlighted fields.", "error"); return; }

    setBusy(k, true);
    const fileField = s.fields.find((f) => f.type === "file");
    let uploaded = null;
    try {
      if (file && fileField) {
        const ext = U.extOf(file.name) || (file.type === "application/pdf" ? "pdf" : "bin");
        const path = `attachments/${fileField.folder}/${U.safeFileName(values[s.key])}__${U.uuid().slice(0, 8)}.${ext}`;
        await state.store.putAttachment(path, file);
        uploaded = { path, name: file.name, size: file.size, type: file.type, uploadedAt: U.nowIso(), uploadedBy: who() };
      }

      const { data, version, result } = await C.commit(state.store, (d) => {
        const errs = validate(k, values, null, d, editingId);
        if (Object.keys(errs).length) throw new C.ValidationError(errs);
        const rv = toRecordValues(k, values);
        const list = d[s.collection];
        if (editingId) {
          const rec = list.find((r) => r.id === editingId);
          if (!rec || rec.deletedAt) throw new Error("This record was deleted in the meantime.");
          if (!canEdit(rec)) throw new C.PermissionError("You can only change records you created.");
          const changes = {};
          Object.keys(rv).forEach((key) => { if (rec[key] !== rv[key]) changes[key] = [rec[key] ?? null, rv[key]]; });
          if (uploaded) { changes.attachment = [rec.attachment ? rec.attachment.name : null, uploaded.name]; rec.attachment = uploaded; }
          else if (removeAttachment && rec.attachment) { changes.attachment = [rec.attachment.name, null]; rec.attachment = null; }
          Object.assign(rec, rv, { updatedAt: U.nowIso(), updatedBy: who() });
          audit(d, "update", k, rec, changes);
          return rec;
        }
        const rec = Object.assign({ id: U.uuid() }, rv, {
          attachment: uploaded, createdAt: U.nowIso(), createdBy: who(), updatedAt: U.nowIso(), updatedBy: who()
        });
        list.push(rec);
        audit(d, "create", k, rec);
        return rec;
      });

      state.data = data;
      state.version = version;
      U.toast(`${s.singular} ${result[s.key]} ${editingId ? "updated" : "saved"}.`, "success");
      resetForm(k);
      renderAll();
    } catch (e) {
      if (uploaded) await state.store.deleteAttachment(uploaded.path);
      if (e instanceof C.ValidationError) { showErrors(k, e.errors); await reload(true); }
      handleError(e);
    } finally {
      setBusy(k, false);
    }
  }

  async function deleteRecord(k, rec) {
    const s = SC[k];
    const ok = await U.confirm(`Delete ${s.singular.toLowerCase()}?`,
      `${s.singular} ${rec[s.key]} will be hidden from all lists. It stays in the data file (marked as deleted) for traceability.`, "Delete", true);
    if (!ok) return;
    state.busy = true;
    try {
      const { data, version } = await C.commit(state.store, (d) => {
        const r = d[s.collection].find((x) => x.id === rec.id);
        if (!r || r.deletedAt) return r;
        if (!canEdit(r)) throw new C.PermissionError("You can only delete records you created.");
        r.deletedAt = U.nowIso();
        r.deletedBy = who();
        audit(d, "delete", k, r);
        return r;
      });
      state.data = data; state.version = version;
      if (state.editing[k] === rec.id) resetForm(k);
      U.toast(`${s.singular} ${rec[s.key]} deleted.`, "success");
      renderAll();
    } finally {
      state.busy = false;
    }
  }

  // ---------- Suche & Ergebnistabelle ----------
  function dateFieldOf(s) { return s.fields.find((f) => f.type === "date"); }

  function runSearch(k) {
    const s = SC[k];
    const filters = s.fields.filter((f) => f.search).map((f) => ({ f, v: $(`#${sid(k, f.name)}`).value.trim().toLowerCase() })).filter((x) => x.v);
    const mine = $(`#${sid(k, "mine")}`).checked;
    const df = dateFieldOf(s);
    const rows = state.data[s.collection]
      .filter((r) => !r.deletedAt)
      .filter((r) => filters.every(({ f, v }) => {
        const val = String(r[f.name] ?? "").toLowerCase();
        return f.search === "equals" ? val === v : val.includes(v);
      }))
      .filter((r) => !mine || (r.createdBy && state.user && r.createdBy.id === state.user.id))
      .sort((a, b) => String(b[df.name]).localeCompare(String(a[df.name])) || String(b.createdAt).localeCompare(String(a.createdAt)));
    state.lastResults[k] = rows;
    renderTable(k, rows);
  }

  function cellValue(k, f, r) {
    if (f.type === "date") return U.fmtDate(r[f.name]);
    if (f.type === "money") return U.fmtMoney(r[f.name], r.currency);
    if (f.type === "int") return r[f.name] === null || r[f.name] === undefined ? "" : U.fmtNumber(r[f.name]);
    return r[f.name] ?? "";
  }

  function renderTable(k, rows) {
    const s = SC[k];
    const table = $(`#table-${k}`);
    clear(table);
    const cols = s.fields;
    table.append(el("thead", {}, el("tr", {},
      cols.map((f) => el("th", { scope: "col", class: f.type === "money" || f.type === "int" ? "num" : null }, f.type === "file" ? "File" : f.label)),
      el("th", { scope: "col" }, "Created by"),
      el("th", { scope: "col", class: "actions-col" }, el("span", { class: "sr-only" }, "Actions")))));
    const tbody = el("tbody");
    if (!rows.length) {
      tbody.append(el("tr", {}, el("td", { colspan: String(cols.length + 2), class: "empty" }, "No records found.")));
    }
    rows.forEach((r) => {
      const editable = canEdit(r);
      tbody.append(el("tr", { class: state.editing[k] === r.id ? "row-editing" : null },
        cols.map((f) => {
          if (f.type === "file") {
            return el("td", {}, r.attachment
              ? el("button", { type: "button", class: "btn btn-small", title: r.attachment.name, onclick: guarded(() => openAttachment(r.attachment)) }, k === "stock" ? "Image" : "PDF")
              : el("span", { class: "muted" }, "–"));
          }
          if (f.name === "status") return el("td", {}, el("span", { class: `pill pill-${String(r.status).toLowerCase().replace(/\s+/g, "-")}` }, r.status));
          const low = k === "stock" && f.name === "qty" && r.qty <= cfg.lowStockThreshold;
          return el("td", { class: `${f.type === "money" || f.type === "int" ? "num" : ""} ${low ? "low" : ""}`.trim() || null }, cellValue(k, f, r));
        }),
        el("td", { class: "muted", title: r.updatedAt ? `Last change ${U.fmtDateTime(r.updatedAt)} by ${r.updatedBy ? r.updatedBy.name : ""}` : null }, r.createdBy ? r.createdBy.name : ""),
        el("td", { class: "actions-col" },
          editable ? el("button", { type: "button", class: "btn btn-small", onclick: () => startEdit(k, r) }, "Edit") : null,
          editable ? el("button", { type: "button", class: "btn btn-small btn-ghost-danger", onclick: guarded(() => deleteRecord(k, r)) }, "Delete") : null,
          editable ? null : el("span", { class: "muted lock", title: "Only the creator (or an admin) can change this record." }, "read-only"))));
    });
    table.append(tbody);
    $(`#count-${k}`).textContent = `${rows.length} ${rows.length === 1 ? "record" : "records"}`;
  }

  async function openAttachment(att) {
    const win = U.openLater();
    try { await state.store.openAttachment(att.path, win); } catch (e) { win.cancel(); throw e; }
  }

  // ---------- CSV ----------
  function csvEscape(v) {
    let s = String(v ?? "");
    if (/^[=+\-@\t\r]/.test(s)) s = `'${s}`; // CSV-/Formel-Injection in Excel verhindern
    return /[",;\n\r]/.test(s) || s.includes(cfg.csvDelimiter) ? `"${s.replace(/"/g, '""')}"` : s;
  }

  function exportCsv(k) {
    const s = SC[k];
    const rows = state.lastResults[k] || [];
    const sep = cfg.csvDelimiter || ";";
    const head = [];
    s.fields.forEach((f) => {
      if (f.type === "file") head.push(`${f.label} (file name)`);
      else if (f.type === "money") head.push(f.label, "Currency");
      else head.push(f.label);
    });
    head.push("Created by", "Created at", "Last change by", "Last change at");
    const lines = [head.map(csvEscape).join(sep)];
    rows.forEach((r) => {
      const cells = [];
      s.fields.forEach((f) => {
        if (f.type === "file") cells.push(r.attachment ? r.attachment.name : "");
        else if (f.type === "money") cells.push(r[f.name] ?? "", r.currency || "");
        else cells.push(r[f.name] ?? "");
      });
      cells.push(r.createdBy ? r.createdBy.name : "", r.createdAt || "", r.updatedBy ? r.updatedBy.name : "", r.updatedAt || "");
      lines.push(cells.map(csvEscape).join(sep));
    });
    const blob = new Blob(["﻿" + lines.join("\r\n")], { type: "text/csv;charset=utf-8" });
    U.downloadBlob(blob, `${s.collection}-${U.todayIso()}.csv`);
  }

  // ======================================================================
  // Dashboard
  // ======================================================================

  function sumByCurrency(rows) {
    const m = {};
    rows.forEach((r) => { if (typeof r.value === "number") m[r.currency] = (m[r.currency] || 0) + r.value; });
    return Object.entries(m).sort((a, b) => b[1] - a[1]);
  }

  function kpi(title, value, sub, href, tone) {
    return el("a", { class: `kpi ${tone ? `kpi-${tone}` : ""}`, href },
      el("span", { class: "kpi-title" }, title),
      el("span", { class: "kpi-value" }, value),
      el("span", { class: "kpi-sub" }, sub || " "));
  }

  function renderDashboard() {
    const page = $("#page-dashboard");
    clear(page);
    const live = (c) => state.data[c].filter((r) => !r.deletedAt);
    const q = live("quotations"), pg = live("poGermany"), pc = live("poCustomer"), st = live("stock");
    const openQ = q.filter((r) => r.status === "Open");
    const money = (rows) => sumByCurrency(rows).map(([c, v]) => U.fmtMoney(v, c)).join(" · ") || "–";
    const low = st.filter((r) => r.qty <= cfg.lowStockThreshold);

    page.append(
      el("h2", { id: "h-dashboard", class: "sr-only" }, "Overview"),
      el("div", { class: "kpis" },
        kpi("Open quotations", String(openQ.length), money(openQ), "#/quotation", "accent"),
        kpi("Closed quotations", String(q.length - openQ.length), money(q.filter((r) => r.status !== "Open")), "#/quotation"),
        kpi("Customer POs", String(pc.length), money(pc), "#/po-customer"),
        kpi("German POs", String(pg.length), money(pg), "#/po-germany"),
        kpi("Stock items", String(st.length), `${U.fmtNumber(st.reduce((a, r) => a + (r.qty || 0), 0))} pcs in total`, "#/stock"),
        kpi("Low stock", String(low.length), `≤ ${cfg.lowStockThreshold} pcs`, "#/stock", low.length ? "warn" : null)));

    const recent = state.data.audit.slice(-12).reverse();
    const actLabel = { create: "created", update: "changed", delete: "deleted", import: "imported", migrate: "copied in" };
    page.append(el("div", { class: "grid-2 dash-grid" },
      el("div", { class: "card" },
        el("div", { class: "card-head" }, el("h2", {}, "Recent activity")),
        recent.length
          ? el("ul", { class: "activity" }, recent.map((a) => el("li", {},
              el("span", { class: "act-main" }, el("strong", {}, a.user ? a.user.name : "?"), ` ${actLabel[a.action] || a.action} `, a.label || ""),
              el("time", { class: "muted", datetime: a.ts }, U.fmtDateTime(a.ts)))))
          : el("p", { class: "muted" }, "No activity yet.")),
      el("div", { class: "card" },
        el("div", { class: "card-head" }, el("h2", {}, "Low stock")),
        low.length
          ? el("ul", { class: "activity" }, low.sort((a, b) => a.qty - b.qty).map((r) => el("li", {},
              el("span", { class: "act-main" }, el("strong", {}, r.partId), ` ${r.description}`),
              el("span", { class: "low" }, `${U.fmtNumber(r.qty)} pcs`))))
          : el("p", { class: "muted" }, "All stock items are above the threshold."))));
  }

  // ======================================================================
  // Einstellungen: Speicher wechseln, Name, Backup, Import, Daten kopieren
  // ======================================================================

  async function openSettings() {
    const d = state.store ? state.store.describe() : { label: "–", detail: "" };
    const close = () => { const dlg = $("dialog.modal[open]"); if (dlg) dlg.close(); };
    const act = (label, fn, extra) => el("button", { type: "button", class: `btn ${extra || ""}`, onclick: guarded(async () => { close(); await fn(); }) }, label);

    const body = el("div", { class: "settings" },
      el("h3", {}, "Storage"),
      el("p", {}, el("strong", {}, d.label), ` – ${d.detail}`),
      el("div", { class: "settings-actions" },
        act("Change storage…", switchStorage),
        state.storeId === "localFolder" ? act("Choose another folder…", async () => { await state.store.connect(); await afterReady(); }) : null,
        state.ready ? act("Copy all data to…", copyToOtherStore) : null),

      el("h3", {}, "User"),
      state.storeId === "sharepoint"
        ? el("div", {}, el("p", {}, `Signed in as ${state.user ? `${state.user.name} (${state.user.upn})` : "–"}`),
            el("div", { class: "settings-actions" }, act("Sign out", async () => { await window.Auth.signOut(); location.reload(); })))
        : el("div", {}, el("p", {}, `Working as ${state.user ? state.user.name : "–"} (not verified)`),
            el("div", { class: "settings-actions" }, act("Change name…", async () => { await askLocalName(true); renderAll(); }))),

      el("h3", {}, "Backup"),
      el("div", { class: "settings-actions" },
        state.ready ? act("Export JSON backup", exportJson) : null,
        state.ready ? act("Import JSON backup…", importJson, "btn-ghost-danger") : null));

    await U.dialog({ title: "Settings", body, buttons: [{ label: "Close", value: null }] });
  }

  async function switchStorage() {
    const id = await chooseStorage(true);
    if (!id || id === state.storeId) return;
    await activateStore(id);
  }

  function exportJson() {
    const blob = new Blob([JSON.stringify(state.data, null, 2)], { type: "application/json" });
    U.downloadBlob(blob, `salesofloga-backup-${U.todayIso()}.json`);
    U.toast("Backup downloaded (data only, without attachments).", "info");
  }

  async function importJson() {
    const input = el("input", { type: "file", accept: "application/json,.json" });
    const file = await new Promise((resolve) => { input.addEventListener("change", () => resolve(input.files[0] || null)); input.click(); });
    if (!file) return;
    let imported;
    try { imported = C.normalize(JSON.parse(await file.text())); } catch (e) { throw new Error(`Not a valid backup file: ${e.message}`); }
    const counts = C.COLLECTIONS.map((c) => `${imported[c].filter((r) => !r.deletedAt).length} ${c}`).join(", ");
    const ok = await U.confirm("Replace all data?", `The current data will be REPLACED by the backup (${counts}). The previous state stays in the backups/version history. Attachments are not part of a JSON backup.`, "Replace data", true);
    if (!ok) return;
    const { data, version } = await C.commit(state.store, (d) => {
      const seen = new Set(d.audit.map((a) => `${a.ts}|${a.action}|${a.recordId}`));
      const audits = d.audit.concat(imported.audit.filter((a) => !seen.has(`${a.ts}|${a.action}|${a.recordId}`)));
      C.COLLECTIONS.forEach((c) => { d[c] = imported[c]; });
      d.audit = audits;
      d.audit.push({ ts: U.nowIso(), user: who(), action: "import", entity: "all", recordId: null, label: `backup ${file.name}` });
    });
    state.data = data; state.version = version;
    renderAll();
    U.toast("Backup imported.", "success");
  }

  /** Daten + Anhänge in ein anderes Backend kopieren (z. B. lokal -> SharePoint). */
  async function copyToOtherStore() {
    const opts = storeOptions().filter((o) => o.id !== state.storeId && o.available);
    if (!opts.length) { U.toast("No other storage is available (SharePoint not configured?).", "error"); return; }
    let target = opts[0].id;
    const body = el("div", {},
      el("p", {}, "Copies all records and attachments into another storage. The target must be empty."),
      opts.map((o) => el("label", { class: "check" }, el("input", { type: "radio", name: "target", value: o.id, checked: o.id === target, onchange: () => { target = o.id; } }), ` ${o.title}`)));
    const res = await U.dialog({ title: "Copy all data", body, buttons: [{ label: "Cancel", value: null }, { label: "Copy", value: "ok", primary: true }] });
    if (res !== "ok") return;

    const to = makeStore(target);
    if (target === "sharepoint") {
      if (!(await window.Auth.init(cfg)) && !(await window.Auth.signIn())) return;
    }
    let r = await to.init();
    if (!r.ready && (r.action === "connect" || r.action === "reconnect")) r = r.action === "connect" ? await to.connect() : await to.reconnect();
    if (!r.ready) throw new Error(r.message || "Target storage is not ready.");

    const { data: targetData } = await to.read();
    if (!C.isEmpty(targetData)) throw new Error("The target storage already contains data. Copy aborted to avoid overwriting.");

    const src = state.data;
    const withFiles = C.COLLECTIONS.flatMap((c) => src[c].filter((x) => x.attachment));
    let done = 0;
    for (const rec of withFiles) {
      const blob = await state.store.getAttachmentBlob(rec.attachment.path);
      try { await to.putAttachment(rec.attachment.path, blob); } catch (e) { if (!(e instanceof C.ConflictError)) throw e; }
      done++;
      U.toast(`Copying attachments ${done}/${withFiles.length}…`, "info");
    }
    await C.commit(to, (d) => {
      if (!C.isEmpty(d)) throw new Error("The target storage received data in the meantime. Copy aborted.");
      C.COLLECTIONS.forEach((c) => { d[c] = src[c]; });
      d.audit = src.audit.concat([{ ts: U.nowIso(), user: who(), action: "migrate", entity: "all", recordId: null, label: `from ${state.store.describe().label}` }]);
    });
    const sw = await U.confirm("Copy finished", `${C.COLLECTIONS.reduce((a, c) => a + src[c].length, 0)} records and ${withFiles.length} attachments copied. Switch to the new storage now?`, "Switch now");
    if (sw) await activateStore(target);
  }

  // ======================================================================
  // Start
  // ======================================================================

  async function start() {
    $("#app-title").textContent = cfg.appTitle;
    buildShell();
    route();
    try {
      let id = localStorage.getItem(LS.store);
      const opts = storeOptions();
      if (!id || !opts.some((o) => o.id === id && o.available)) {
        id = await chooseStorage(false);
        if (!id) { showGate(null, "No storage selected."); return; }
      }
      await activateStore(id);
    } catch (e) {
      handleError(e);
      showGate(null, (e && e.message) || "Start failed.");
    }
  }

  // Für Tests/Diagnose
  window.SalesApp = { state, reload, activateStore };

  document.addEventListener("DOMContentLoaded", start);
})();
