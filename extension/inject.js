// Skrip dunia halaman (MAIN world): membungkus XMLHttpRequest dan fetch
// untuk memindai isi respons teks/JSON — URL media yang tak pernah muncul
// di DOM (sumber dari API situs streaming) ditangkap di sini, pola yang
// sama dengan document.js IDM. Aplikasi tidak boleh terganggu: respons
// fetch dibaca lewat clone, respons XHR hanya yang bertipe teks.
(() => {
  "use strict";

  if (window.__swiftget_inject__) return;
  window.__swiftget_inject__ = true;

  const MAX_SCAN = 1024 * 1024;      // batas isi respons yang dipindai
  const MAX_PER_RESPONSE = 10;       // batas kandidat per respons
  const MEDIA = /\.(mp4|m4v|mkv|webm|mov|avi|flv|ts|mp3|m4a|wav|flac|ogg|opus|m3u8|mpd|zip|rar|7z|tar|gz|xz|iso|pdf|docx?|xlsx?|pptx?|epub|exe|msi|msix|apk|dmg)([?#]|$)/i;
  const URL_PATTERN = /https?:\/\/[^\s"'<>()\\]+/g;

  const seen = new Set();
  let total = 0;

  // Regex khusus situs (tahap 3) dari sites.json, diteruskan content script.
  const regexSitus = [];

  // Content script memberi sinyal "ready" setelah listener terpasang;
  // sebelum itu, kandidat mengantre agar tidak ada yang hilang oleh race
  // antara document_start dan selesainya pendaftaran listener.
  let ready = false;
  const queue = [];

  window.addEventListener("message", (e) => {
    if (e.source !== window) return;
    const d = e.data;
    if (!d || d.__swiftget__ !== true) return;

    if (d.kind === "config") {
      for (const rx of d.responseRegexes || []) {
        try { regexSitus.push(new RegExp(rx, "g")); } catch (err) { /* regex situs rusak: lewati */ }
      }
      return;
    }

    if (d.kind !== "ready") return;
    ready = true;
    for (const msg of queue) window.postMessage(msg, location.origin);
    queue.length = 0;
  });

  function emit(fresh) {
    if (!fresh.length) return;
    total += fresh.length;
    const msg = { __swiftget__: true, kind: "media", total, urls: fresh };
    if (ready) window.postMessage(msg, location.origin);
    else queue.push(msg);
  }

  function scanText(body, fresh) {
    for (const match of body.matchAll(URL_PATTERN)) {
      const raw = match[0].replace(/[.,;]+$/, "");
      if (seen.has(raw)) continue;
      if (!MEDIA.test(raw)) continue;
      tambah(raw, fresh);
      if (fresh.length >= MAX_PER_RESPONSE) return;
    }

    // Regex khusus situs: kecocokan dipercaya sebagai media tanpa cek
    // ekstensi (situs bisa memakai URL tanpa akhiran yang jelas).
    for (const rx of regexSitus) {
      rx.lastIndex = 0;
      for (const m of body.matchAll(rx)) {
        tambah(m[1] || m[0], fresh);
        if (fresh.length >= MAX_PER_RESPONSE) return;
      }
    }
  }

  function tambah(raw, fresh) {
    let url = raw.replace(/[.,;]+$/, "");
    if (seen.has(url)) return;
    try { url = new URL(url, location.href).href; } catch (e) { return; }
    if (seen.has(url)) return;
    seen.add(url);
    fresh.push(url);
  }

  function extract(text) {
    if (!text || typeof text !== "string") return;
    if (text.length > MAX_SCAN) text = text.slice(0, MAX_SCAN);

    const fresh = [];
    scanText(text, fresh);

    // URL di dalam JSON sering ditulis dengan slash ter-escape (\/) dan
    // lolos dari regex biasa; pindai sekali lagi salinan yang diluruskan.
    if (text.includes("\\/")) scanText(text.replace(/\\\//g, "/"), fresh);

    emit(fresh);
  }

  // --- XHR: pasang loadend sekali per objek; hanya responseType teks yang
  // punya responseText (lainnya melempar dan ditelan).
  const origOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (...args) {
    const xhr = this;
    if (!xhr.__swiftget_hooked__) {
      xhr.__swiftget_hooked__ = true;
      xhr.addEventListener("loadend", () => {
        try {
          if (xhr.responseType && xhr.responseType !== "text") return;
          extract(xhr.responseText);
        } catch (e) { /* respons tak terbaca bukan urusan kita */ }
      });
    }
    return origOpen.apply(this, args);
  };

  // --- fetch: clone sebelum dikembalikan; pembacaan clone tidak pernah
  // mengunci body milik aplikasi.
  const origFetch = window.fetch;
  if (typeof origFetch === "function") {
    window.fetch = async function (...args) {
      const response = await origFetch.apply(this, args);
      try {
        const contentType = response.headers.get("content-type") || "";
        if (/text|json|javascript/i.test(contentType) || contentType === "") {
          response.clone().text().then(extract).catch(() => { });
        }
      } catch (e) { /* header tak terbaca: lewati */ }
      return response;
    };
  }
})();
