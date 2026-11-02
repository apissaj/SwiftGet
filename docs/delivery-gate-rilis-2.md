# Delivery Gate — Rilis 2 (Fase A–G)

Laporan penyelesaian Rilis 2. Satu fase = satu commit; setiap commit-tree
diverifikasi sendiri (build + test di worktree sementara) sebelum dianggap
selesai, bukan hanya working tree.

## Ringkasan

| Fase | Commit | Isi | Test saat commit |
|---|---|---|---|
| A | `efc0725` | Tema tersimpan + ikut Windows; FocusTrap panel periksa | 73 lulus |
| B | `e8b2533` | Reduce-motion reaktif; penyaring + urut kolom | 83 lulus |
| C | `3d0f533` | Verifikasi checksum otomatis | 110 lulus |
| D | `333d2ec` | Dynamic segmentation | 129 lulus |
| E | `40b874b` | Kategori + riwayat unduhan | 198 lulus |
| F | `871f45f` | Endpoint lokal + bookmarklet | 216 lulus |
| G | `c47ebba` | HLS/DASH + penggabungan segmen | 250 lulus |

Akhir: build 0 warning / 0 error, 250/250 test lulus, riwayat commit bersih
per fase, working tree kosong.

## Cara kerja yang dipakai

- Riset arsitektur setiap fase dikerjakan agen penjelajah (read-only) yang
  melaporkan peta kode dengan rujukan baris; implementasi menyusul dari
  laporan itu; test ditulis agen terpisah terhadap kontrak yang sudah final.
- Setiap agen penulis test dilarang menyentuh `src/`; temuan mereka terhadap
  `src/` dilaporkan dan diperbaiki terpisah dengan alasan yang dijelaskan.
- Verifikasi commit: `git worktree add` sementara pada HEAD baru, build dan
  test di sana, lalu worktree dibuang — commit harus berdiri sendiri.

## Bug yang tertangkap sebelum terjadi di tangan pengguna

Perbaikan-perbaikan ini tidak akan terlihat oleh test yang lulus saja karena
semuanya cacat penyambungan, bukan cacat logika murni:

1. **Tombol penyaring mati diam-diam (Fase B).** `EnumConverter` didaftarkan
   di `App.xaml` tetapi tidak pernah dipakai binding mana pun, dan desainnya
   sendiri tidak bisa bekerja untuk `CommandParameter` (WPF tidak memberi
   targetType enum). Enum kini diurai dari string di `SetFilterCommand`;
   converter dan kode mati `SetSortCommand` dihapus.
2. **Resume setelah korupsi berputar selamanya (Fase C).** Segmen masih
   tertanda selesai di manifest, jadi resume menyusun ulang data korup yang
   sama terus-menerus. Kini segmen dinolkan saat `Corruption`.
3. **Algoritma checksum tak dikenal menyatakan berkas korup (Fase C).**
   `calculated` kosong dibandingkan dan pasti gagal. Kini algo tak dikenal
   mengembalikan null dan verifikasi dilewati.
4. **Rentang orphan tidak pernah dikonsumsi (Fase D).** `_orphanedSegments`
   hanya diisi; janji "reassign ke worker lain" tidak pernah terjadi dan
   unduhan gagal padahal masih bisa diselamatkan. Model worker diganti
   antrean kerja dengan work-stealing.
5. **Restart endpoint lokal mematikan accept loop (Fase F).** CTS tidak
   dibuat ulang pada `Start()` kedua; siklus Stop-Start menggantung semua
   permintaan. Kini CTS dibuat ulang di `Start()`.
6. **Respons 413 tertulis dua kali (Fase F).** Penulisan kedua ditiadakan
   lewat kontrak `PostBody.Handled`.
7. **Worker segmen mati diam saat koneksi terputus (Fase G).** `IOException`
   saat membaca segmen tidak dikonversi; penulis menunggu tanpa akhir.
   Kini dikonversi ke `ConnectionFailed` (transient, dicoba ulang).
8. **Klasifikasi kategori selalu "Lainnya" (Fase E).** Kunci peta tanpa titik
   dibandingkan dengan `Path.GetExtension` yang berkarakter titik.

## Keputusan desain selama fase

- Pemantau tema Windows dipasang sekali di `MainWindow` (pemilik
  `ThemeManager`), bukan dua kali di `App` dan `MainWindow`.
- Kategori tidak menjadi penyaring status; baris penyaring tetap empat
  kondisi sesuai DESIGN.md bagian 11. Kategori tampil di panel periksa dan
  riwayat. Peninjauan keputusan ditandai di DESIGN.md bagian 16.
- Endpoint bookmarklet mati secara bawaan dan hanya mengikat `127.0.0.1`.
- Unduhan media jujur soal yang tidak diketahui: `TotalBytes` tetap null dan
  UI menunjukkan rasio segmen, bukan persen karangan.

## Celah yang masih terbuka

Lihat DESIGN.md bagian 16.

Catatan purna-delivery: celah "seleksi baris hilang saat daftar dibangun
ulang" yang dilaporkan di bawah sudah diperbaiki sesudah rilis dokumen ini —
`ApplyFilterAndSort` kini merekonsiliasi `VisibleItems` secara in-place
(Remove/Insert/Move) alih-alih Clear dan isi ulang, sehingga seleksi ListBox
dan panel periksa bertahan selama unduhan berjalan. Diverifikasi visual:
panel tetap terisi penuh dengan unduhan aktif.

## Kebersihan

- Tidak ada rahasia atau token di repositori.
- Berkas demo yang dibuat untuk tangkapan layar (server lokal ter-throttle,
  berkas 150/120 MB) sudah dihapus dari folder unduhan dan folder sementara.
- Tangkapan layar README adalah foto aplikasi yang benar-benar berjalan dengan
  unduhan sungguhan, bukan gambar rekayasa.
