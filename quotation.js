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

const WEB_APP_URL =
  "https://script.google.com/macros/s/AKfycbxQqAlHZy2LvVhR-0HsS-Hhy9SUNldxGFJ1RaAwan5mZ8MR_gAiEiaaIHBSbnDCdGZC/exec";

/* ================= SUBMIT QUOTATION ================= */
window.submitQuotation = async function () {

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

  reader.onload = async function () {

    const base64 = reader.result.split(",")[1];

    // 🔹 1. Upload PDF to Google Drive
    const driveRes = await fetch(WEB_APP_URL, {
      method: "POST",
      body: JSON.stringify({
        pdfBase64: base64,
        pdfName: qNo + ".pdf"
      })
    });

    const driveData = await driveRes.json();

    if (!driveData.success) {
      alert("PDF upload failed");
      return;
    }

    // 🔹 2. Save data to Firestore
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
  };

  reader.readAsDataURL(file);
};

/* ================= SEARCH ================= */
window.searchQuotation = async function () {

  const qNo = document.getElementById("searchQNo").value.trim();
  const customer = document.getElementById("searchCustomer").value;

  const tableBody = document.getElementById("resultTableBody");
  tableBody.innerHTML = "";
  document.getElementById("searchResultArea").style.display = "block";

  if (qNo) {
    const snap = await getDoc(doc(db, "quotations", qNo));
    if (!snap.exists()) {
      alert("Quotation not found");
      return;
    }
    addRow(snap.data());
    return;
  }

  let q = collection(db, "quotations");
  if (customer) q = query(q, where("customer", "==", customer));

  const snap = await getDocs(q);
  snap.forEach(d => addRow(d.data()));
};

/* ================= RESULT ROW ================= */
function addRow(data) {
  const row = `
    <tr>
      <td>${data.quotationDate || ""}</td>
      <td>${data.quotationNo || ""}</td>
      <td>${data.customer || ""}</td>
      <td>${data.status || ""}</td>
      <td>${data.value || ""}</td>
      <td>
        <a href="${data.pdfUrl}" target="_blank">View</a>
      </td>
    </tr>
  `;
  document.getElementById("resultTableBody")
    .insertAdjacentHTML("beforeend", row);
}

/* ================= CLEAR ================= */
window.clearQuotation = function () {
  ["qDate","qNo","qCustomer","qStatus","qValue"]
    .forEach(id => document.getElementById(id).value = "");
  document.getElementById("qPdf").value = "";
};

window.clearSearch = function () {
  document.getElementById("searchQNo").value = "";
  document.getElementById("searchCustomer").value = "";
  document.getElementById("searchResultArea").style.display = "none";
};
