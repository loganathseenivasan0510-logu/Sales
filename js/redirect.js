/* Redirect-Bridge für MSAL v5 (siehe redirect.html) */
window.msalRedirectBridge.broadcastResponseToMainFrame().catch(function (error) {
  console.error("Sign-in response could not be passed back to the app:", error);
  document.body.textContent = "Sign-in could not be completed. Please close this window and try again.";
});
