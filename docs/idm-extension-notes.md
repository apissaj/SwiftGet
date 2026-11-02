# Catatan REA: Integrasi browser IDM (ekstensi Chrome/Edge)

Hasil reverse engineering integrasi browser IDM sebagai referensi desain
ekstensi SwiftGet. Analisis sebelumnya (`idm-behavior-notes.md`) membahas
mesin unduhannya; dokumen ini khusus sisi browser.

## Target dan bukti

| Artefak | Lokasi | Identitas |
|---|---|---|
| Ekstensi Chrome | `C:\Program Files (x86)\Internet Download Manager\IDMGCExt59.crx` (CRX2, v6.40.8.2) | diekstrak ke folder kerja sementara |
| Ekstensi Edge | `IDMEdgeExt.crx` (satu paket yang sama, ID berbeda) | — |
| Native host | `IDMMsgHost.exe`, manifest `IDMMsgHost.json` | `com.tonec.idm`, stdio |

Bukti REA (analisis CLI `analyze-javascript-application`, cakupan parsial —
analisis per berkas kehabisan batas 300 detik karena kode ter-minify):
inventory `jsri:cb286d7383c7aa2162ea89e3422d01fdbf924188475b5bb49a43a905711cdf72`,
container SHA-256 `6f19ad67ee7b2cac6806017c549dbda203e499a8f9681b589edd9d5a7a106fed`,
`background.js` `b278e786…`, `content.js` `0f5e5868…`, `document.js`
`f6ac16ef…`. Grafik aliran data REA tercatat `partial` dengan daftar unknown
eksplisit; fakta protokol di bawah dibaca langsung dari sumber ter-minify
(pencarian pola + pembacaan fungsi), bukan dari grafik.

## Arsitektur: tiga kanal transport, dirotasi

Ekstensi memilih kanal dengan rotasi `attempt % (2 host + 1)`:

1. **WebSocket** ke `ws://127.0.0.1:1001/?cid=<acak>` dengan subprotokol
   `plugin.v3.internetdownloadmanager.com`, timeout sambung 1,5 detik.
2. **WebSocket** cadangan ke `ws://0.1.0.1:1001/…` (alamat ganjil yang sama
   portnya; kemungkinan trik anti-mudah-diakses-aplikasi-lain).
3. **Native messaging** `chrome.runtime.connectNative("com.tonec.idm")` —
   `IDMMsgHost.exe` stdio, allowlist dua ID ekstensi (Chrome
   `ngpampappnmepgilojfohadhhmbhlaek`, Edge `llbjbkhnmlidjebalopleeepgdfgcpec`).

Ada kanal keempat yang sinkron: XHR POST ke `<basis>?seq=<n>` (dipakai saat
ekstensi perlu jawaban segera). Basis server lokal IDM: `http://127.0.0.1:1001`
— dan probe ketersediaan `GET http://<host-unduh>/status/204+IDM` dengan
timeout 1 detik (`204+IDM` di akhir path adalah penanda IDM, bukan URL acak).

## Format kabel

Teks berpanjang-depan: `MSG#<seq>#<tipe>#<flags>#<codepage>[:param…][,<kunci>=<panjang>:<nilai>…]`
(nilai array/Blob dihitung panjangnya, kunci berupa angka). Jenis pesan yang
terlihat: 1, 5, 8, 10, 13, 14, 15, 16, 19, 21, 23. Handshake setelah sambung
mengirim penanda versi `[87,51,…]` ("W3"), codepage locale (1028/1031), dan
dict berkunci angka — yang teridentifikasi: `114` = nama kelas jendela browser
(`Chrome_RenderWidgetHostHWND` — dipakai IDM untuk menargetkan jendela),
`116` = bahasa UI, `125` = konfigurasi JSON.

## Pembagian peran yang tidak disangka

IDM **mendeteksi unduhan sendiri** lewat hook jaringannya; ekstensi bukan
detektor utama melainkan pemasok konteks:

- **Cookie atas permintaan**: `browser.cookies.getAll({url})` per URL yang
  diminta IDM (dict `122`), bahasa UI (`121`), flag silent/foreground (`2`/`4`).
- **Fetch atas nama IDM** (tipe 13): IDM meminta ekstensi melakukan XHR dari
  sesi halaman (konteks auth), dengan penulisan-ulang header terlarang —
  `Referer/Cookie/User-Agent/…` diawali `X-IDM-` sebelum `setRequestHeader`.
- Ambang deteksi ekstensi: `webRequest.onBeforeRequest/` + `onHeadersReceived`
  untuk melihat unduhan, daftar tipe resource `main_frame|sub_frame|object|
  image|xmlhttprequest`, dan ekstensi berkas khusus `MPD|M3U8|F4M`.

## Sisi halaman: penangkapan media dari API situs

`document.js` disuntik ke **dunia halaman** (bukan isolated world) dan:

- Membungkus `XMLHttpRequest.prototype.open` dan `window.fetch` bila
  dikonfigurasi: menandai permintaan dengan `Accept: x-idm-request-id/<id>`,
  membaca ulang `responseText`, lalu mencocokkannya dengan **regex dari IDM**
  untuk menemukan URL media yang tersembunyi di respons API situs streaming.
- Menjawab permintaan ekstraksi mendalam: elemen bertanda `__idm_id__` dibaca
  propertinya lewat path `a.b.c` dengan dukungan `*` (wildcard semua properti)
  — cara IDM mengambil `src` asli dari objek player yang tidak terlihat di DOM.
- `content.js` memantau elemen `video/audio/object/embed`, kejadian mouse dan
  scroll untuk tombol unduh melayang; **selector CSS per situs dikirim oleh
  IDM** (dict konfigurasi; bawaan `:root:empty` = mati) — database situs yang
  diperbarui dari server, bukan logika keras di ekstensi.

## Izin yang dipakai

`<all_urls>`, `tabs`, `cookies`, `contextMenus`, `webNavigation`,
`webRequest`, `webRequestBlocking`, `downloads`, `management`, `storage`,
`proxy`, `nativeMessaging` (manifest v2, background persisten — bentuk lama).

## Implikasi desain ekstensi SwiftGet

1. **Transport: HTTP lokal yang sudah ada.** SwiftGet sudah punya endpoint
   `127.0.0.1` — ekstensi cukup POST ke sana; tidak perlu native host untuk
   MVP. Tambahkan **token sesi acak** (IDM memakai `?auth=` pada halaman
   `captured.html`-nya) supaya halaman web lain tidak bisa POST ke endpoint.
2. **MV3, bukan MV2.** IDM masih MV2 persisten (bentuk lama). Ekstensi baru
   wajib service worker + `chrome.storage.session`.
3. **Cookie = nilai utama ekstensi.** `chrome.cookies.getAll({url})` dengan
   `host_permissions` — lalu SwiftGet mengirim header `Cookie` sendiri
   (HttpRangeClient perlu dukungan header Cookie, referer sudah ada).
4. **Referer dari tab/frame**, bukan `document.referrer` saja.
5. **Hook XHR/fetch dunia halaman** (pola document.js) adalah cara menangkap
   URL media di situs streaming — tahap kedua setelah MVP pemindai DOM.
6. **Database selector per situs** (pola IDM) = tahap ketiga; jangan keras
   di ekstensi.

## Yang tidak diketahui (batas analisis)

- Detail serializer biner native (`tb`/`ub` untuk pesan non-teks) dan makna
  lengkap tiap tipe pesan — hanya sebagian yang terbaca.
- Komunikasi `IDMMsgHost.exe` ⇄ IDM (proses native, di luar lingkup pass ini).
- Sumber isi database selector/regex per situs (dikirim IDM, tidak di-replay).
- Kapan tepatnya IDM memakai kanal WS vs native (rotasi terlihat, prioritas
  runtime tidak diobservasi — semua fakta di atas statis).
