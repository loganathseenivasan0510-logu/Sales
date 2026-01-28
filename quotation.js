import { db } from "./firebase.js";
import {
  doc,
  setDoc,
  getDoc,
  collection,
  getDocs,
  query,
  where,
  serverTimestamp
} from "https://www.gstatic.com/firebasejs/10.7.1/firebase-firestore.js";

console.log("quotation.js loaded");

const WEB_APP_URL =
  "https://script.google.com/macros/s/AKfycbxQqAlHZy2LvVhR-0HsS-Hhy9SUNldxGFJ1RaAwan5mZ8MR_gAiEiaaIHBSbnDCdGZC/exec";

/* ================= SUBMIT QUOTATION ================= */
async function submitQuotation() {

  console.log("Submit clicked");

  const qNo = document.getElementById("qNo").value.trim();
  if (!qNo) {
    alert("Quotation No required");
    return;
  }

  const fileInput = document.getElementById("qPdf");
  if (fileInput.files.length === 0) {
    alert("Please upload PDF");
    return;
  }

  const file = fileInput.files[0];
  const reader = new FileReader();

  reader.onload = async () => {
    try {
      const base64 = reader.result.split(",")[1];

      // 1️⃣ Upload PDF
      const driveRes = await fetch(WEB_APP_URL, {
        method: "POST",
        body: JSON.stringify({
          pdfBase64: base64,
          pdfName: qNo + ".pdf"
        })
      });

      const text = await driveRes.text();
console.log("RAW RESPONSE:", text);

let driveData;
try {
  driveData = JSON.parse(text);
} catch (e) {
  alert("Server did not return JSON. Check Apps Script.");
  return;
}
      if (!driveData.success) {
        alert("PDF upload failed");
        return;
      }

      // 2️⃣ Save to Firestore
      await setDoc(doc(db, "quotations", qNo), {
        quotationNo: qNo,
        quotationDate: document.getElementById("qDate").value,
        customer: document.getElementById("qCustomer").value,
        status: document.getElementById("qStatus").value,
        value: Number(document.getElementById("qValue").value),
        pdfUrl: driveData.pdfUrl,
        createdAt: serverTimestamp()
      });

      alert("Quotation saved successfully");
      clearQuotation();

    } catch (err) {
      console.error(err);
      alert("Error occurred. Check console.");
    }
  };

  reader.readAsDataURL(file);
}

/* ================= CLEAR ================= */
function clearQuotation() {
  ["qDate","qNo","qCustomer","qStatus","qValue"]
    .forEach(id => document.getElementById(id).value = "");
  document.getElementById("qPdf").value = "";
}

/* ================= BUTTON BINDING ================= */
document.addEventListener("DOMContentLoaded", () => {
  console.log("DOM loaded");

  const btn = document.getElementById("submitQuotationBtn");
  if (!btn) {
    console.error("Submit button not found");
    return;
  }

  btn.addEventListener("click", submitQuotation);
  console.log("Submit button bound");
});
