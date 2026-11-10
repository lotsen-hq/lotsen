// Exchanges the one-time code from the link for the UI credential, which the page keeps for this tab only.
const credentialKey = "lotsen.uiCredential";
const status = document.getElementById("status");

async function connect() {
   const code = new URLSearchParams(location.hash.slice(1)).get("code");
   if (code) {
      // Drop the spent code from the address bar and the history.
      history.replaceState(null, "", location.pathname + location.search);
      sessionStorage.removeItem(credentialKey);
      const response = await fetch("/api/ui-credential", {
         method: "POST",
         headers: { "Content-Type": "application/json" },
         body: JSON.stringify({ code }),
      });
      if (response.ok) {
         const { credential } = await response.json();
         sessionStorage.setItem(credentialKey, credential);
      }
   }

   status.textContent = sessionStorage.getItem(credentialKey) ? "Connected to the lotsen server." : "This link has expired or was already used. Run `lotsen open` to open lotsen.";
}

connect().catch(() => {
   status.textContent = "Could not reach the lotsen server. Run `lotsen open` to open lotsen.";
});
