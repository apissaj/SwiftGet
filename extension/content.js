// Panel melayang ala IDM: mengikuti elemen video/gambar/audio yang diarahkan
// kursor, mengirim URL ke SwiftGet lewat service worker (yang menambahkan
// cookie dan token). Blob MSE (YouTube dkk.) diakui jujur sebagai tidak bisa
// diunduh langsung.
(() => {
  "use strict";

  if (window.__swiftget_panel__) return;
  window.__swiftget_panel__ = true;

  // Listener pesan dari dunia halaman dipasang PALING AWAL (document_start)
  // supaya tidak ada kandidat yang hilang; segala hal yang butuh DOM
  // ditunda sampai dokumen siap.
  const candidates = new Map(); // url -> "baru" | "terkirim"
  let abaikanGambar = false;    // per situs (sites.json), mis. YouTube
  let izinkanBlob = true;       // per situs; YouTube menahan panel blob

  let terbaik = null;           // kandidat terpilih otomatis (URL)
  let domSiap = false;

  // Endpoint SwiftGet dikecualikan berdasarkan PORT yang dikonfigurasi,
  // bukan host saja — situs di localhost dengan port lain tetap sah.
  let endpointPort = null;
  chrome.runtime.sendMessage({ type: "swiftget-get-config" })
    .then((cfg) => { endpointPort = (cfg && cfg.port) || 49160; })
    .catch(() => { endpointPort = 49160; });

  function isOurEndpoint(url) {
    if (endpointPort === null) return true; // konfigurasi belum tiba: tahan dulu
    try {
      const u = new URL(url);
      return (u.hostname === "127.0.0.1" || u.hostname === "localhost") &&
        Number(u.port || 80) === endpointPort;
    } catch (ex) {
      return true;
    }
  }

  // Pilih kandidat terbaik ala IDM: itag 18 (MP4 progresif bersuara) lebih
  // dulu; kalau tidak ada, video segar dengan ukuran terbesar (clen).
  function hitungTerbaik() {
    terbaik = null;
    let skorTerbaik = -1;
    for (const url of candidates.keys()) {
      const info = infoKandidat(url);
      if (!info || info.kedaluwarsa) continue;
      let skor;
      if (info.itag === "18") skor = 100;
      else if (info.jenis === "video") skor = 50;
      else if (info.jenis === "audio") skor = 10;
      else continue;

      let clen = 0;
      try { clen = Number(new URL(url).searchParams.get("clen")) || 0; } catch (e) { }
      skor += clen / 1e9; // pengikat untuk membedakan kualitas

      if (skor > skorTerbaik) { skorTerbaik = skor; terbaik = url; }
    }
  }

  // Kandidat googlevideo membawa metadata di query-nya: itag (varian),
  // mime (video/audio), expire (unix detik). Dipakai untuk label dan
  // penyaring kedaluwarsa kandidat — URL kedaluwarsa pasti 403.
  function infoKandidat(url) {
    try {
      const u = new URL(url);
      const itag = u.searchParams.get("itag");
      const mime = u.searchParams.get("mime");
      const expire = Number(u.searchParams.get("expire"));
      const jenis = mime
        ? (mime.startsWith("video") ? "video" : mime.startsWith("audio") ? "audio" : null)
        : null;
      return {
        itag,
        jenis,
        kedaluwarsa: expire > 0 && expire * 1000 <= Date.now(),
        waktu: expire > 0 ? new Date(expire * 1000).toLocaleString() : null,
      };
    } catch (ex) {
      return null;
    }
  }

  window.addEventListener("message", (e) => {
    // Sumber harus window yang sama (dunia halaman) dan penanda khusus;
    // halaman lain tidak bisa memalsukan pesan lintas-origin ini.
    if (e.source !== window || !e.data || e.data.__swiftget__ !== true) return;
    if (e.data.kind !== "media" || !Array.isArray(e.data.urls)) return;

    (async () => {
      let added = 0;
      for (const url of e.data.urls) {
        if (isOurEndpoint(url) || candidates.has(url)) continue;
        candidates.set(url, "baru");
        added++;
      }
      if (added) hitungTerbaik();
    })();
  });

  // Sinyal ke inject.js: listener sudah terpasang, antriannya boleh dilepas.
  window.postMessage({ __swiftget__: true, kind: "ready" }, location.origin);

  // Konfigurasi per situs (tahap 3): dibaca langsung dari berkas ekstensi —
  // tanpa transit service worker supaya tidak kalah cepat oleh fetch halaman.
  fetch(chrome.runtime.getURL("sites.json"))
    .then((r) => r.json())
    .then((db) => {
      const host = location.hostname;
      const situs = (db.sites || []).find((s) =>
        (s.hosts || []).some((h) => host === h || host.endsWith("." + h)));
      abaikanGambar = !!(situs && situs.ignoreImages);
      izinkanBlob = !!(situs && (situs.blobPanel !== false));
      window.postMessage({
        __swiftget__: true, kind: "config",
        responseRegexes: (situs && situs.responseRegexes) || [],
      }, location.origin);
    })
    .catch(() => { });

  // Sisanya butuh DOM: panel melayang.
  function initDom() {
    if (domSiap) return;
    domSiap = true;

    const panel = document.createElement("div");
    panel.className = "swiftget-panel";
    panel.innerHTML =
      '<span class="swiftget-main">' +
      '<span class="swiftget-arrow"></span>' +
      '<span class="swiftget-label"></span>' +
      "</span>" +
      '<span class="swiftget-btn swiftget-help" title="Pengaturan ekstensi">?</span>' +
      '<span class="swiftget-btn swiftget-close" title="Sembunyikan">×</span>';
    document.documentElement.appendChild(panel);


  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initDom);
  } else {
    initDom();
  }
})();
