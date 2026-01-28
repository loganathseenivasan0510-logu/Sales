import { db } from "./firebase.js";

import {
  doc,
  setDoc
} from "https://www.gstatic.com/firebasejs/10.7.1/firebase-firestore.js";

window.submitQuotation = async function () {

  let quotationNo =
    document.getElementById("quotationNo").value;

  await setDoc(doc(db, "quotations", quotationNo), {

    customer: document.getElementById("customer").value,
    status: document.getElementById("status").value,
    value: Number(document.getElementById("value").value),

    timestamp: new Date()
  });

  alert("Quotation Saved Successfully!");
};

import {
  getDoc
} from "https://www.gstatic.com/firebasejs/10.7.1/firebase-firestore.js";

window.searchQuotation = async function () {

  let searchNo =
    document.getElementById("searchQuotationNo").value;

  const ref = doc(db, "quotations", searchNo);
  const snap = await getDoc(ref);

  if (snap.exists()) {

    let data = snap.data();

    document.getElementById("customer").value = data.customer;
    document.getElementById("status").value = data.status;
    document.getElementById("value").value = data.value;

    alert("Quotation Found!");

  } else {
    alert("Quotation Not Found!");
  }
};
