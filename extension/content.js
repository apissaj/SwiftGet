// Panel melayang ala IDM: mengikuti elemen video/gambar/audio yang diarahkan
// kursor, mengirim URL ke SwiftGet lewat service worker (yang menambahkan
// cookie dan token). Blob MSE (YouTube dkk.) diakui jujur sebagai tidak bisa
// diunduh langsung.
(() => {
  "use strict";

  if (window.__swiftget_panel__) return;
  window.__swiftget_panel__ = true;

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

  const label = panel.querySelector(".swiftget-label");
  const main = panel.querySelector(".swiftget-main");
  let current = null;          // elemen media yang ditunjuk
  let hideTimer = null;
  const dismissed = new WeakSet();

  function labelFor(el) {
    if (el.tagName === "VIDEO") return "Unduh video ini";
    if (el.tagName === "AUDIO") return "Unduh audio ini";
    return "Unduh gambar ini";
  }

  function resolveUrl(el) {
    if (el.tagName === "IMG") return el.currentSrc || el.src || null;
    return el.currentSrc || el.src ||
      (el.querySelector("source") && el.querySelector("source").src) || null;
  }

  function show(el) {
    if (dismissed.has(el)) return;
    current = el;

    const url = resolveUrl(el);
    if (!url || url.startsWith("data:") || url.startsWith("blob:")) {
      // Blob = Media Source Extensions: sumber asli tidak pernah diberikan
      // ke browser dalam bentuk URL yang bisa diunduh ulang.
      label.textContent = url && url.startsWith("blob:")
        ? "Stream MSE (blob:) tidak bisa diunduh langsung"
        : "Sumber media tidak dikenal";
      panel.classList.add("swiftget-status", "swiftget-error");
      panel.classList.remove("swiftget-visible");
      return;
    }

    label.textContent = labelFor(el);
    panel.classList.remove("swiftget-status", "swiftget-error");
    panel.classList.add("swiftget-visible");

    const rect = el.getBoundingClientRect();
    const w = panel.offsetWidth || 170;
    const h = panel.offsetHeight || 26;
    const left = Math.max(8, Math.min(rect.left, window.innerWidth - w - 8));
    const top = Math.max(8, Math.min(rect.top, window.innerHeight - h - 8));
    panel.style.left = `${left}px`;
    panel.style.top = `${top}px`;
  }

  function hide() {
    panel.classList.remove("swiftget-visible");
    current = null;
  }

  function flash(message, isError) {
    label.textContent = message;
    panel.classList.toggle("swiftget-error", !!isError);
    panel.classList.add("swiftget-status", "swiftget-visible");
    hideTimer && clearTimeout(hideTimer);
    hideTimer = setTimeout(hide, 2500);
  }

  async function sendCurrent() {
    if (!current) return;
    const url = resolveUrl(current);
    const referer = location.href;
    main.style.opacity = ".6";
    label.textContent = "Mengirim…";

    let reply;
    try {
      reply = await chrome.runtime.sendMessage({ type: "swiftget-add", url, referer });
    } catch (ex) {
      reply = { ok: false, message: "Service worker tidak merespons" };
    }

    main.style.opacity = "";
    flash(reply && reply.ok ? "Terkirim ke SwiftGet" : (reply && reply.message) || "Gagal mengirim", !(reply && reply.ok));
    if (reply && reply.ok) dismissed.add(current);
  }

  // Tampilkan panel saat kursor menyentuh elemen media yang cukup besar.
  document.addEventListener("mouseover", (e) => {
    const el = e.target && e.target.closest && e.target.closest("video,img,audio");
    if (!el) return;
    if (el.tagName === "IMG") {
      const rect = el.getBoundingClientRect();
      // Ikon dan piksel pelacak tidak layak panel.
      if (rect.width < 64 || rect.height < 48) return;
    }
    show(el);
  }, true);

  panel.addEventListener("mouseenter", () => {
    hideTimer && clearTimeout(hideTimer);
  });
  panel.addEventListener("mouseleave", hide);

  // Klik di luar panel dan elemen media menutup panel.
  document.addEventListener("click", (e) => {
    if (panel.contains(e.target)) return;
    if (current && (current === e.target || current.contains(e.target))) return;
    if (panel.classList.contains("swiftget-visible") &&
        !panel.classList.contains("swiftget-status")) hide();
  });

  main.addEventListener("click", sendCurrent);
  panel.querySelector(".swiftget-close").addEventListener("click", (e) => {
    e.stopPropagation();
    if (current) dismissed.add(current);
    hide();
  });
  panel.querySelector(".swiftget-help").addEventListener("click", (e) => {
    e.stopPropagation();
    chrome.runtime.sendMessage({ type: "swiftget-open-options" });
    hide();
  });

  // ============================================================
  // Kandidat dari jaringan (tahap 2): inject.js di dunia halaman
  // memindai respons XHR/fetch; kandidat dikumpulkan di sini dan
  // diumumkan lewat badge pojok kanan bawah + picker in-page.
  // ============================================================

  const candidates = new Map(); // url -> "baru" | "terkirim"
  let badge, picker;

  function isOurEndpoint(url) {
    return /^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?\//.test(url);
  }

  window.addEventListener("message", (e) => {
    // Sumber harus window yang sama (dunia halaman) dan penanda khusus;
    // halaman lain tidak bisa memalsukan pesan lintas-origin ini.
    if (e.source !== window || !e.data || e.data.__swiftget__ !== true) return;
    if (e.data.kind !== "media" || !Array.isArray(e.data.urls)) return;

    let added = 0;
    for (const url of e.data.urls) {
      if (isOurEndpoint(url) || candidates.has(url)) continue;
      candidates.set(url, "baru");
      added++;
    }
    if (added) updateBadge();
  });

  function updateBadge() {
    const baru = [...candidates.values()].filter((s) => s === "baru").length;
    if (!candidates.size) {
      badge?.remove();
      badge = null;
      return;
    }

    if (!badge) {
      badge = document.createElement("div");
      badge.className = "swiftget-badge";
      badge.title = "Kandidat unduhan dari SwiftGet";
      badge.textContent = "⬇";
      badge.addEventListener("click", openPicker);
      document.documentElement.appendChild(badge);
    }
    badge.textContent = baru > 0 ? `⬇ ${candidates.size} (${baru} baru)` : `⬇ ${candidates.size}`;
  }

  function openPicker() {
    if (picker) closePicker();

    picker = document.createElement("div");
    picker.className = "swiftget-overlay";
    const box = document.createElement("div");
    box.className = "swiftget-picker";
    const head = document.createElement("div");
    head.className = "swiftget-picker-head";
    head.textContent = `Kirim ke SwiftGet — ${candidates.size} kandidat`;
    box.appendChild(head);

    for (const [url, status] of candidates) {
      const row = document.createElement("a");
      row.className = "swiftget-picker-row" + (status === "terkirim" ? " swiftget-sent" : "");
      row.textContent = status === "terkirim" ? `✓ ${url}` : url;
      row.href = "#";
      row.addEventListener("click", async (ev) => {
        ev.preventDefault();
        row.textContent = "Mengirim…";
        const reply = await chrome.runtime.sendMessage({ type: "swiftget-add", url, referer: location.href });
        if (reply && reply.ok) {
          candidates.set(url, "terkirim");
          row.classList.add("swiftget-sent");
          row.textContent = `✓ ${url}`;
          updateBadge();
        } else {
          row.textContent = (reply && reply.message) || "Gagal mengirim";
        }
      });
      box.appendChild(row);
    }

    const close = document.createElement("a");
    close.className = "swiftget-picker-close";
    close.textContent = "Tutup";
    close.href = "#";
    close.addEventListener("click", (ev) => {
      ev.preventDefault();
      closePicker();
    });
    box.appendChild(close);

    picker.appendChild(box);
    document.documentElement.appendChild(picker);
  }

  function closePicker() {
    picker?.remove();
    picker = null;
  }
})();
