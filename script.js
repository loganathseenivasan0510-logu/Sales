const WEB_APP_URL = "https://script.google.com/macros/s/AKfycbwSTRFx7IH9HzkhWeD_Rx8Vhm5Pc1tRvjLNh2u6C554Ysht4kF0NlKjecZv2GW0LDN_wg/exec";


function showPage(pageId, element) {

    // Hide all pages
    document.querySelectorAll('.page').forEach(page => {
        page.classList.remove('active');
    });

    // Show selected page
    document.getElementById(pageId).classList.add('active');

    // Remove active state from all menu items
    document.querySelectorAll('.menu-item').forEach(item => {
        item.classList.remove('active');
    });

    // Highlight clicked menu
    element.classList.add('active');
}

function addItemRow() {
    const table = document.getElementById("itemTable");
    const rowCount = table.rows.length + 1;

    const row = table.insertRow();
    row.innerHTML = `
        <td>${rowCount}</td>
        <td><input></td>
        <td><input></td>
        <td><input type="number"></td>
        <td><input type="number"></td>
        <td><input type="number"></td>
    `;
}

function addInvoiceRow() {
    const table = document.getElementById("invoiceItems");
    const rowNo = table.rows.length + 1;

    const row = table.insertRow();
    row.innerHTML = `
        <td>${rowNo}</td>
        <td><input></td>
        <td><input></td>
        <td><input type="number"></td>
        <td><input type="number"></td>
        <td><input></td>
        <td><input type="number"></td>
    `;
}







//Required fields check popup
function allFieldsFilled() {
    const fields = ["qDate","qNo","qCustomer","qStatus","qValue"];
    return fields.every(id => document.getElementById(id).value.trim() !== "");
}
//Clear button clears everything
function clearQuotation() {
    document.querySelectorAll("#quotation input, #quotation select")
        .forEach(el => el.value = "");
}

function renamePdf(file, qNo) {
    return new File([file], qNo + ".pdf", {
        type: file.type,
        lastModified: Date.now()
    });
}

// ---------------- SEARCH QUOTATION ----------------

function searchQuotation() {
    const qNo = document.getElementById("searchQNo").value.trim();
    const customer = document.getElementById("searchCustomer").value.trim();

    if (!qNo && !customer) {
        alert("Fill the Quotation No or Customer name");
        return;
    }

    fetch(WEB_APP_URL, {
        method: "POST",
        body: JSON.stringify({
            action: "search",
            qNo,
            customer
        })
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

// Clear Search Button Logic

function clearSearch() {
    document.getElementById("searchQNo").value = "";
    document.getElementById("searchCustomer").value = "";
    document.getElementById("searchResultArea").style.display = "none";
}





// -------- CT INDIA STOCKS --------

function clearStock() {
    document.querySelectorAll(
        "#CTIndiaStocks input"
    ).forEach(el => el.value = "");
}

function submitStock() {
    if (!stockFieldsFilled()) {
        alert("Please fill all stock fields");
        return;
    }

    alert("Stock submitted successfully");
    clearStock();

    // Later: connect to Google Apps Script (same as quotation)
}

function stockFieldsFilled() {
    const fields = [
        "stockDate",
        "stockPartId",
        "stockDescription",
        "stockQty"
    ];
    return fields.every(id => document.getElementById(id).value.trim() !== "");
}

function searchStock() {
    const partId = document.getElementById("searchPartId").value.trim();
    const desc = document.getElementById("searchPartDesc").value.trim();

    if (!partId && !desc) {
        alert("Enter Part ID or Description");
        return;
    }

    alert("Stock search triggered");
}

function clearStockSearch() {
    document.getElementById("searchPartId").value = "";
    document.getElementById("searchPartDesc").value = "";
}

function allFieldsFilled() {
  const fields = ["qDate", "qNo", "qCustomer", "qStatus", "qValue"];
  return fields.every(id => document.getElementById(id).value.trim() !== "");
}

function clearQuotation() {
  document.querySelectorAll("#quotation input, #quotation select")
    .forEach(el => el.value = "");
}

function toBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result.split(",")[1]);
    reader.onerror = reject;
    reader.readAsDataURL(file);
  });
}

// ✅ MAIN SUBMIT FUNCTION
async function submitQuotation() {

  if (!allFieldsFilled()) {
    alert("⚠ Please fill all required fields!");
    return;
  }

  const qDate = document.getElementById("qDate").value;
  const qNo = document.getElementById("qNo").value;
  const customer = document.getElementById("qCustomer").value;
  const status = document.getElementById("qStatus").value;
  const value = document.getElementById("qValue").value;

  const pdfFile = document.getElementById("qPdf").files[0];

  let pdfBase64 = "";
  let pdfName = "";

  if (pdfFile) {
    pdfBase64 = await toBase64(pdfFile);
    pdfName = qNo + ".pdf";
  }

  fetch(WEB_APP_URL, {
    method: "POST",
    body: JSON.stringify({
      qDate,
      qNo,
      customer,
      status,
      value,
      pdfBase64,
      pdfName
    })
  })
    .then(res => res.json())
    .then(response => {
      if (response.success) {
        alert("✅ Successfully Submitted!!");
        clearQuotation();
      } else {
        alert("❌ Error: " + response.message);
      }
    })
    .catch(err => {
      alert("❌ Submission Failed!");
      console.log(err);
    });
}


