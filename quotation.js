import { db, storage } from "./firebase.js";
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

import {
  ref,
  uploadBytes,
  getDownloadURL
} from "https://www.gstatic.com/firebasejs/10.7.1/firebase-storage.js";

/* ================= SAVE QUOTATION ================= */
window.submitQuotation = async function () {

  const qNo = document.getElementById("qNo").value.trim();
  if (!qNo) {
    alert("Quotation No is required");
    return;
  }

  const fileInput = document.getElementById("qPdf");
  let pdfUrl = "";

  // ---------- PDF UPLOAD ----------
  if (fileInput.files.length > 0) {
    const file = fileInput.files[0];
    const pdfRef = ref(storage, `quotations/${qNo}.pdf`);

    await uploadBytes(pdfRef, file);
    pdfUrl = await getDownloadURL(pdfRef);
  }

  // ---------- SAVE FIRESTORE ----------
  await setDoc(doc(db, "quotations", qNo), {
    quotationNo: qNo,
    quotationDate: document.getElementById("qDate").value,
    customer: document.getElementById("qCustomer").value,
    status: document.getElementById("qStatus").value,
    value: Number(document.getElementById("qValue").value),
    pdfUrl: pdfUrl,
    createdAt: serverTimestamp()
  });

  alert("Quotation saved with PDF");
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
  if (customer) {
    q = query(q, where("customer", "==", customer));
  }

  const snap = await getDocs(q);
  snap.forEach(d => addRow(d.data()));
};

/* ================= RESULT ROW ================= */
function addRow(data) {
  const pdfLink = data.pdfUrl
    ? `<a href="${data.pdfUrl}" target="_blank">View</a>`
    : "-";

  const row = `
    <tr>
      <td>${data.quotationDate || ""}</td>
      <td>${data.quotationNo || ""}</td>
      <td>${data.customer || ""}</td>
      <td>${data.status || ""}</td>
      <td>${data.value || ""}</td>
      <td>${pdfLink}</td>
    </tr>
  `;

  document.getElementById("resultTableBody")
    .insertAdjacentHTML("beforeend", row);
}

/* ================= CLEAR ================= */
window.clearQuotation = function () {
  ["qDate", "qNo", "qCustomer", "qStatus", "qValue"].forEach(id => {
    document.getElementById(id).value = "";
  });
  document.getElementById("qPdf").value = "";
};

window.clearSearch = function () {
  document.getElementById("searchQNo").value = "";
  document.getElementById("searchCustomer").value = "";
  document.getElementById("searchResultArea").style.display = "none";
};
