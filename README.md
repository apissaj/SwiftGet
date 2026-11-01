# SwiftGet

Download manager ringan untuk Windows dengan **unduhan segmented paralel** dan **resume**.

Ditulis dari nol dalam C# / .NET 8 + WPF. Satu berkas besar dipecah menjadi beberapa
bagian yang diunduh **bersamaan** lewat HTTP `Range`, lalu ditulis ke posisi
masing-masing di satu berkas sementara.

---

## Kenapa SwiftGet

| | |
|---|---|
| **Unduhan dipecah** | Berkas dibagi menjadi N bagian (default 8) yang diunduh paralel. |
| **Resume** | Berhenti kapan saja (termasuk aplikasi dimatikan paksa); melanjutkan dari posisi terakhir, bukan dari nol. |
| **Validasi konten** | `ETag` / `Last-Modified` diperiksa sebelum resume supaya data lama tidak dicampur dengan versi berkas yang sudah berubah. |
| **Fallback otomatis** | Server yang tidak mendukung `Range` tetap bisa diunduh (mode satu aliran). |
| **Ringan** | Tanpa framework UI eksternal, tanpa ORM, tanpa dependency runtime tambahan. |
| **Editor-friendly** | State disimpan sebagai JSON yang mudah dibaca dan di-debug. |

---

## Cara pakai

1. Jalankan `SwiftGet.exe`.
2. Tempel URL berkas (http/https) di kotak atas, tekan **Enter** atau klik **Unduh**.
3. Untuk mengunduh tanpa langsung memulai, klik **Tambah ke antrean**.

URL yang tampak seperti berkas unduhan juga **terdeteksi otomatis dari clipboard**
(salin URL apa pun, SwiftGet akan menawarkannya).

### Kontrol

| Aksi | Cara |
|---|---|
| Jeda satu unduhan | Tombol **Jeda** di baris |
| Lanjutkan | Tombol **Lanjut** di baris |
| Batalkan & hapus | Tombol **Batal** di baris |
| Jeda / lanjut semua | Tombol di baris aksi massal |
| Ganti tema | Tombol **Tema** di header |
| Pengaturan | Tombol **Pengaturan** di status bar |
| Sembunyikan ke tray | Tutup jendela (unduhan tetap berjalan) |

---

## Arsitektur

```
SwiftGet.Core   (engine, tanpa dependensi UI)
├── Http/
│   ├── HttpRangeClient   probe + permintaan Range, redirect manual
│   ├── HeaderParsing     Content-Range, Content-Disposition (RFC 5987), Accept-Ranges
│   └── ProbeResult       hasil pemeriksaan URL
├── Downloads/
│   ├── Segment           satu rentang [Start, End) + progres
│   ├── SegmentPlanner    pembagian statis N segmen, invarian cakupan penuh
│   ├── DownloadSession   orkestrasi: probe, worker per segmen, retry, finalisasi
│   ├── DownloadTask      kebenaran tunggal progres; snapshot aman untuk UI
│   ├── DownloadManager   antrian, konkurensi, pemulihan sesi
│   └── RetryPolicy       backoff eksponensial + jitter
├── Storage/
│   ├── PartFile          penulisan paralel ber-offset (RandomAccess)
│   ├── ManifestStore     persistensi JSON atomik (tmp + rename)
│   └── Checksum          SHA-256 / MD5 untuk verifikasi
└── Scheduling/
    └── ConnectionBudget  semaphore global + per-host

SwiftGet.App    (UI, .NET 8 WPF)
├── MainWindow            daftar unduhan, input URL, tray
├── SettingsWindow        folder, konkurensi, koneksi per berkas
├── ViewModels/           MainViewModel, DownloadsViewModel, DownloadItemViewModel
├── Infrastructure/       AppSettings, ClipboardWatcher, ThemeManager, converters
└── Themes/               Styles.xaml, SpeedGraph (digambar manual, tanpa library chart)
```

### Aliran satu unduhan

```
Probe URL
  └─ HEAD  → sudah 206? pakai
  └─ GET bytes=0-0 → 206 + Content-Range = dukungan Range terkonfirmasi
       └─ 200 = server mengabaikan Range → mode satu aliran

Rencanakan segmen
  └─ N bagian, tiap segmen [start, end), dijamin menutupi seluruh berkas

Jalankan worker
  └─ per segmen: ambil izin koneksi (global + per-host)
       └─ GET Range: bytes=<posisi>-<end-1>
            └─ tulis ke PartFile pada offset absolut segmen
                 └─ catat progres ke manifest (±1 detik)

Selesai
  └─ flush → pindahkan .swiftpart menjadi berkas akhir → hapus manifest
```

---

## Persyaratan

- Windows 10 atau lebih baru (64-bit).
- Tidak perlu .NET terpasang bila memakai build **portable**.

---

## Build

```powershell
# Kedua varian (menjalankan unit test lebih dulu)
.\build.ps1

# Hanya portable (self-contained, satu exe, tanpa .NET)
.\build.ps1 -Target portable

# Hanya framework-dependent (exe kecil, butuh .NET Desktop Runtime 8)
.\build.ps1 -Target framework-dependent
```

Hasil:

| Varian | Ukuran | Butuh runtime |
|---|---|---|
| `publish\portable\SwiftGet.exe` | ~67 MB | Tidak |
| `publish\framework-dependent\SwiftGet.exe` | ~320 KB | .NET Desktop Runtime 8 |

---

## Lokasi data

| Data | Lokasi |
|---|---|
| Berkas hasil unduhan | `%USERPROFILE%\Downloads\SwiftGet` (bisa diubah) |
| Pengaturan | `%LOCALAPPDATA%\SwiftGet\settings.json` |
| State unduhan belum selesai | `%LOCALAPPDATA%\SwiftGet\state\*.swiftget` + `*.swiftpart` |

Manifest `*.swiftget` berisi URL, validator, daftar segmen, dan progres tiap segmen.
Menghapusnya sama dengan membuang kemampuan resume untuk unduhan tersebut.

---

## Pengujian

```powershell
dotnet test
```

44 test mencakup:

- Perencanaan segmen: cakupan penuh, tanpa celah, tanpa tumpang tindih, batas koneksi.
- Parsing header: `Content-Range`, `Content-Disposition` (termasuk RFC 5987), `Accept-Ranges`.
- Unduhan paralel: hasil byte-per-byte identik dengan sumber.
- Penulisan ber-offset benar (setiap segmen pada posisinya).
- Server tanpa `Range`: fallback ke satu aliran.
- Resume: melanjutkan dari tengah segmen, bukan mengunduh ulang.
- Perubahan konten di server: resume ditolak, unduhan dinyatakan gagal.
- Nama berkas dari `Content-Disposition`.
- Manifest atomik dan bisa dibaca ulang.

---

## Aturan agen & arah desain

Proyek ini memakai [antislop](https://antislop.ai.id/), sistem aturan untuk agen
AI yang menyaring UI, teks, dan komentar hasil generik. Berkas aturan ada di
`.antislop/` (inti + skill UI, aksesibilitas, copywriting, dan higiene kode).

Arah desain tinggal di `DESIGN.md`. Aturan antislop menghapus yang generik;
`DESIGN.md` yang menentukan mau dibawa ke mana.

Memperbarui berkas aturan:

```powershell
.\scripts\update-antislop.ps1
```

## Catatan desain

**Penulisan file paralel.** Semua segmen menulis ke satu `FileStream` yang sama,
tetapi memakai `RandomAccess.WriteAsync` dengan offset eksplisit. Memakai
`Stream.Position` bersama dari beberapa worker adalah bug nyata yang pernah
terjadi di proyek ini: cursor bersama membuat blok data saling menimpa
(32.672 byte rusak pada uji pertama). Offset eksplisit menghilangkan masalah itu
sepenuhnya.

**Mengapa `Range` dikonfirmasi dengan permintaan sungguhan.** Banyak server
mengirim `Accept-Ranges: bytes` tetapi tetap menjawab `200 OK` (isi penuh) saat
diminta `Range`. Menyimpulkan dukungan hanya dari header akan menghasilkan
unduhan yang rusak. Karena itu dukungan `Range` hanya diakui bila server
membalas `206 Partial Content` dengan `Content-Range` yang cocok.

**Resume yang aman.** Resume hanya dilanjutkan bila `ETag`/`Last-Modified` dan
panjang berkas masih cocok dengan yang tercatat. Jika berubah, unduhan dimulai
ulang — lebih baik lambat sedikit daripada menyimpan berkas yang tidak konsisten.

---

## Status & rencana

**Selesai (Rilis 1 / MVP)**

- [x] Unduhan segmented paralel
- [x] Resume setelah jeda, gagal, atau aplikasi mati
- [x] Validasi validator sebelum resume
- [x] Fallback satu aliran untuk server tanpa Range
- [x] Antrian dengan batas unduhan bersamaan & batas per-host
- [x] UI: daftar, progres, kecepatan, sisa waktu, grafik kecepatan
- [x] Deteksi URL dari clipboard
- [x] Tray, single-instance, pengaturan, tema terang/gelap

**Rencana (Rilis 2)**

- [ ] Dynamic segmentation (pecah/gabung segmen saat runtime)
- [ ] Deteksi URL lengkap dengan referer & cookie (browser)
- [ ] Endpoint lokal `127.0.0.1` + bookmarklet/ekstensi
- [ ] HLS/DASH (`.m3u8` / `.mpd`) dengan penggabungan segmen
- [ ] Verifikasi checksum otomatis bila server menyediakannya
- [ ] Kategori/riwayat unduhan

---

## Riwayat analisis

Teknik segmentasi dirancang dari nol berbasis RFC 7233, dengan studi perilaku
pembanding terhadap Internet Download Manager (IDM 6.43.15). Catatan analisis
tersebut ada di [`docs/idm-behavior-notes.md`](docs/idm-behavior-notes.md).
Tidak ada kode, string protokol privat, atau aset IDM yang disalin.
