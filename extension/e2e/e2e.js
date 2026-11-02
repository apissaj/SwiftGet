// E2E ekstensi SwiftGet: Edge nyata + ekstensi dimuat + situs uji lokal.
// Skenario: opsi (port+token) -> hover video (tahap 1) -> kirim -> badge
// kandidat jaringan (tahap 2) -> picker -> kirim -> verifikasi di SwiftGet
// lewat history.json dan log server.
const { chromium } = require("playwright-core");
const fs = require("fs");
const path = require("path");

const EXT = "D:\\Internet Fast Downloader\\extension";
const STATE = path.join(process.env.LOCALAPPDATA, "SwiftGet", "state");
const HISTORY = path.join(STATE, "history.json");
const LOG = path.join(process.env.TEMP, "swiftget-e2e", "requests.log");
const SHOT = (n) => path.join(process.env.TEMP, "swiftget-e2e", n);

const hasil = [];
function catat(langkah, ok, detail) {
  hasil.push({ langkah, ok, detail });
  console.log(`${ok ? "LULUS" : "GAGAL"} | ${langkah}${detail ? " | " + detail : ""}`);
}

async function tungguHistory(mengandung, timeoutMs = 20000) {
  const mulai = Date.now();
  while (Date.now() - mulai < timeoutMs) {
    try {
      const isi = fs.readFileSync(HISTORY, "utf8");
      if (isi.includes(mengandung)) return true;
    } catch (e) { /* history belum ada */ }
    await new Promise((r) => setTimeout(r, 500));
  }
  return false;
}

(async () => {
  // Ekstensi hanya termuat di persistent context (profil milik sesi ini).
  const context = await chromium.launchPersistentContext("", {
    channel: "msedge",
    headless: false,
    args: [
      `--disable-extensions-except=${EXT}`,
      `--load-extension=${EXT}`,
    ],
  });

  // MV3: service worker menyimpan ID ekstensi di URL-nya.
  let id = null;
  if (context.serviceWorkers().length) {
    id = new URL(context.serviceWorkers()[0].url()).host;
  } else {
    const sw = await context.waitForEvent("serviceworker", { timeout: 15000 });
    id = new URL(sw.url()).host;
  }
  catat("Ekstensi dimuat (service worker aktif)", !!id, `id=${id}`);

  // 1. Opsi ekstensi: port + token dari pengaturan SwiftGet e2e.
  const opt = await context.newPage();
  await opt.goto(`chrome-extension://${id}/options.html`);
  await opt.fill("#port", "49160");
  await opt.fill("#token", "e2etokene2etokene2");
  await opt.click("#simpan");
  await opt.waitForSelector("#status:text('Tersimpan')");
  catat("Opsi ekstensi: port + token tersimpan", true);

  // 2. Buka situs uji.
  const hal = await context.newPage();
  hal.on("pageerror", (err) => console.log("PAGEERROR:", err.message));
  hal.on("console", (m) => { if (m.type() === "error") console.log("CONSOLE-ERR:", m.text()); });
  await hal.goto("http://127.0.0.1:49777/");
  await hal.waitForSelector("#api-status:text-matches('API dimuat.*')");
  catat("Situs uji terbuka, API halaman dimuat", true);

  // 3. Tahap 1: hover video -> panel muncul.
  await hal.hover("#v1");
  const panel = hal.locator(".swiftget-panel.swiftget-visible");
  await panel.waitFor({ timeout: 5000 });
  const teksPanel = await panel.locator(".swiftget-label").innerText();
  catat("Panel muncul saat hover video", teksPanel === "Unduh video ini", teksPanel);
  await hal.screenshot({ path: SHOT("e2e-panel.png") });

  // 4. Klik panel -> terkirim.
  await panel.locator(".swiftget-main").click();
  await panel.locator(":text('Terkirim ke SwiftGet')").waitFor({ timeout: 8000 });
  catat("Klik panel -> terkirim ke SwiftGet", true);
  await hal.screenshot({ path: SHOT("e2e-terkirim.png") });

  // 5. Verifikasi di SwiftGet: history memuat video uji.
  const videoTercatat = await tungguHistory("video-320p.mp4");
  catat("SwiftGet mencatat video di riwayat", videoTercatat);

  // 6. Diagnostik tahap 2: inject.js terpasang? kandidat sintetis memunculkan badge?
  const inj = await hal.evaluate(() => !!window.__swiftget_inject__);
  catat("inject.js terpasang di MAIN world", inj);

  await hal.evaluate(() => window.postMessage(
    { __swiftget__: true, kind: "media", total: 1,
      urls: ["http://127.0.0.1:49777/media/diagnostik.mp4"] }, location.origin));
  await hal.waitForTimeout(600);
  const badgeSintetis = await hal.evaluate(() => {
    const b = document.querySelector(".swiftget-badge");
    return b ? b.textContent : null;
  });
  catat("Badge dari kandidat sintetis", !!badgeSintetis, badgeSintetis || "tidak muncul");
  const domDump = await hal.evaluate(() => ({
    total: document.querySelectorAll("[class*='swiftget']").length,
    daftar: [...document.querySelectorAll("[class*='swiftget']")].map((el) => el.className).slice(0, 6),
  }));
  console.log("DOM-DUMP:", JSON.stringify(domDump));

  // 7. Tahap 2: badge kandidat dari respons API (m3u8).
  const badge = hal.locator(".swiftget-badge");
  await badge.waitFor({ timeout: 10000 });
  const teksBadge = await badge.innerText();
  catat("Badge kandidat jaringan muncul", teksBadge.startsWith("⬇"), teksBadge);

  // 7. Picker in-page -> kirim m3u8.
  await badge.click();
  const picker = hal.locator(".swiftget-picker");
  await picker.waitFor({ timeout: 5000 });
  const rows = await picker.locator(".swiftget-picker-row").count();
  catat("Picker membuka daftar kandidat", rows >= 1, `${rows} baris`);
  await hal.screenshot({ path: SHOT("e2e-picker.png") });

  const adaArsip = await picker.locator(".swiftget-picker-row", { hasText: "arsip-contoh" }).count();
  catat("Kandidat tanpa ekstensi tertangkap lewat sites.json", adaArsip === 1, `${adaArsip} baris`);
  const sg = await hal.evaluate(() => ({ ...document.documentElement.dataset }));
  console.log("SG-DEBUG:", JSON.stringify(sg));

  await picker.locator(".swiftget-picker-row", { hasText: "playlist-240p" }).first().click();
  await picker.locator(".swiftget-picker-row:text-matches('^✓')").waitFor({ timeout: 8000 });
  catat("Kandidat m3u8 terkirim", true);
  await hal.screenshot({ path: SHOT("e2e-picker-terkirim.png") });

  // 8. Verifikasi: m3u8 di riwayat + server uji menerima fetch playlist+segmen.
  const m3u8Tercatat = await tungguHistory("playlist-240p.m3u8");
  catat("SwiftGet mencatat m3u8 di riwayat (unduhan HLS berjalan)", m3u8Tercatat);

  let logServer = "";
  try { logServer = fs.readFileSync(LOG, "utf8"); } catch (e) {}
  const segmenDiambil = logServer.includes("/media/seg-1.ts") && logServer.includes("/media/seg-2.ts");
  catat("Server uji menerima permintaan segmen HLS dari mesin SwiftGet", segmenDiambil);

  await context.close();
  const gagal = hasil.filter((h) => !h.ok);
  console.log(`\nRINGKASAN: ${hasil.length - gagal.length}/${hasil.length} lulus`);
  process.exit(gagal.length ? 1 : 0);
})().catch((e) => {
  console.error("E2E GAGAL TOTAL:", e.message);
  process.exit(2);
});
