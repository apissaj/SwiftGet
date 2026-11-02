# E2E ekstensi (manual harness)

Skenario end-to-end nyata: Edge + ekstensi dimuat + situs uji lokal +
aplikasi SwiftGet berjalan. Dipakai untuk regresi sebelum rilis.

Butuh: Node 18+, `npm i playwright-core` di folder ini (tidak di-commit),
Microsoft Edge terpasang, ffmpeg untuk membuat media uji.

Menjalankan:

```powershell
cd extension/e2e
npm init -y; npm i playwright-core
# 1. salakan endpoint: set settings.json (%LOCALAPPDATA%\SwiftGet)
#    enableLocalEndpoint=true, localEndpointPort=49160,
#    localEndpointToken="e2etokene2etokene2" (atau ubah skripnya)
# 2. jalankan aplikasi SwiftGet
python site_server.py          # terminal 1 (situs uji di 127.0.0.1:49777)
node e2e.js                    # terminal 2
```

Hasil: 13 pemeriksaan — opsi tersimpan, panel hover + kirim (tahap 1),
badge kandidat jaringan dari respons API + picker (tahap 2), unduhan HLS
berjalan di mesin SwiftGet (diverifikasi lewat history.json dan log server).
