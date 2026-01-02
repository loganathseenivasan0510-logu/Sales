const WEB_APP_URL = "https://script.google.com/macros/s/AKfycbxQqAlHZy2LvVhR-0HsS-Hhy9SUNldxGFJ1RaAwan5mZ8MR_gAiEiaaIHBSbnDCdGZC/exec";

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

async function submitQuotation() {

    function formatDateDMY(dateStr) {
    if (!dateStr) return "";
    const [y, m, d] = dateStr.split("-");
    return `${d}-${m}-${y}`;
}


    if (!allFieldsFilled()) {
        alert("Kindly fill all the fields");
        return;
    }

    const qNo = document.getElementById("qNo").value.trim();

    //if (await quotationExists(qNo)) {
      //  alert("Quotation No already exists");
        //return;
    //}

    const fileInput = document.getElementById("qPdf");
    let file = fileInput.files[0];

    if (!validatePdf(file)) return;

    if (file) {
        file = renamePdf(file, qNo);
    }

    const reader = new FileReader();

    reader.onload = function () {

        const base64File = file ? reader.result.split(",")[1] : "";

        const data = {
            qDate: formatDateDMY(document.getElementById("qDate").value),
            qNo: qNo,
            customer: document.getElementById("qCustomer").value,
            status: document.getElementById("qStatus").value,
            value: document.getElementById("qValue").value,
            pdfBase64: base64File,
            pdfName: file ? file.name : ""
        };

        fetch("https://script.google.com/macros/s/AKfycbxQqAlHZy2LvVhR-0HsS-Hhy9SUNldxGFJ1RaAwan5mZ8MR_gAiEiaaIHBSbnDCdGZC/exec", {
            method: "POST",
            body: JSON.stringify(data)
        })
       .then(res => res.json())
.then(result => {
    if (result.success) {
        alert(result.message);
        clearQuotation();
    } else if (result.message === "DUPLICATE") {
        alert("Quotation No already exists");
    } else {
        alert("Error: " + result.message);
    }
})


        .catch(() => alert("Error saving quotation"));
    };

    if (file) reader.readAsDataURL(file);
    else reader.onload();
}


function validatePdf(file) {
    if (!file) return true;
    if (file.type !== "application/pdf") {
        alert("Select proper PDF file");
        return false;
    }
    return true;
}
//Quotation no Already exists
async function quotationExists(qNo) {
    const res = await fetch(
        "https://script.google.com/macros/s/AKfycbzTPAgzE_BadrDmg3MH8lKxLq8vIVT1zVhP0OmYS4pGhx51uIYMQdr54GgCfmrZbVUJ/exec?qNo=" + qNo
    );
    const data = await res.json();
    return data.exists === true;
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



