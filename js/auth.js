/*
 * Anmeldung mit Microsoft Entra ID (SSO) über MSAL.js v5 (lokal eingebunden unter vendor/).
 * - Silent SSO zuerst (bestehende Microsoft-Sitzung im Browser, z. B. Edge mit Firmenkonto)
 * - sonst Popup, bei Popup-Blocker Redirect
 * - Tokens nur im sessionStorage (werden beim Schließen des Tabs verworfen)
 */
(function () {
  "use strict";

  const Auth = { pca: null, account: null };

  function redirectUri(cfg) {
    return new URL(cfg.sharepoint.redirectPath || "redirect.html", window.location.href).href;
  }

  Auth.init = async function (cfg) {
    if (!window.msal) throw new Error("MSAL library missing (vendor/msal-browser.min.js).");
    if (location.protocol === "file:") {
      throw new Error("SharePoint sign-in needs the app to be served via http(s) – e.g. start-local-server.ps1 or an internal web server.");
    }
    const s = cfg.sharepoint;
    Auth.scopes = s.scopes;
    Auth.pca = new msal.PublicClientApplication({
      auth: {
        clientId: s.clientId,
        authority: `https://login.microsoftonline.com/${s.tenantId}`,
        redirectUri: redirectUri(cfg),
        postLogoutRedirectUri: redirectUri(cfg)
      },
      cache: { cacheLocation: "sessionStorage" }
    });
    await Auth.pca.initialize();
    const result = await Auth.pca.handleRedirectPromise();
    if (result && result.account) Auth.account = result.account;
    if (!Auth.account) {
      const accounts = Auth.pca.getAllAccounts();
      if (accounts.length) Auth.account = accounts[0];
    }
    if (!Auth.account) {
      try {
        const r = await Auth.pca.ssoSilent({ scopes: Auth.scopes });
        Auth.account = r.account;
      } catch (e) { /* keine bestehende Sitzung – Anmeldung per Klick nötig */ }
    }
    if (Auth.account) Auth.pca.setActiveAccount(Auth.account);
    return !!Auth.account;
  };

  Auth.signIn = async function () {
    try {
      const r = await Auth.pca.loginPopup({ scopes: Auth.scopes, prompt: "select_account" });
      Auth.account = r.account;
      Auth.pca.setActiveAccount(r.account);
      return true;
    } catch (e) {
      if (e && (e.errorCode === "popup_window_error" || e.errorCode === "empty_window_error")) {
        await Auth.pca.loginRedirect({ scopes: Auth.scopes });
        return false; // Seite wird umgeleitet
      }
      throw e;
    }
  };

  Auth.signOut = async function () {
    if (!Auth.pca || !Auth.account) return;
    await Auth.pca.logoutPopup({ account: Auth.account });
    Auth.account = null;
  };

  Auth.getToken = async function () {
    if (!Auth.account) throw new Error("Not signed in.");
    const req = { scopes: Auth.scopes, account: Auth.account };
    try {
      return (await Auth.pca.acquireTokenSilent(req)).accessToken;
    } catch (e) {
      if (e instanceof msal.InteractionRequiredAuthError) {
        return (await Auth.pca.acquireTokenPopup(req)).accessToken;
      }
      throw e;
    }
  };

  /** Identität für Eigentümer-Prüfung und Audit-Log */
  Auth.user = function () {
    if (!Auth.account) return null;
    const a = Auth.account;
    return {
      id: `aad:${a.localAccountId || a.homeAccountId}`,
      name: a.name || a.username,
      upn: (a.username || "").toLowerCase()
    };
  };

  window.Auth = Auth;
})();
