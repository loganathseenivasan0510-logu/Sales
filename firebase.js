// Import Firebase App Module
import { initializeApp } from
  "https://www.gstatic.com/firebasejs/10.7.1/firebase-app.js";

// Import Firestore Module
import { getFirestore } from
  "https://www.gstatic.com/firebasejs/10.7.1/firebase-firestore.js";
import { getStorage } from 
  "https://www.gstatic.com/firebasejs/10.7.1/firebase-storage.js";
// 🔥 Paste your Firebase Config here
const firebaseConfig = {
  apiKey: "AIzaSyDBIzNoDGZjTpey9IFezHC1fPcbSG28cPk",
  authDomain: "quotationdashboard1.firebaseapp.com",
  projectId: "quotationdashboard1",
  storageBucket: "quotationdashboard1.firebasestorage.app",
  messagingSenderId: "503074048994",
  appId: "1:503074048994:web:4e7727745165f6de3547bb"
};

// Initialize Firebase
const app = initializeApp(firebaseConfig);

// Initialize Firestore Database
export const db = getFirestore(app);
export const storage = getStorage(app);
