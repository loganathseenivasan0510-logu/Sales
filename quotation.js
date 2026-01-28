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
