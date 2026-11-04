# REA lanjutan: binari pendamping IDM & analisis kesenjangan fitur

Lampiran untuk `idm-behavior-notes.md` (mesin) dan `idm-extension-notes.md`
(browser). Pass ini membedah **binari pendamping** dan **permukaan
integrasi** yang belum disentuh, lalu memetakan fitur IDM vs SwiftGet.

## 1. Metode dan bukti

- Ekspor tabel PE via REA CLI `inspect-native-exports` (tanpa eksekusi,
  tanpa engine; bukti `evidence_id` dicantumkan per berkas).
- String statis via Python (suplemen, ditandai sebagai metode pendukung).
- Registry pengaturan: enumerasi kunci, **tanpa** membaca nilai sensitif
  (Passwords tidak dibaca isinya — hanya keberadaannya dicatat).

## 2. Peran tiap binari pendamping (dari tabel ekspor)

| Berkas | SHA-256 (awal) | Ekspor bermakna | Peran |
|---|---|---|---|
| `IDMNetMon.dll` | `006d5bee…` | `ControlMonitoring` (+`ev_ef7871cc…`) | Pemantau jaringan tingkat sistem: mendeteksi unduhan di **aplikasi mana pun**, bukan hanya browser. |
| `IDMShellExt64.dll` | — | `ControlMonitoring`, COM (`DllGetClassObject`) | Shell extension; juga membawa saklar pemantauan yang sama. |
| `IDMVMPrs64.dll` | `6ff10a30…` | **645 ekspor**: seluruh **libxml2/html** + `Parse1`, `GetMPDParserInfo` (`ev_17675a31…`) | Parser media: MPD/DASH dan halaman HTML diurai dengan **libxml2 yang dibundel**. Konfirmasi independen bahwa parsing manifest streaming adalah inti deteksi video. |
| `IDMGetAll64.dll` | — | COM 4 ekspor standar (`ev_9be078aa…`) | "Download all links with IDM" (lihat juga `IEGetAll.htm`). |
| `downlWithIDM64.dll` | — | COM 4 ekspor standar (`ev_f2f2f750…`) | Menu konteks "Download with IDM". |
| `IDMFType64.dll` | — | `IDMFileType`, `IDMExtensionForMimeType` (`ev_84160e01…`) | Database jenis berkas yang **bisa dikueri**: jenis → ekstensi per MIME. |
| `idmBroker.exe`, `IDMIntegrator64.exe`, `IEMonitor.exe` | — | (tidak dieksplor — pendukung proses) | Integrasi antar-proses. |

## 3. Permukaan integrasi resmi

- **COM type library** `idmantypeinfo.tlb`: `IDManLib` dengan dua metode —
  `SendLinkToIDMWWW` (satu tautan) dan `SendLinksArrayWW` (array tautan).
  Minimalis dan elegan: cukup itu untuk integrasi dari aplikasi mana pun.
- **Command line** `IDMan.exe`: switch `/d /s /a /p /f /n /q /h` dkk.
  terkonfirmasi di string biner (metode pendukung: pindai string).
- **Registry** `HKCU\Software\DownloadManager`: sub-kunci `FoldersTree`
  (kategori → folder), `Passwords` (HTTP auth — **isi tidak dibaca**),
  `ProxyServers`, `Queue`, `Scheduler`, `ListSettings`, `menuExt`.
  Nama-nama ini memetakan fitur di bawah.

## 4. Temuan fitur non-mesin

- **Defexclusion list** (`defexclist.txt`, teks terbuka): pola wildcard URL
  yang **tidak boleh ditangkap** — suara widget, ikon toolbar, piksel
  pelacak. Fitur anti-kebisingan, bukan sekadar daftar.
- **`IDMFType.dat`**: 185 KB, entropi tinggi (terenkripsi/terkompresi
  sengaja). Tidak diurai — isi yang relevan (jenis → ekstensi) sudah
  terwakili oleh ekspor `IDMFType64.dll`.
- **Site Grabber**: `IDMGrHlp.exe` + `grabber.chm` + string
  `"IDM Site Grabber. Step %d --- %s"` di IDMan.exe — perayap situs
  bertahap dengan antrean dan filter.
- **Speed Limiter**: string `bUseSpeedLimiter`, `Speed Limiter` — batas
  kecepatan per unduhan yang bisa dinyalakan pengguna.
- **Scheduler**: sub-kunci `Scheduler` + tautan dukungan ws_for_scheduler —
  mulai/berhenti otomatis berdasarkan waktu.
- **HTTP auth**: string `useAuthorization=%d` + sub-kunci `Passwords` —
  Basic/autentikasi halaman untuk server berpassword.
- **Kategori → folder otomatis**: `FoldersTree` — kategori tidak hanya
  label; berkas selesai **dipindahkan** ke foldernya.

## 5. Analisis kesenjangan: IDM vs SwiftGet

| # | Fitur IDM | SwiftGet | Rekomendasi |
|---|---|---|---|
| 1 | **Pindah otomatis ke folder per kategori** | Kategori hanya label + penyaring; semua berkas ke satu folder | **Ambil** — kecil, kualitas harian langsung terasa |
| 2 | **Exclusion list** anti-kebisingan | Filter minimal di ekstensi/clipboard | **Ambil** — kecil; berkas teks wildcard + pemuat |
| 3 | **Speed limiter** | Tidak ada | **Ambil** — sedang; throttle di penulis segmen, saklar UI |
| 4 | **HTTP auth (Basic)** + daftar kredensial | Tidak ada | **Ambil** — sedang; diperlukan untuk server berpassword |
| 5 | **Download all links** (ekstensi) | Bookmarklet punya picker sederhana | **Ambil** — sedang; tambah aksi "kirim semua tautan" di picker |
| 6 | **Scheduler** (mulai/berhenti waktu) | Tidak ada | Bisa nanti — sedang; nilai nyata hanya untuk pengguna berat |
| 7 | **Network monitor** (unduhan di aplikasi mana pun) | Tidak ada — hanya clipboard + ekstensi | **Tinggalkan** — butuh driver/dll injeksi + hak admin; ekstensi menutup 95% kasus |
| 8 | **Site Grabber** (cerai seluruh situs) | Tidak ada | **Tinggalkan** dulu — produk seluruh baru, kompleksitas tinggi |
| 9 | **Parser MPD via libxml2** | Parser XML sendiri (Fase G) | Cukup — tak perlu bundel libxml2 |
| 10 | **DB jenis berkas terenkripsi** | Taksonomi terbuka di `FileCategories` | Cukup — versi terbuka lebih mudah diperluas |
| 11 | **COM/CLI integrasi** | Endpoint HTTP lokal + ekstensi | Cukup — HTTP lebih mudah dari luar; CLI kecil opsional |
| 12 | **FTP / MMS / RTMP** | Tidak ada | Tetap ditinggalkan (keputusan lama) |

## 6. Rekomendasi urutan (Rilis 2.x)

1. **#1 + #2** (satu commit): kategori → folder otomatis + exclusion list.
2. **#3**: speed limiter.
3. **#4**: HTTP Basic auth (URL `http://user:pass@…` dan/atau form di UI).
4. **#5**: "kirim semua tautan" di ekstensi.
5. #6 menunggu permintaan nyata.
