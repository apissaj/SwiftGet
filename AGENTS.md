# AGENTS.md — SwiftGet

Petunjuk untuk agen coding (ZCode/Claude/Codex dkk.) yang bekerja di repo ini.
Baca ini sebelum mengubah apa pun supaya tidak salah pola.

## Apa ini

SwiftGet: pengelola unduhan Windows (pengganti IDM) — WPF .NET 8, C# `latest`,
`Nullable enable`. Semua teks pengguna, komentar kode, dan dokumentasi ditulis
dalam **bahasa Indonesia** dengan gaya tenang (lihat DESIGN.md §13 dan
`.antislop/`).

## Struktur

```
src/SwiftGet.Core/          Mesin unduhan — TIDAK BOLEH bergantung WPF/UI.
  Downloads/                DownloadManager, DownloadSession, DownloadTask, DownloadManifest
  Http/                     HttpRangeClient (Range/multi-koneksi), HeaderParsing, ProbeResult
  DownloadException.cs      Pengecualian khusus unduhan
src/SwiftGet.App/           UI WPF (net8.0-windows).
  ViewModels/               MainViewModel, DownloadsViewModel — MVVM murni, tanpa logika UI
  Infrastructure/           ThemeManager, Motion, SystemThemeWatcher, MotionPreferenceWatcher,
                            InspectorOverlay (FocusTrap), ClipboardWatcher, AppSettings,
                            Humanize, Contrast, Converters
  Themes/                   Styles.xaml (satu-satunya sumber style), SpeedGraph, SegmentMap
  MainWindow.xaml(.cs)      Jendela utama; satu-satunya pemanggil ThemeManager.Apply saat runtime
tests/SwiftGet.Tests/       xUnit, target net8.0. Semua perilaku baru wajib ada ujiannya.
DESIGN.md                   OTORITAS DESAIN. Setiap keputusan UI harus cocok dengannya.
docs/idm-behavior-notes.md  Catatan REA perilaku IDM (referensi mesin unduhan).
.antislop/                  Aturan anti-slop (komentar kode, copywriting, UI). Wajib dipatuhi.
build.ps1                   Build rilis; menjalankan test dulu, gagal test = batal publish.
scripts/update-antislop.ps1 Sinkronisasi aturan .antislop.
```

## Perintah

```powershell
dotnet build SwiftGet.sln -v q            # build cepat (0 warning adalah standar)
dotnet test tests/SwiftGet.Tests/SwiftGet.Tests.csproj -v q   # wajib lulus sebelum commit
./build.ps1 -Target portable              # rilis: portable | framework-dependent | all
```

## Aturan arsitektur yang mudah dilanggar

- **Core tidak menyentuh UI.** Uji perilaku jaringan/unduhan di
  `tests/SwiftGet.Tests`, bukan lewat UI.
- **Palet tema satu sumber kebenaran.** Hanya `MainWindow`
  (`OnViewModelPropertyChanged`) yang memanggil `ThemeManager.Apply(IsDark)`.
  Jangan membuat pemanggil kedua; watcher tema (`SystemThemeWatcher`) dipasang
  sekali di `MainWindow.SetupWatchers()` — bukan di `App.xaml.cs`.
- **Tema:** `AppSettings.DarkTheme` (`bool?`, disimpan ke
  `%LOCALAPPDATA%\SwiftGet\settings.json`). `null` = ikuti tema Windows.
  `ToggleTheme` bercycle tiga mode: gelap → terang → ikuti Windows.
- **Pengaturan** selalu lewat `AppSettings` + `Save()` (tulis atomic via `.tmp`).
- **Motion:** durasi animasi selalu lewat `Motion.Effective(...)`; jangan hardcode
  durasi. `ReduceMotion` membaca preferensi Windows dan diperbarui reaktif oleh
  `MotionPreferenceWatcher`.
- **Panel periksa:** dua mode — kolom kanan (jendela ≥ 1000px) dan lapisan
  overlay + scrim + `FocusTrap` (jendela sempit). Ambang: `OverlayBreakpoint`.
  Jangan mengubah tata letak panel tanpa memperbarui keduanya.
- **Single instance** via mutex `Local\SwiftGet.SingleInstance.v1`; jangan
  menambah jalur startup kedua.

## Konvensi menulis

- Komentar menjelaskan *mengapa*, bukan *apa* (lihat `.antislop/antislop-code.md`).
  Tidak ada komentar dekoratif atau yang mengulang baris di bawahnya.
- String UI bahasa Indonesia, konsisten dengan yang sudah ada (lihat
  `MainWindow.xaml` untuk nada kalimatnya).
- XAML: style baru masuk `Themes/Styles.xaml` dengan `x:Key`; warna selalu
  `DynamicResource` brush dari `ThemeManager`, jangan hardcode hex (kecuali scrim).
- Nama file/identifier mengikuti pola yang sudah ada di folder masing-masing.

## Alur kerja fase (Rilis 2)

Rencana besar dibagi fase dan **satu fase = satu commit**:

- [x] **Fase A** — tema tersimpan + ikut sistem, focus trap panel periksa (`efc0725`)
- [ ] **Fase B** — reduce-motion reaktif, filter daftar + urut kolom
- [ ] **Fase C** — verifikasi checksum otomatis
- [ ] **Fase D** — dynamic segmentation
- [ ] **Fase E** — kategori dan riwayat unduhan
- [ ] **Fase F** — endpoint lokal 127.0.0.1 + bookmarklet
- [ ] **Fase G** — HLS/DASH (.m3u8/.mpd)
- Terakhir: perbarui DESIGN.md (§16 celah) + README + laporan Delivery Gate.

Catatan penting: working tree boleh berisi pekerjaan fase yang belum selesai.
Sebelum commit suatu fase, pastikan hanya bagian fase itu yang masuk — jika
perubahan tercampur di satu file, gunakan staging parsial (`git apply --cached`
atau sunting-versi-stage lalu pulihkan), dan **verifikasi commit-tree sendiri**
(mis. lewat `git worktree add` sementara + build + test) sebelum commit.

## Commit

Format: `tipe(scope): ringkasan` — contoh riil repo ini:
`feat(design): ...`, `fix: ...`, `docs: ...`, `feat(core): ...`.
Commit hanya bila diminta atau bila fase selesai disepakati.
