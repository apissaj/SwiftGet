# Rencana riset: sadap streaming ala IDM via WinDivert

> **Status**: riset jangka panjang. Dokumen ini adalah rencana eksekusi, bukan
> fitur jadi. Target: memungkinkan unduhan video dari situs yang menyembunyikan
> URL aslinya (YouTube/SABR) dengan menyadap aliran yang diputar browser —
> pola yang dipakai IDM lewat driver jaringannya.
>
> **Prinsip etika tetap**: menyadap hanya trafik komputer pengguna sendiri,
> untuk konten yang sedang mereka putar sendiri. Tidak ada penyadapan trafik
> orang lain, tidak ada pembobolan DRM.

## 1. Latar

YouTube memakai SABR (Server-Side Adaptive Bitrate): URL player sengaja
dibuat tidak bisa diunduh ulang — terbukti empiris di proyek ini
(403, host maya, `expire` fiktif — lihat `idm-features-gaps.md`). Jalur
yt-dlp menutup kebutuhan itu hari ini; jalur ini adalah riset untuk
kemampuan independen ala IDM.

## 2. Temuan desain awal (pengubah gambar)

1. **Chrome men-streaming `googlevideo` lewat QUIC (UDP/HTTP3)** — sadapan
   TCP buta terhadapnya. Wajib: paksa fallback TCP dengan memblokir UDP/443.
2. **Aliran TCP terenkripsi TLS** — butuh MITM dengan CA lokal yang
   dipercaya sistem, khusus host media.
3. **Hasil bersifat realtime** — tersimpan secepat pemutaran; tidak bisa
   lebih cepat, tidak bisa resume di tengah yang belum diputar.

## 3. Keputusan teknologi yang menguntungkan

- **WinDivert** (LGPL; driver `.sys` **sudah bertanda tangan pabrikan**) —
  kita tidak perlu proses sertifikasi driver Microsoft sendiri; cukup hak
  administrator saat memuat. Inilah alasan jalur ini layak riset.
- **Bukan** menulis driver sendiri.

## 4. Arsitektur yang dituju

```
Chrome ──(QUIC/443)── ✁ diblok WinDivert ──▶ jatuh ke TCP/443
Chrome ──(TCP/443 TLS ke googlevideo)──▶ WinDivert alihkan ke proxy lokal
Proxy TLS lokal (CA lokal "SwiftGet Local CA")
  ├── baca SNI dari ClientHello → hanya MITM host media target
  ├── forward ke server asli
  └── plaintext stream ──▶ penulis part file SwiftGet ──▶ mesin unduhan
```

Komponen:
1. **Elevasi admin + pemuatan WinDivert** (handle handle, pola ffmpeg).
2. **Filter QUIC** — blok UDP/443 ke host target selama mode sadap.
3. **Proxy TLS terminator** — CA lokal dibuat sekali, dipasang ke trust
   store Windows; SNI di-sniff untuk memilih host yang di-MITM.
4. **Penangkap stream** — plaintext ditulis ke part file; commit lewat
   mesin SwiftGet yang sudah ada.
5. **Integrasi UI** — mode "Sadap" per unduhan: mulai sadap → pengguna
   memutar video → selesai ditandai manual/otomatis (durasi diketahui
   dari player).

## 5. Fase riset & kriteria kelulusan

| Fase | Isi | Lolos bila |
|---|---|---|
| **P0 Spike QUIC** | Blok UDP/443 via WinDivert; buka YouTube; pastikan Chrome jatuh ke TCP dan video tetap diputar | Video tetap jalan lewat TCP (catatan: cek juga HTTP/2 vs HTTP/1.1) |
| **P1 CA + MITM** | Buat CA lokal, pasang ke Windows; MITM hanya SNI `googlevideo.com` | Chrome memutar video TANPA error sertifikat |
| **P2 Tangkap** | Tulis plaintext ke berkas saat pemutaran penuh | Berkas hasil = byte identik dengan yang diputar (uji checksum) |
| **P3 Integrasi** | Sambungkan ke SwiftGet (UI, part file, commit) | Alur: play → sadap → file final di folder |
| **P4 Pemeliharaan** | Matriks risiko perubahan Chrome/Google; rencana tindak | — |

### Hasil P0 (terlaksana — LULUS, lanjut P1)

| Item | Hasil |
|---|---|
| Tanggal | 2026-11-05 |
| Metode | Blocker P/Invoke WinDivert 2.2.2 (filter `outbound and udp.DstPort == 443`, drop tanpa reinject), 45 detik, elevated |
| Bukti QUIC dipakai Chrome | **214 paket UDP/443 dihapus** selama blokir (drop #100 di detik ke-13, #200 di ke-33 — aliran kontinu) |
| Bukti fallback TCP | Sesi `TCP …:443 ESTABLISHED` tetap hidup selama blokir, termasuk ke IP Google `74.125.24.94` |
| Observasi pengguna | Video mangket sebentar lalu lanjut lagi |
| Alat | `WdBlocker.exe` (P/Invoke, log di `blocker.log`) — spike di folder kerja WinDivert |
| Keputusan | **LULUS → P1 disetujui** (CA lokal + MITM SNI googlevideo) |

**Titik berhenti yang jujur**: bila P1 gagal karena ECH/certificate-pinning
yang tidak teratasi, riset ditutup dan didokumentasikan — tanpa paksaan.

## 6. Persyaratan pengguna akhir (saat fitur jadi)

- Windows admin: sekali untuk pemasangan CA + driver.
- Menjalankan SwiftGet sebagai admin saat mode sadap aktif.
- Memutar video hingga selesai (realtime) — keunggulan: 100% merekam apa
  yang benar-benar diputar, termasuk SABR.

## 7. Risiko terbuka

- **ECH** (Encrypted Client Hello) menyembunyikan SNI → MITM tidak tahu
  host target; mitigasi: filter berbasis IP/CIDR googlevideo.
- **Certificate pinning** pada subsistem tertentu (kemungkinan rendah di
  Chrome umum, wajib diuji P1).
- Chrome/Google mengubah mekanisme (risiko permanen, sama seperti IDM).
- Performa proxy lokal pada video 4K.

## 8. Alternatif yang tetap berlaku paralel

- **yt-dlp** untuk YouTube hari ini (cepat, komunitas yang mengejar).
- Mesin SwiftGet untuk semua non-SABR (sudah berjalan).
