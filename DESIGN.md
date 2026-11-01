# SwiftGet - DESIGN.md

Arah desain untuk antarmuka SwiftGet. Dokumen ini mengikat. Aturan `antislop`
menghapus yang generik; dokumen ini menentukan mau dibawa ke mana.

Dial: **ENERGY 2 / RHYTHM 2 / MOTION 2**

Versi 2.0. Menggantikan versi 1.0 yang berhenti di "bersih tapi belum hidup".

---

## 1. Masalah pada versi 1.0

Versi sebelumnya lolos aturan tetapi melakukan kesalahan yang justru dilarang
`antislop-ui` dalam bagian "Sterile Default": bersih, rapi, dan tidak punya
karakter. Yang salah bukan warnanya, tetapi tidak ada satu pun keputusan yang
menyatakan "ini SwiftGet dan bukan aplikasi lain".

Yang berubah di 2.0:

| Aspek | 1.0 | 2.0 | Sebab |
|---|---|---|---|
| Palet | Biru netral `#2D6CDF` | Hijau-teal `#0F6B5C` | Biru adalah pilihan default; teal memberi identitas dan tetap terbaca sebagai alat |
| Denah baris | Satu baris, progres di bawah teks | Baris adaptif dengan mode ringkas | Denah mengikuti keputusan pengguna, bukan mengikuti lebar kolom |
| Grafik | Batang di dalam kotak | Sparkline garis dengan garis dasar bergerak | Menjawab "tren kecepatan", bukan sekadar "sedang ada transfer" |
| Struktur | Header, input, daftar | Header, input, daftar, **panel periksa** | Pengguna perlu melihat bagian mana yang sedang diunduh, bukan hanya persen |
| Gerak | Hover saja | MOTION 2: transisi keadaan nyata | Unduhan segmented punya peristiwa yang layak ditampilkan |

---

## 2. Design Read

> Dibaca sebagai: aplikasi desktop utilitas untuk satu pengguna yang mengunduh
> berkas besar, dengan bahasa visual **instrument panel**: rapat, presisi,
> tenang, dan bergerak hanya ketika ada yang benar-benar berubah. Dial
> ENERGY 2 / RHYTHM 2 / MOTION 2.

**Pengguna dan konteks.** Satu orang, sering membiarkan jendela terbuka di
samping aplikasi lain. Pertanyaan yang dijawab layar, berurutan: apakah unduhan
selesai, seberapa cepat, berapa lama lagi, dan **bagian mana yang tertinggal**.

**Mengapa ENERGY 2.** Warna dan tipografi harus tenang karena dipandang lama.
Yang memberi energi bukan dekorasi, melainkan **data yang hidup**: garis
kecepatan yang bergerak, angka yang naik, segmen yang menyala satu per satu.
Nol energi menghasilkan kotak abu-abu; energi 3 bersaing dengan pekerjaan
sebenarnya.

**Mengapa RHYTHM 2.** Tiga pita atas berbeda kepadatan dari area daftar, dan
panel periksa di kanan memecah keseragaman. Bukan RHYTHM 3 karena jendela ini
dipakai berulang, bukan halaman naratif yang dibaca sekali.

**Mengapa MOTION 2.** Naik dari 1 karena ada peristiwa nyata yang perlu
dikomunikasikan: segmen selesai, unduhan dimulai, status berubah, panel dibuka.
Setiap gerak punya alasan. Tidak ada gerak hiasan.

---

## 3. Referensi dan apa yang diambil

Referensi dipakai sebagai **inspirasi teknik, bukan template untuk ditiru**
(R-30). Tidak ada produk yang dipalsukan.

### IBM Carbon - color palettes & chart anatomy

Sumber: `carbondesignsystem.com/building-blocks/data-visualization/color-palettes`

- **Yang diambil:** disiplin palet kategorikal yang dibatasi dan kurasi urutan
  warna agar kontras antar warna bertetangga maksimal. Juga prinsip alert palette:
  merah = bahaya, oranye = peringatan serius, hijau = normal.
- **Yang diadopsi:** empat warna status dengan makna tetap, dan aturan bahwa
  warna status **tidak** dipakai untuk dekorasi.
- **Yang ditolak:** palet 14 warna kategorikal. SwiftGet tidak punya 14 kategori
  data; memakai palet sebesar itu justru melanggar R-29.

### IBM Carbon - empty states

Sumber: `carbondesignsystem.com/building-blocks/core/patterns/empty-states`

- **Yang diambil:** anatomi keadaan kosong (judul + penjelasan + satu tindakan
  utama), dan aturan "empty state menggantikan elemen yang biasanya menampilkan
  data", termasuk **header kolom ikut hilang** agar pembaca layar tidak membaca
  tabel kosong lebih dulu.
- **Yang diadopsi:** tiga jenis keadaan kosong berbeda: belum ada data,
  hasil tindakan, dan galat. Masing-masing punya kalimat sendiri.
- **Yang ditolak:** gambar ilustrasi. SwiftGet tidak punya ilustrasi orisinal,
  dan R-22 melarang ilustrasi generik.

### IBM Carbon - chart anatomy

- **Yang diambil:** sumbu tanpa garis kotak penuh, label seminimal mungkin, dan
  prinsip bahwa grafik harus menjawab satu pertanyaan yang bisa disebutkan.
- **Yang diadopsi:** pertanyaan grafik SwiftGet ditulis eksplisit:
  *"apakah bandwidth saya terpakai penuh saat ini, dan apakah trennya naik atau
  turun?"*.

### NN/g - progress indicators

Sumber: `nngroup.com/articles/progress-indicators`

- **Yang diambil:** persen-selesai dipakai untuk proses lebih dari 10 detik;
  indikator berputar hanya untuk 2 sampai 10 detik; indikator statis dilarang.
  Juga: perubahan kecepatan akan diperhatikan pengguna, jadi grafik kecepatan
  bukan hiasan melainkan pemenuhan ekspektasi.
- **Yang diadopsi:** unduhan (selalu lebih dari 10 detik untuk berkas besar)
  memakai persen, kecepatan, dan sisa waktu sekaligus. Fase periksa URL (di
  bawah 2 detik) memakai indikator garis tipis, bukan spinner.
- **Yang ditolak:** animasi hiburan saat menunggu. Pengguna sedang bekerja.

### Linear, Vercel, Raycast (diamati, tidak ditiru)

- **Yang diambil:** kepadatan tinggi dengan hierarki tipografi sempit,
  permukaan gelap berlapis dengan garis 1px, dan status yang dibaca dari warna
  teks bukan lencana.
- **Yang ditolak:** menyalin tata letaknya (R-30). SwiftGet punya struktur
  sendiri: pita input di atas, daftar di tengah, panel periksa di kanan.

---

## 4. Warna

Basis netral hangat (bukan abu-abu biru), tinta gelap, dan **satu aksen
hijau-teal**. Empat warna status hanya muncul saat statusnya terjadi.

### Mode terang

| Peran | Token | Nilai | Kontras terukur | Alasan |
|---|---|---|---|---|
| Kanvas | `AppBackgroundBrush` | `#F7F7F4` | - | Netral hangat; memisahkan kanvas dari permukaan tanpa garis tebal |
| Permukaan | `SurfaceBrush` | `#FFFFFF` | - | Daerah kerja: daftar, header, panel |
| Permukaan alt | `SurfaceAltBrush` | `#EFEFEA` | - | Hover, isian, kolom kiri panel |
| Garis | `BorderBrush` | `#E2E2DB` | - | Pemisah 1px, senada dengan kanvas |
| Tinta | `TextPrimaryBrush` | `#16171A` | 17,92:1 di permukaan | Nyaris hitam, bukan hitam murni, agar tidak bergetar di layar OLED |
| Tinta sekunder | `TextSecondaryBrush` | `#5F6169` | 6,18:1 | URL, label, satuan |
| Aksen | `AccentBrush` | `#0F6B5C` | 6,41:1; putih di atasnya 6,41:1 | Teal gelap: terbaca sebagai alat, bukan kampanye |
| Aksen hover | `AccentHoverBrush` | `#0B5548` | putih di atasnya 8,72:1 | Arah gelap saat hover; teks putih makin tegas, bukan makin pudar |
| Sukses | `SuccessBrush` | `#0F7A46` | 5,39:1 | Status selesai |
| Bahaya | `DangerBrush` | `#B3261E` | 6,54:1 | Status gagal |
| Peringatan | `WarningBrush` | `#8A5B00` | 5,87:1 | Status dijeda dan memeriksa |
| Rel | `TrackBrush` | `#E6E6DF` | - | Bagian progres yang belum terisi |

### Mode gelap

| Peran | Token | Nilai | Kontras terukur |
|---|---|---|---|
| Kanvas | `AppBackgroundBrush` | `#141518` | - |
| Permukaan | `SurfaceBrush` | `#1B1D21` | - |
| Permukaan alt | `SurfaceAltBrush` | `#24262B` | - |
| Garis | `BorderBrush` | `#33363C` | - |
| Tinta | `TextPrimaryBrush` | `#EDEDEF` | 14,43:1 |
| Tinta sekunder | `TextSecondaryBrush` | `#9B9EA6` | 6,30:1 |
| Aksen | `AccentBrush` | `#3FB39C` | 6,54:1; tinta gelap `#0B201C` di atasnya 6,58:1 |
| Aksen hover | `AccentHoverBrush` | `#57C7B0` | - |
| Sukses | `SuccessBrush` | `#3DD68C` | 9,00:1 |
| Bahaya | `DangerBrush` | `#FF7A6E` | 6,64:1 |
| Peringatan | `WarningBrush` | `#FFB84D` | 9,82:1 |
| Rel | `TrackBrush` | `#111214` | - |

**Teks di atas tombol aksen berbeda per tema.** Mode terang memakai putih di
atas teal gelap (6,41:1). Mode gelap memakai tinta gelap `#0B201C` di atas teal
terang (6,58:1); putih di atas `#3FB39C` hanya 2,4:1 dan gagal.

### Aturan warna

- **Kenapa teal, bukan biru.** Biru adalah warna tautan bawaan setiap sistem;
  di aplikasi utilitas ia menghilang. Teal gelap memberi identitas tanpa
  berteriak, dan tetap dingin sehingga cocok untuk alat.
- **Kenapa kanvas hangat.** Putih kebiruan membuat jendela terasa seperti
  dokumen. Netral hangat membuat permukaan putih terbaca sebagai panel alat.
- **Tanpa gradien.** R-01: gradien hanya sah bila memisahkan hierarki. Di sini
  hierarki sudah dinyatakan warna permukaan dan garis, jadi gradien tidak
  menambah apa pun.
- **Warna status bukan dekorasi.** Merah hanya untuk gagal, kuning hanya untuk
  dijeda atau memeriksa. Titik status kecil memakai warna yang sama dengan teks
  statusnya, bukan warna baru.
- Semua pasangan dikunci oleh `ThemeContrastTests` (lihat bagian 12).

---

## 5. Tipografi

Dua keluarga dengan pembagian tugas yang tegas.

| Peran | Keluarga | Alasan |
|---|---|---|
| Antarmuka | **Segoe UI Variable** (fallback Segoe UI) | Font sistem Windows: tanpa unduhan, hinting terbaik, dan sepadan dengan UI lain di mesin yang sama |
| Angka dan URL | **Cascadia Mono** (fallback Consolas) | Ukuran byte, kecepatan, dan sisa waktu berubah puluhan kali per detik; angka tabular mencegah lebar berubah dan baris bergetar |

**Kenapa monospace hanya untuk angka.** Ini bukan estetika terminal (R-06
melarangnya). Alasannya mekanis: `1,1 MB/s` dan `11,8 MB/s` harus menempati
lebar sama, jika tidak kolom kecepatan bergoyang setiap pembaruan.

| Peran | Ukuran | Berat | Keluarga |
|---|---|---|---|
| Nama aplikasi | 15 | Bold | UI |
| Nama berkas di baris | 13 | SemiBold | UI |
| Kecepatan total | 20 | SemiBold | Mono |
| Angka baris | 12 | Regular | Mono |
| Isi umum | 12 | Regular | UI |
| Label kolom dan bagian | 11 | SemiBold | UI |
| Keterangan | 11 | Regular | UI |

Hierarki dibangun dari **berat dan warna**, bukan dari lompatan ukuran. Rentang
11 sampai 20 px cukup untuk jendela selebar 1080 px.

---

## 6. Ruang dan bentuk

- **Skala jarak:** 2, 4, 8, 12, 16, 20, 24 px.
- **Radius:** 4 px kontrol kecil, 6 px tombol dan input, 10 px kartu dan panel,
  2 px rel progres. Tidak ada bentuk pil (R-11).
- **Tinggi baris daftar:** 64 px mode penuh, 40 px mode ringkas.
- **Lebar panel periksa:** 300 px, bisa ditutup. Di bawah 1000 px panel menjadi
  lapisan di atas daftar.
- **Lebar minimum jendela:** 900 px. Di bawahnya, empat kolom informasi tidak
  lagi terbaca sekaligus.

---

## 7. Denah baris unduhan (keputusan inti)

Versi 1.0 memaksa setiap baris memuat semua informasi, sehingga berkas kecil
dan besar diperlakukan sama. Versi 2.0 memberi **dua mode**:

**Mode penuh** (unduhan aktif):
```
nama-berkas.zip                                        [Jeda] [Batal]
https://contoh.test/berkas.zip
Mengunduh   4,2 / 12 MB   1,1 MB/s   22s tersisa
[========--------------------------]  35%   3/8 bagian
```

**Mode ringkas** (selesai atau dijeda):
```
nama-berkas.zip                              Selesai   12 MB   [Buka]
https://contoh.test/berkas.zip
```

Alasan: setelah unduhan selesai, kecepatan dan sisa waktu menjadi nol informasi
yang menempati ruang. Merangkum baris selesai membuat daftar tetap terbaca saat
berisi puluhan entri. Ini menjawab R-14 secara terbalik: bukan semua kartu sama,
melainkan **baris mengikuti keadaan isinya**.

---

## 8. Panel periksa (fitur interaktif utama)

Klik satu baris untuk membuka panel di kanan berisi:

- **Peta segmen.** Satu batang per segmen. Segmen selesai berwarna aksen;
  segmen berjalan bergaris berdenyut pelan; segmen menunggu berwarna rel.
  Diurutkan sesuai urutan byte, dari kiri ke kanan.
  *Pertanyaan yang dijawab: bagian mana yang tertinggal.*
- **Ringkasan** dalam daftar kunci-nilai: URL, ukuran, tipe, ETag, dukungan
  Range, jumlah koneksi, folder tujuan.
- **Grafik kecepatan unduhan ini** (sparkline 60 detik).
- **Aksi:** Jeda, Lanjut, Batal, Buka folder, Salin URL.

**Kenapa ini ada.** Unduhan segmented berperilaku tidak merata: satu koneksi bisa
tertinggal jauh. Tanpa peta segmen, pengguna hanya melihat persen yang macet
tanpa tahu sebabnya. Peta segmen mengubah keluhan "kenapa lambat" menjadi
informasi yang bisa dibaca.

**Kenapa di panel, bukan di dalam baris.** Delapan batang segmen di setiap baris
akan memenuhi daftar dan mendorong nama berkas keluar layar.

---

## 9. Grafik kecepatan

Dua grafik, dua pertanyaan berbeda:

| Tempat | Bentuk | Pertanyaan |
|---|---|---|
| Header | Sparkline garis 60 titik, garis dasar bergerak | Apakah bandwidth terpakai penuh, dan trennya naik atau turun? |
| Panel periksa | Sparkline garis 60 titik, skala sendiri | Apakah unduhan ini stabil atau tersendat? |

- Menggambar **garis** bukan batang: yang dibaca adalah tren, dan garis lebih
  tepat untuk itu pada lebar 180 px.
- Area di bawah garis diisi aksen dengan opasitas 12 persen. Ini bukan dekorasi:
  ia memberi bobot visual supaya garis tipis tidak hilang di latar.
- Tanpa sumbu, tanpa label angka, tanpa legenda. Angka persisnya sudah ada di
  teks di sebelah grafik. Menambahkan sumbu pada 180 px hanya menambah tinta
  tanpa menambah bacaan.
- Tanpa transfer, grafik menggambar garis dasar datar di posisi nol, bukan kotak
  kosong. Kotak kosong terbaca sebagai grafik rusak.
- Titik terakhir diberi titik 3 px sebagai penanda "sekarang".

---

## 10. Gerak (MOTION 2)

Setiap gerak punya pemicu dan alasan. Tidak ada animasi yang berjalan sendiri.

| Gerak | Durasi | Pemicu | Alasan |
|---|---|---|---|
| Hover baris dan tombol | 120 ms | Kursor masuk | Menandai sasaran sebelum diklik |
| Fokus keyboard | 120 ms | Tab | Menunjukkan posisi; cincin fokus 2 px aksen |
| Baris mengembang ke mode penuh | 200 ms, ease-out | Unduhan mulai | Menyatakan bahwa pekerjaan dimulai |
| Baris merangkum ke mode ringkas | 200 ms, ease-out | Unduhan selesai | Menarik perhatian ke satu peristiwa, lalu menenangkan daftar |
| Panel periksa masuk | 180 ms, ease-out | Baris dipilih | Menyatakan panel itu milik baris yang dipilih |
| Segmen selesai menyala | 260 ms, warna | Segmen mencapai 100 persen | Umpan balik per bagian; ini inti produk |
| Denyut segmen berjalan | 1,4 detik, loop | Hanya segmen aktif | Menandai "masih bekerja". Berhenti saat dijeda |
| Garis kecepatan bergeser | 500 ms | Data baru | Data yang bergerak adalah animasi utamanya |

**Batas.** Maksimal tiga elemen bergerak bersamaan di satu layar. Denyut hanya
pada segmen aktif, tidak pada tombol, lencana, atau latar. Tidak ada fade-up,
tidak ada parallax, tidak ada bounce.

**Pengurangan gerak.** Bila pengguna memilih "kurangi gerakan" di Windows,
semua durasi menjadi 0 dan denyut berhenti. Garis kecepatan tetap diperbarui,
karena itu data, bukan animasi.

---

## 11. Keadaan

Setiap keadaan punya kalimat sendiri, bukan satu pesan generik.

| Keadaan | Judul | Isi | Tindakan |
|---|---|---|---|
| Kosong, belum pernah unduh | "Belum ada unduhan" | "Tempel URL di kotak atas, lalu tekan Enter." | Fokus ke kotak URL |
| Kosong, semua dihapus | "Daftar kosong" | "Unduhan yang dihapus tidak muncul lagi di sini." | - |
| Tidak ada hasil filter | "Tidak ada yang cocok" | "Tidak ada unduhan berstatus gagal." | "Hapus filter" |
| Memeriksa | "Memeriksa URL" | "Menanyakan ukuran dan dukungan Range ke server." | Batal |
| Mengunduh | "Mengunduh" | "3 dari 8 bagian selesai, 22 detik lagi." | Jeda |
| Dijeda | "Dijeda" | "Progres tersimpan. Lanjutkan kapan saja." | Lanjut |
| Gagal | "Gagal" | Pesan galat apa adanya dari engine | Coba lagi, Salin pesan |
| Selesai | "Selesai" | Ukuran akhir dan waktu | Buka folder |

**Keadaan kosong menggantikan tabel sepenuhnya.** Header kolom ikut hilang
(aturan Carbon), agar pembaca layar tidak membaca tabel kosong lebih dulu.

**Fase periksa memakai indikator garis tipis bergerak**, bukan spinner berputar.
Alasannya dari NN/g: proses di bawah dua detik tidak boleh memakai animasi
berulang, karena pengguna tidak sempat mengikuti apa yang terjadi.

---

## 12. Aksesibilitas

Diukur lewat rumus luminans WCAG, dikunci oleh test.

- Teks normal minimal 4,5:1, teks besar dan elemen non-teks minimal 3:1.
- Rasio dihitung dari nilai mentah; 4,49 tidak dibulatkan menjadi lolos.
- **Teks tombol aksen berbeda per tema.** Ini bukan inkonsistensi, melainkan
  konsekuensi kontras: putih di atas teal terang gagal.
- Fokus keyboard selalu terlihat: cincin 2 px aksen dengan offset 2 px.
- Setiap aksi terjangkau Tab dan dapat dijalankan Enter atau Space.
- Escape menutup panel periksa dan dialog pengaturan.
- Status tidak dibedakan warna saja; setiap status punya label teks.
- Peta segmen punya nama aksesibel yang menyebut jumlah bagian selesai.
- Target klik minimal 32 px pada tombol utama dan 28 px pada tombol baris.
- Bilah progres punya `role` progres dengan nilai persen yang diumumkan.

---

## 13. Suara penulisan

- Bahasa Indonesia, kalimat pendek, kata kerja konkret.
- Label menyebut tindakan: "Unduh", "Jeda", "Lanjut", "Batal", "Buka folder".
- Tanpa seru, tanpa emoji, tanpa tanda pisah panjang, tanpa huruf kapital penuh.
- Pesan galat menyebut sebab yang bisa ditindaklanjuti.
  Contoh: "Server tidak menyatakan panjang berkas, jadi tidak bisa dipecah
  menjadi beberapa bagian."
- Angka yang ditampilkan adalah angka sebenarnya dari engine. Tidak ada
  persentase karangan, tidak ada "dipercepat 300%".

---

## 14. Ditolak dengan alasan

| Pola | Alasan |
|---|---|
| Gradien ungu-biru | Penanda default; tidak memisahkan hierarki apa pun |
| Bento grid | Ini aplikasi kerja, bukan halaman pemasaran |
| Empat kartu statistik | SwiftGet tidak punya empat metrik setara |
| Bayangan lembut besar | Mengaburkan tepi di layar rapat informasi |
| Glassmorphism | Satu-satunya lapisan mengambang adalah panel periksa; ia memakai permukaan padat |
| Denyut pada tombol atau lencana | Denyut hanya menandai segmen yang benar-benar sedang bekerja |
| Spinner berputar untuk periksa URL | Proses di bawah dua detik; garis tipis lebih jujur |
| Angka contoh di kolom kosong | Menyesatkan; keadaan kosong yang jujur lebih baik |
| Ikon dari pustaka umum (Lucide dan sejenis) | Tidak relevan dengan isi; SwiftGet memakai panah unduhan sederhana yang digambar sendiri |

---

## 15. Peta token ke kode

| Dokumen | Kode |
|---|---|
| Bagian 4 (warna) | `src/SwiftGet.App/Infrastructure/ThemeManager.cs` |
| Bagian 5, 6 (tipografi dan bentuk) | `src/SwiftGet.App/Themes/Styles.xaml` |
| Bagian 7, 8, 9 (denah baris, panel, grafik) | `src/SwiftGet.App/MainWindow.xaml`, `Themes/SpeedGraph.cs` |
| Bagian 10 (gerak) | `src/SwiftGet.App/Themes/Styles.xaml`, `Infrastructure/Motion.cs` |
| Bagian 12 (kontras) | `Infrastructure/Contrast.cs`, `tests/SwiftGet.Tests/ThemeContrastTests.cs` |

---

## 16. Celah yang diketahui

Ditulis terbuka supaya tidak terbaca sebagai sudah beres.

- **Pilihan tema belum tersimpan** antar sesi, dan belum mengikuti tema Windows.
- **Panel periksa di layar sempit** masih menjadi lapisan sederhana; belum ada
  perangkap fokus penuh untuk pembaca layar.
- **"Kurangi gerakan"** membaca preferensi Windows saat mulai, tetapi tidak
  bereaksi bila preferensi diubah ketika aplikasi sudah berjalan.
- **Filter daftar** belum ada; keadaan "tidak ada hasil filter" sudah
  dirancang tetapi belum dapat dicapai sampai filter dibuat.
- **Urut kolom** belum tersedia. Bila ditambahkan, hanya Status dan Ukuran,
  karena hanya keduanya yang menjawab keputusan pengguna.
