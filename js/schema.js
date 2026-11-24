/*
 * Fachliches Datenmodell. Formulare, Suche, Tabellen, Validierung und CSV-Export
 * werden aus diesen Definitionen erzeugt – neue Felder nur hier ergänzen.
 *
 * Feldtypen: date, text, select, money (Betrag + Währung), int, file
 */
(function () {
  "use strict";
  const cfg = window.APP_CONFIG;

  const PDF = { accept: "application/pdf,.pdf", mimes: ["application/pdf"], exts: ["pdf"] };
  const IMG = { accept: "image/png,image/jpeg,image/webp", mimes: ["image/png", "image/jpeg", "image/webp"], exts: ["png", "jpg", "jpeg", "webp"] };
  const REF = /^[A-Za-z0-9][A-Za-z0-9 ._\-\/]*$/;

  window.SCHEMAS = {
    quotation: {
      collection: "quotations",
      page: "quotation",
      nav: "Quotation Entry",
      singular: "Quotation",
      key: "qNo",
      fields: [
        { name: "qDate", label: "Quotation Date", type: "date", required: true },
        { name: "qNo", label: "Quotation No", type: "text", required: true, unique: true, maxLength: 40, pattern: REF, search: "contains", placeholder: "e.g. QT-2026-0815" },
        { name: "customer", label: "Customer", type: "select", options: () => cfg.customers, required: true, search: "equals" },
        { name: "status", label: "Status", type: "select", options: () => cfg.statuses.quotation, required: true, search: "equals" },
        { name: "value", label: "Value", type: "money", required: true },
        { name: "attachment", label: "Quotation PDF", type: "file", folder: "quotations", ...PDF }
      ]
    },

    poGermany: {
      collection: "poGermany",
      page: "po-germany",
      nav: "PO Germany Entry",
      singular: "German PO",
      key: "poNo",
      fields: [
        { name: "poDate", label: "PO Date", type: "date", required: true },
        { name: "poNo", label: "German PO No", type: "text", required: true, unique: true, maxLength: 40, pattern: REF, search: "contains" },
        { name: "customer", label: "Customer", type: "select", options: () => cfg.customers, required: true, search: "equals" },
        { name: "status", label: "Status", type: "select", options: () => cfg.statuses.po, required: true, search: "equals" },
        { name: "value", label: "Value", type: "money", required: true },
        { name: "attachment", label: "PO PDF", type: "file", folder: "po-germany", ...PDF }
      ]
    },

    poCustomer: {
      collection: "poCustomer",
      page: "po-customer",
      nav: "PO Customer Entry",
      singular: "Customer PO",
      key: "poNo",
      fields: [
        { name: "poDate", label: "PO Date", type: "date", required: true },
        { name: "qNo", label: "Quotation No", type: "text", maxLength: 40, pattern: REF, search: "contains", suggestFrom: "quotations", hint: "Links the PO to a quotation – customer is filled in automatically." },
        { name: "poNo", label: "Customer PO No", type: "text", required: true, unique: true, maxLength: 40, pattern: REF, search: "contains" },
        { name: "customer", label: "Customer", type: "select", options: () => cfg.customers, required: true, search: "equals" },
        { name: "status", label: "Status", type: "select", options: () => cfg.statuses.po, required: true, search: "equals" },
        { name: "value", label: "Value", type: "money", required: true },
        { name: "attachment", label: "PO PDF", type: "file", folder: "po-customer", ...PDF }
      ]
    },

    stock: {
      collection: "stock",
      page: "stock",
      nav: "CT India Stocks",
      singular: "Stock item",
      key: "partId",
      fields: [
        { name: "stockDate", label: "Stock Update Date", type: "date", required: true },
        { name: "partId", label: "Part ID", type: "text", required: true, unique: true, maxLength: 40, pattern: REF, search: "contains" },
        { name: "description", label: "Part Description", type: "text", required: true, maxLength: 200, search: "contains" },
        { name: "qty", label: "Quantity", type: "int", required: true, min: 0, max: 1000000 },
        { name: "attachment", label: "Image", type: "file", folder: "stock", ...IMG }
      ]
    }
  };
})();
