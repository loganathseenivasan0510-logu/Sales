import { db } from "./firebase.js";
import { doc, setDoc, serverTimestamp } from "https://www.gstatic.com/firebasejs/10.7.1/firebase-firestore.js";

const WEB_APP_URL = "https://script.google.com/macros/s/AKfycbxQqAlHZy2LvVhR-0HsS-Hhy9SUNldxGFJ1RaAwan5mZ8MR_gAiEiaaIHBSbnDCdGZC/exec";

// Submit quotation
async function submitQuotation() {
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

      // Upload PDF
      const driveRes = await fetch(WEB_APP_URL, {
        method: "POST",
        body: JSON.stringify({ pdfBase64: base64, pdfName: qNo + ".pdf" })
      });

      const text = await driveRes.text();
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

      // Save to Firestore
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
      alert("Error occurred. Check console.");
      console.error(err);
    }
  };

  reader.readAsDataURL(file);
}

// Clear form
function clearQuotation() {
  ["qDate", "qNo", "qCustomer", "qStatus", "qValue"].forEach(id => document.getElementById(id).value = "");
  document.getElementById("qPdf").value = "";
}

// Search logic
function searchQuotation() {
  const qNo = document.getElementById("searchQNo").value.trim();
  const customer = document.getElementById("searchCustomer").value.trim();

  if (!qNo && !customer) {
    alert("Fill the Quotation No or Customer name");
    return;
  }

  fetch(WEB_APP_URL, {
    method: "POST",
    body: JSON.stringify({ action: "search", qNo, customer })
  })
    .then(res => res.json())
    .then(data => {
      if (!data.success || data.results.length === 0) {
        alert("No data found !!");
        return;
      }
      renderSearchResults(data.results);
    })
    .catch(() => alert("Error fetching data"));
}

function renderSearchResults(rows) {
  const area = document.getElementById("searchResultArea");
  const tbody = document.getElementById("resultTableBody");

  tbody.innerHTML = "";
  area.style.display = "block";

  rows.forEach(r => {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td>${r.qDate}</td>
      <td>${r.qNo}</td>
      <td>${r.customer}</td>
      <td>${r.status}</td>
      <td>${r.value}</td>
      <td>${r.pdf || "No PDF"}</td>
    `;
    tbody.appendChild(tr);
  });
}

// Clear search form
function clearSearch() {
  document.getElementById("searchQNo").value = "";
  document.getElementById("searchCustomer").value = "";
  document.getElementById("searchResultArea").style.display = "none";
}
