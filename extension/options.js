const portInput = document.getElementById("port");
const tokenInput = document.getElementById("token");
const statusEl = document.getElementById("status");

chrome.storage.local.get({ port: 49160, token: "" }, (cfg) => {
  portInput.value = cfg.port;
  tokenInput.value = cfg.token;
});

document.getElementById("simpan").addEventListener("click", () => {
  const port = Math.trunc(Number(portInput.value)) || 49160;
  chrome.storage.local.set(
    { port, token: tokenInput.value.trim() },
    () => {
      statusEl.textContent = "Tersimpan";
      setTimeout(() => (statusEl.textContent = ""), 2000);
    },
  );
});
