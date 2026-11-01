# SwiftGet  -  DESIGN.md

Arah desain untuk antarmuka SwiftGet. Dokumen ini mengikat; aturan `antislop`
menghapus *slop*, dokumen ini yang memberi arah.

Dial: **ENERGY 2 / RHYTHM 2 / MOTION 1**

---

## 1. Design Read

> Dibaca sebagai: aplikasi desktop utilitas untuk satu pengguna yang mengunduh
> berkas berukuran besar, dengan bahasa visual *instrument panel* yang tenang dan
> padat informasi, dial ENERGY 2 / RHYTHM 2 / MOTION 1.

**Mengapa dial ini, bukan yang lain.**

- **ENERGY 2, bukan 3.** Jendela ini dilihat sambil mengerjakan hal lain. Ia harus
  terbaca dalam satu lirikan lalu dilupakan. Energi tinggi (gradien, warna ramai,
  ilustrasi) bersaing dengan pekerjaan sebenarnya.
- **ENERGY 2, bukan 1.** Nol energi menghasilkan "Sterile Default": kotak abu-abu
  tanpa identitas. Satu aksen biru dipakai tepat di satu tempat, dan grafik
  kecepatan memberi hidup pada layar.
- **RHYTHM 2, bukan 1.** Tiga pita atas (header, input, aksi massal) punya
  kepadatan berbeda dari area daftar. Satu jeda nyata, sisanya konsisten.
- **RHYTHM 2, bukan 3.** Ini jendela yang dipakai berulang kali, bukan halaman
  naratif. Komposisi asimetris yang berubah tiap kali dibuka justru memperlambat.
- **MOTION 1.** Selama unduhan berjalan, layar ini terus berubah sendiri:
  progres, kecepatan, grafik. Animasi tambahan di atas data yang sudah bergerak
  adalah kebisingan.

**Pengguna dan konteks.** Satu orang di komputernya sendiri, sering membiarkan
jendela ini terbuka di samping aplikasi lain. Yang dijawab layar ini, berurutan:
*apakah unduhan saya selesai, seberapa cepat, dan berapa lama lagi.* Sisanya
adalah aksesori.

---

## 2. Prinsip

**1. Data yang bergerak adalah satu-satunya animasi yang dibutuhkan.**
Angka kecepatan, progres, dan grafik berubah puluhan kali per detik. Menambah
fade, slide, atau pulse hanya menambah gerakan tanpa menambah informasi.

**2. Satu tempat, satu aksen.**
Aksen biru hanya muncul di tombol aksi utama dan garis progres. Status memakai
warna semantiknya sendiri. Bila aksen muncul di semua tempat, ia berhenti menjadi
aksen.

**3. Lapisan berbeda terlihat berbeda.**
Tiga kedalaman: kanvas (latar aplikasi), permukaan (kartu/daftar), permukaan
alternatif (hover, terpilih, isian). Pembedanya warna, bukan bayangan.

**4. Kolom dipilih dari pertanyaan pengguna, bukan dari komponen tabel.**
Pertanyaan berurutan: apa ini (Berkas), sudah sampai mana (Status), seberapa
besar (Ukuran), seberapa cepat (Kecepatan), lalu aksi. Kolom yang tidak menjawab
salah satunya dikeluarkan.

**5. Kosong itu jujur.**
Tanpa unduhan, tampilkan alasan kosong dan satu tindakan yang mengisinya. Tidak
ada angka contoh, tidak ada riwayat palsu, tidak ada metrik karangan.

---

## 3. Warna

Palet aktif dibatasi: **satu netral sebagai basis, satu tinta, satu aksen**, plus
tiga warna status yang hanya muncul saat statusnya benar-benar terjadi.

### Mode terang (default)

| Peran | Token | Nilai | Alasan |
|---|---|---|---|
| Kanvas | `AppBackgroundBrush` | `#F5F6F8` | Netral sedikit dingin; memisahkan kanvas dari permukaan tanpa garis tebal |
| Permukaan | `SurfaceBrush` | `#FFFFFF` | Daerah kerja utama: daftar, header, status bar |
| Permukaan alternatif | `SurfaceAltBrush` | `#F0F1F4` | Hover baris, kotak grafik, isian sekunder |
| Garis | `BorderBrush` | `#DFE1E6` | Batas 1px; cukup terlihat, tidak memotong |
| Tinta utama | `TextPrimaryBrush` | `#1A1A20` | 17,32:1 di atas permukaan |
| Tinta sekunder | `TextSecondaryBrush` | `#6B6B76` | 5,26:1 di atas permukaan; untuk URL, label, keterangan |
| Aksen | `AccentBrush` | `#2D6CDF` | 4,86:1 di atas permukaan; tombol utama + progres berjalan |
| Aksen hover | `AccentHoverBrush` | `#245BB8` | Arah gelap saat hover, bukan arah terang; teks putih tetap lolos |
| Sukses | `SuccessBrush` | `#0F7A46` | 5,39:1; teks status "Selesai" dan angka kecepatan |
| Bahaya | `DangerBrush` | `#C42B2B` | Teks status "Gagal" |
| Peringatan | `WarningBrush` | `#8A5B00` | Teks status "Dijeda" dan "Memeriksa" |
| Rel progres | `TrackBrush` | `#E6E8EC` | Bagian progres yang belum terisi |

### Mode gelap

| Peran | Token | Nilai | Alasan |
|---|---|---|---|
| Kanvas | `AppBackgroundBrush` | `#1E1E22` | Lebih gelap dari permukaan agar kartu terbaca sebagai lapisan di atas |
| Permukaan | `SurfaceBrush` | `#26262B` | |
| Permukaan alternatif | `SurfaceAltBrush` | `#2E2E34` | |
| Garis | `BorderBrush` | `#3A3A42` | |
| Tinta utama | `TextPrimaryBrush` | `#F0F0F4` | 13,25:1 |
| Tinta sekunder | `TextSecondaryBrush` | `#A0A0AC` | 5,82:1 |
| Aksen | `AccentBrush` | `#366FE0` | 4,66:1 untuk teks putih tombol; biru terang gagal AA sebagai latar tombol |
| Aksen hover | `AccentHoverBrush` | `#2F63CC` | |
| Sukses | `SuccessBrush` | `#3DD68C` | 8,03:1 |
| Bahaya | `DangerBrush` | `#FF6B6B` | 5,43:1 |
| Peringatan | `WarningBrush` | `#FFB84D` | 8,76:1 |
| Rel progres | `TrackBrush` | `#1A1A1E` | Lebih gelap dari permukaan agar rel terbaca |

### Aturan warna

- **Kenapa biru, bukan ungu.** Ungu-biru bergradien adalah penanda paling jelas
  hasil default AI. Biru tunggal yang tenang adalah warna alat (bukan warna
  kampanye) dan tidak menarik perhatian lebih dari data.
- **Tidak ada gradien.** Warna solid saja; gradien tidak membawa informasi.
- **Tidak ada bayangan lembut.** Kedalaman dinyatakan lewat warna permukaan dan
  garis 1px. Bayangan besar di aplikasi padat informasi hanya mengaburkan tepi.
- **Warna status wajib lolos AA sebagai teks.** Nilai `#1FA463` dan `#C88000`
  yang dipakai sebelumnya hanya mencapai 3,21:1 di atas putih; keduanya digelapkan
  hingga lolos 4,5:1. Dikunci oleh `ThemeContrastTests`.
- **Aksen mode gelap digelapkan, bukan diterangkan.** Putih di atas `#4C8DFF` hanya
  3,20:1. `#366FE0` memberi 4,66:1.

---

## 4. Tipografi

Satu keluarga: **Segoe UI**, font sistem Windows.

- Alasan: pada aplikasi desktop Windows, font sistem menghilangkan beban unduh,
  konsisten dengan UI lain di mesin yang sama, dan punya *hinting* terbaik di
  layar ber-DPI campuran. Mengganti ke web font hanya menambah latensi dan
  membuat aplikasi terasa asing.

| Peran | Ukuran | Berat | Warna |
|---|---|---|---|
| Nama aplikasi | 16 | Bold | Tinta utama |
| Nama berkas | 13 | SemiBold | Tinta utama |
| Angka kecepatan total | 15 | SemiBold | Sukses |
| Isi umum | 12 | Regular | Tinta utama |
| Label kolom | 11 | SemiBold | Tinta sekunder |
| Keterangan, URL, satuan | 11 | Regular | Tinta sekunder |

- **Kenapa hierarki hanya 11/12/13/15/16.** Rentang sempit memaksa hierarki lewat
  berat dan warna, bukan ukuran yang melompat-lompat. Di jendela 1080px, ukuran
  yang lebih bervariasi justru membuat baris tabel tidak rata.
- Angka memakai angka tabular agar kecepatan dan ukuran tidak "bergetar" saat
  nilainya berubah.

---

## 5. Ruang dan bentuk

- **Skala jarak:** 4, 8, 12, 14, 16, 20 px. Tidak ada nilai di antaranya.
- **Radius:** 6 px untuk kontrol (tombol, input), 8 px untuk kartu dan panel,
  3 px untuk rel progres. Radius adalah alat hierarki, bukan dekorasi; tidak ada
  elemen berbentuk pil.
- **Tinggi baris daftar:** 62 px. Cukup untuk tiga baris informasi (nama, URL,
  progres) tanpa membuat daftar terasa longgar.
- **Header 84 px, status bar 34 px.** Keduanya tetap; hanya area daftar yang
  mengembang.

**Mengapa lebar tetap minimum 860 px.** Di bawah itu, empat kolom informasi tidak
lagi terbaca sekaligus dan tabel mulai memotong nama berkas. Batas ini disengaja,
bukan angka asal.

---

## 6. Komposisi layar utama

```
+--------------------------------------------------------------+
| Mark  SwiftGet          kecepatan total  [grafik]    [Tema]  |  header
|       Unduhan segmented 1,2 MB/s  3 unduhan - 1 selesai      |
+--------------------------------------------------------------+
| [ URL berkas ................................. ] [Unduh]     |  input
|                                       [Antrean]  [Folder]    |
+--------------------------------------------------------------+
| [Jeda semua] [Lanjutkan semua] [Hapus selesai]      status   |  aksi massal
+--------------------------------------------------------------+
| Berkas              Status        Ukuran      Kecepatan      |
| ------------------------------------------------------------ |
| nama.zip            Mengunduh     4,2/12 MB   1,1 MB/s       |  daftar
| https://...         3/8 bagian    35%         22s tersisa    |
| #############----------------------------------------------- |
+--------------------------------------------------------------+
| Folder: C:\Users\...\SwiftGet    3 unduhan - 1 selesai [Pengaturan] |
+--------------------------------------------------------------+
```

**Focal point tunggal:** kotak URL di baris atas. Itu satu-satunya tempat
pengguna memulai sesuatu, jadi ia mendapat lebar maksimum dan satu-satunya tombol
berisi penuh.

**Kenapa input di atas, bukan di dialog.** Alur tersering adalah menempel URL
lalu menekan Enter. Dialog menambah dua klik untuk hal yang dilakukan puluhan
kali sehari.

**Kenapa grafik kecepatan diheader, bukan di setiap baris.** Grafik per baris
memakan ruang dan mengulang informasi yang sudah ada di angka kecepatan.
Ringkasan agregat di header menjawab pertanyaan yang berbeda: *apakah bandwidth
saya terpakai penuh saat ini?*

**Kenapa "Tambah ke antrean" berupa tombol sekunder, bukan dropdown.** Dua
tindakan (langsung unduh, atau masukkan antrean) lebih cepat dipilih sebagai dua
tombol daripada satu tombol dengan menu.

---

## 7. Komponen

| Komponen | Keputusan | Alasan |
|---|---|---|
| Tombol utama | Isi aksen penuh, radius 6, teks putih 13 SemiBold, padding 16 x 8 | Hanya satu per layar; pengguna tidak perlu mencari mana aksi utama |
| Tombol sekunder | Transparan, garis 1px, radius 6 | Tersedia tanpa berkompetisi dengan aksi utama |
| Tombol baris | Ukuran 26 px, muncul sesuai keadaan | Aksi yang tidak mungkin dilakukan tidak ditampilkan alih-alih di-disable |
| Input URL | Permukaan putih, garis 1px, radius 6, fokus menjadi aksen | Batas fokus adalah satu-satunya penanda posisi keyboard |
| Rel progres | 6 px, radius 3, aksen di atas rel netral | Cukup tipis untuk tidak mendominasi baris, cukup tebal untuk terlihat |
| Kartu | Permukaan, garis 1px, radius 8, padding 16 | Memisahkan kelompok tanpa bayangan |
| Grafik kecepatan | Batang 1,5 - 4 px, aksen, tanpa sumbu dan label | Menjawab "apakah bandwidth terpakai" dalam sekali lihat; angka persisnya sudah ada di sebelahnya |

---

## 8. Keadaan (states)

Setiap keadaan punya kalimat sendiri. Tidak ada "Tidak ada data".

| Keadaan | Yang ditampilkan |
|---|---|
| Kosong (pertama kali) | "Belum ada unduhan" + "Tempel URL di kotak atas, lalu tekan Enter." |
| Memeriksa | Status "Memeriksa", progres belum bergerak, kecepatan "-" |
| Mengunduh | Progres bergerak, "3/8 bagian", kecepatan, sisa waktu |
| Dijeda | Status "Dijeda", tombol berubah menjadi "Lanjut" |
| Gagal | Status "Gagal", pesan kesalahan ditampilkan apa adanya |
| Selesai | Status "Selesai", progres penuh 100%, tombol aksi hilang |
| Dibatalkan | Baris dihapus dari daftar |

**Keadaan kecepatan rendah.** Grafik tanpa transfer menggambar garis dasar tipis,
bukan kotak kosong. Kotak kosong terbaca sebagai "grafik rusak".

**Belum ada keadaan loading global.** Operasi terpanjang (probe URL) berlangsung
di bawah satu detik pada koneksi normal dan sudah ditandai lewat status
"Memeriksa". Menambah spinner layar penuh akan lebih banyak mengganggu daripada
menginformasikan.

---

## 9. Aksesibilitas

Diukur, bukan diklaim. Semua pasangan warna di Bagian 3 diperiksa dengan rumus
luminans WCAG dan dikunci oleh test otomatis (`ThemeContrastTests`, 24 kasus).

- Teks normal >= 4,5:1; elemen non-teks dan teks besar >= 3:1.
- Rasio dihitung dari nilai mentah; 4,49 tidak dibulatkan menjadi lolos.
- Fokus keyboard terlihat: garis input berubah warna aksen saat fokus.
- Semua aksi terjangkau keyboard lewat Tab; Enter di kotak URL menjalankan unduhan.
- Status tidak hanya dibedakan warna: setiap baris membawa label teks
  ("Selesai", "Gagal", "Dijeda").
- Target klik minimum 26 px pada tombol baris, 32 px pada tombol utama.

---

## 10. Gerak

- **MOTION 1.** Hanya keadaan hover dan perubahan data.
- Transisi hover: 120 ms, hanya warna latar.
- Tidak ada animasi masuk, tidak ada skeleton berdenyut, tidak ada loop.
- Grafik diperbarui setiap 500 ms dan menggeser satu batang per pembaruan; gerak
  ini berasal dari data, bukan dari dekorasi.
- Animasi dinonaktifkan seluruhnya bila pengguna memilih "kurangi gerakan" di
  Windows (target Rilis 2; belum diimplementasikan).

---

## 11. Suara penulisan (UI copy)

- Bahasa Indonesia, kalimat pendek, kata kerja konkret.
- Label tombol menyebut tindakannya: "Unduh", "Jeda", "Lanjut", "Batal",
  "Buka folder". Bukan "Mulai", bukan "Lanjutkan!".
- Tanpa seru, tanpa emoji, tanpa tanda pisah panjang.
- Pesan galat menyebut penyebab yang bisa ditindaklanjuti, bukan kode.
  Contoh: "Server tidak menyatakan panjang berkas; tidak bisa mengunduh dengan aman."
- Tidak ada klaim tanpa bukti. Angka yang ditampilkan adalah angka sebenarnya.

---

## 12. Ditolak dengan alasan

| Pola | Alasan ditolak |
|---|---|
| Gradien ungu-biru | Penanda default AI; tidak membawa informasi |
| Bento grid / kartu fitur | Ini aplikasi kerja, bukan halaman pemasaran |
| Bar samping kiri + empat kartu statistik | Shell dashboard generik; layar ini tidak punya empat metrik setara |
| Bayangan lembut besar | Mengaburkan tepi di layar padat data |
| Radius > 8 px / bentuk pil | Menghilangkan hierarki bentuk dan memakan ruang vertikal |
| Emoji sebagai ikon status | Tidak terbaca oleh pembaca layar dan tidak konsisten lintas font |
| Animasi pulse pada unduhan aktif | Data sudah bergerak; pulse hanya menambah kebisingan |
| Data contoh di tabel | Menyesatkan; keadaan kosong yang jujur lebih baik |
| Mode gelap sebagai default tanpa alasan | Ini alat, bukan editor kode; pengguna diberi pilihan, bawaan mengikuti sistem |

---

## 13. Peta token ke kode

| Dokumen | Kode |
|---|---|
| Bagian 3 (warna) | `src/SwiftGet.App/Infrastructure/ThemeManager.cs` |
| Bagian 4 (tipografi), 5 (bentuk) | `src/SwiftGet.App/Themes/Styles.xaml` |
| Bagian 6 (komposisi) | `src/SwiftGet.App/MainWindow.xaml` |
| Bagian 7 (grafik) | `src/SwiftGet.App/Themes/SpeedGraph.cs` |
| Bagian 9 (kontras) | `src/SwiftGet.App/Infrastructure/Contrast.cs`, `tests/SwiftGet.Tests/ThemeContrastTests.cs` |

---

## 14. Celah yang diketahui

Ditulis terbuka supaya tidak terbaca sebagai sudah beres.

- **Tema tidak tersimpan.** Pilihan terang/gelap kembali ke terang setiap
  aplikasi dijalankan. Perlu field di `AppSettings`.
- **Belum mengikuti tema sistem.** Bawaan seharusnya membaca preferensi Windows.
- **"Kurangi gerakan" belum dihormati.**
- **Grafik kecepatan tanpa label sumbu.** Disengaja untuk ukuran ini, tetapi pada
  jendela sangat lebar grafik bisa terbaca seperti dekorasi. Perlu dievaluasi bila
  lebar grafik melewati 240 px.
- **Baris tabel belum bisa diurutkan.** Kolom yang bisa diurutkan harus dipilih
  dari kebutuhan (paling mungkin: Status dan Ukuran), bukan semua kolom.
