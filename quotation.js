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

/* ================= SAVE QUOTATION ================= */
window.submitQuotation = async function () {

  const qNo = document.getElementById("qNo").value.trim();
  if (!qNo) {
    alert("Quotation No is required");
    return;
  }

  await setDoc(doc(db, "quotations", qNo), {
    quotationNo: qNo,
    quotationDate: document.getElementById("qDate").value,
    customer: document.getElementById("qCustomer").value,
    status: document.getElementById("qStatus").value,
    value: Number(document.getElementById("qValue").value),
    createdAt: serverTimestamp()
  });

  alert("Quotation saved successfully");
};

/* ================= SEARCH QUOTATION ================= */
window.searchQuotation = async function () {

  const qNo = document.getElementById("searchQNo").value.trim();
  const customer = document.getElementById("searchCustomer").value;

  const tableBody = document.getElementById("resultTableBody");
  tableBody.innerHTML = "";
  document.getElementById("searchResultArea").style.display = "block";

  // Search by Quotation No
  if (qNo) {
    const ref = doc(db, "quotations", qNo);
    const snap = await getDoc(ref);

    if (!snap.exists()) {
      alert("Quotation not found");
      return;
    }

    addRow(snap.data());
    return;
  }

  // Search by Customer
  let q = query(collection(db, "quotations"));
  if (customer) {
    q = query(collection(db, "quotations"), where("customer", "==", customer));
  }

  const snap = await getDocs(q);
  snap.forEach(doc => addRow(doc.data()));
};

/* ================= ADD RESULT ROW ================= */
function addRow(data) {
  const row = `
    <tr>
      <td>${data.quotationDate || ""}</td>
      <td>${data.quotationNo || ""}</td>
      <td>${data.customer || ""}</td>
      <td>${data.status || ""}</td>
      <td>${data.value || ""}</td>
      <td>-</td>
    </tr>
  `;
  document.getElementById("resultTableBody").insertAdjacentHTML("beforeend", row);
}

/* ================= CLEAR ================= */
window.clearQuotation = function () {
  ["qDate", "qNo", "qCustomer", "qStatus", "qValue"].forEach(id => {
    document.getElementById(id).value = "";
  });
};

window.clearSearch = function () {
  document.getElementById("searchQNo").value = "";
  document.getElementById("searchCustomer").value = "";
  document.getElementById("searchResultArea").style.display = "none";
};
