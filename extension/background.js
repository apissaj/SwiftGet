// Service worker MV3: jembatan antara panel di halaman dan endpoint lokal
// SwiftGet. Tugasnya menambahkan yang tidak bisa diakses halaman: cookie
// sesi browser (chrome.cookies) dan token endpoint (chrome.storage).
// Konfigurasi per situs (sites.json) dibaca langsung oleh content script.
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {

  if (msg && msg.type === "swiftget-open-options") {
    chrome.runtime.openOptionsPage();
    return;
  }

  if (msg && msg.type === "swiftget-get-config") {
    chrome.storage.local.get({ port: 49160 }, (cfg) => sendResponse({ port: cfg.port }));
    return true;
  }

  if (msg && msg.type === "swiftget-add") {
    terimaUnduhan(msg).then(sendResponse);
    return true; // respons async
  }
});

async function terimaUnduhan({ url, referer }) {
  const cfg = await chrome.storage.local.get({ port: 49160, token: "" });

  let cookies = "";
  try {
    const jar = await chrome.cookies.getAll({ url });
    cookies = jar.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch (ex) {
    cookies = ""; // cookie gagal dibaca bukan alasan batal; server mungkin publik
  }

  try {
    const resp = await fetch(`http://127.0.0.1:${cfg.port}/add`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url, referer, cookies, token: cfg.token }),
    });
    const text = await resp.text();

    if (resp.status === 401) {
      return { ok: false, message: "Token tidak cocok — periksa opsi ekstensi" };
    }
    if (!resp.ok) {
      return { ok: false, message: text || `HTTP ${resp.status}` };
    }
    return { ok: true, message: text };
  } catch (ex) {
    return { ok: false, message: "SwiftGet tidak terjangkau — apakah aplikasinya berjalan dan endpoint-nya menyala?" };
  }
}
