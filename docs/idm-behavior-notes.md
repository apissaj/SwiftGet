# Catatan Perilaku IDM (hasil analisis REA) — acuan desain SwiftGet

> **Tujuan dokumen**: merekam hasil analisis statis terhadap `IDMan.exe` untuk memahami
> *teknik* yang dipakai IDM, lalu dipakai sebagai acuan desain SwiftGet.
>
> **Batasan etis**: dokumen ini hanya merekam ide & teknik yang sebagian besar juga
> dijelaskan publik (RFC 7233 Range, resume, segmentasi). Token/protokol privat IDM
> tidak direplikasi. Tidak ada kode, string protokol, atau aset IDM yang disalin ke
> produk kita. SwiftGet ditulis ulang dari nol.

## 1. Artefak yang dianalisis

| Item | Nilai |
|---|---|
| Path | `C:\Program Files (x86)\Internet Download Manager\IDMan.exe` |
| Versi terpasang | 6.43.15 |
| SHA-256 | `e3fc655a335e17a6fdd09a9b315291d0c8863180f0d801e5dd7433c9e56afbd8` |
| Format | PE, arsitektur x86 (32-bit) |
| Image base | `0x400000` |
| Provider analisis | Ghidra 12.1.4 (via REA) |
| Jumlah fungsi | 19.603 |
| Jumlah string terdefinisi | 7.261 |
| Segmen | Headers, `.text` (2.699.776 B), `.rdata` (938.496 B), `.data` (79.580 B), `.rsrc` (2.283.520 B), `.reloc` (226.304 B), `tdb` |

Catatan: tidak ada PDB yang dapat dimuat REA, sehingga nama simbol internal tidak
tersedia — fungsi muncul sebagai `FUN_xxxxxxxx`. Nama kelas C++ **masih** dapat
dipulihkan dari string RTTI di `.rdata`.

## 2. Header HTTP yang dikenali (bukti langsung)

Ditemukan sebagai literal string di `.rdata`. Ini membuktikan IDM memakai
mekanisme standar, bukan protokol proprietary:

| String | Alamat |
|---|---|
| `Range: bytes=%I64d-%I64d\r\n` | `0x6cfa00` |
| `Range: bytes=%I64d-\r\n` | `0x6cf9e8` |
| `Range: bytes=0-%I64d\r\n` | `0x6cf9bc` |
| `Range: bytes=0-\r\n` | `0x6cf9d4` |
| `Accept-Ranges` (req/resp, dua varian) | `0x6cfcc0`, `0x6d5d64` |
| `Content-Range:` | `0x6cf014`, `0x6d5ca4` |
| `Content-Disposition:` | `0x6cfbfc`, `0x6d5d00` |
| `ETag:` | `0x6cf024` |
| `Last-Modified:` | `0x6cfaf8`, `0x6d5c04` |
| `Connection: Keep-Alive\r\n` | `0x6cf81c` |
| `chunked` (Transfer-Encoding) | `0x6cf084` |
| `&bytes=%I64d-%I64d` (parameter query, mis. untuk server yang Range via query) | `0x69c720` |
| `&bytes=0-%I64d`, `&bytes=0-` | `0x6ca288`, `0x6ca298` |

**Implikasi untuk SwiftGet**: pakai `Range: bytes=<start>-<end>` standar,
parse `Accept-Ranges`, `Content-Range`, `Content-Length`, `ETag`,
`Last-Modified`, `Content-Disposition`. Tidak ada yang perlu di-reverse-engineer
di level protokol — cukup RFC 7233 + RFC 6266.

## 3. Arsitektur internal (dari RTTI & nama kelas C++)

Kelas-kelas yang terpulihkan dari string tipe C++ di `.rdata`:

| Kelas | Alamat RTTI | Peran (inferensi dari nama + pesan log) |
|---|---|---|
| `Connection` | `0x780430` | Base class koneksi; mengelola satu stream unduhan, batas chunk, state. |
| `HTTPConnection` | `0x7805b0` | Turunan `Connection`; tanggung jawab HTTP/HTTPS, header, status, redirect, cookie. |
| `HTTPSession` | (pesan log) | Mengelola **kumpulan** `HTTPConnection` untuk satu file (pool koneksi paralel). |
| `FTPConnection` | `0x78044c` | Turunan `Connection` untuk FTP (PASV/PORT, REST untuk resume). |
| `FTPSession` | (pesan log) | Padanan `HTTPSession` untuk FTP. |
| `ProxyConnection` | `0x7808a0` | Koneksi lewat proxy (HTTP CONNECT untuk HTTPS). |
| `CFTPProxyConnection` | `0x7808c0` | Proxy FTP. |
| `MMSConnection` | `0x7808e4` | Protokol MMS (legacy streaming). |
| `RTMPConnection` | `0x780900` | RTMP (streaming). |
| `ChunksList` | (pesan log) | **Inti segmentasi**: daftar chunk + algoritma alokasi/rebuild/join. |
| `ChunksListItem` | (pesan log) | Satu item chunk (file sementara, start/end, state). |
| `CM3UParser::MediaSegment` | `0x78061c` | Parser playlist **HLS (M3U8)** → objek segmen media. |

**Pola desain yang terlihat (dan akan kita tiru secara independen):**

```
HTTPSession            (1 per file)
 ├── max_connections   (user setting, mis. MaxConnectionsNumber)
 ├── ChunksList        (daftar segmen + status)
 │    ├── ChunksListItem  ── start, end, bytes_done, temp_file
 │    └── FindMaxFreeChunk / NeedNewConnection / ApplyAlternative / JoinFile
 └── Connection[]      (N koneksi aktif, masing-masing memegang 1 chunk)
      └── HTTPConnection (state machine + parser header)
```

## 4. Algoritma segmentasi (bukti dari pesan log)

Pesan log internal IDM membocorkan alur algoritmanya. Ini yang paling relevan
untuk SwiftGet.

### 4.1 Alokasi chunk
- `ChunksList::FindMaxFreeChunk()` — cari rentang belum terunduh terbesar → itulah
  yang dikerjakan koneksi baru. **Bukan** membagi rata di awal, tapi *work-stealing*
  dari sisa terbesar.
- `ChunksList::NeedNewConnection()` — sesi memutuskan butuh koneksi tambahan.
- `HTTPSession::TryStartNewOptionalConnections()` / `CreateOptionalConnections()` —
  menambah koneksi **opsional** secara dinamis.
- `Optional connections not needed.` / `Need optional connections.` — ada heuristik
  untuk berhenti menambah koneksi.
- `ChunksList::AddAlternative()` / `CheckAlternative()` / `ApplyAlternative()` —
  mekanisme bila server membatasi jumlah koneksi (alternatif strategi).

### 4.2 Ukuran chunk adaptif
- `Change chunk size to %ld` — ukuran chunk diubah saat runtime.
- `Try rollback, set chunksize to %I64d` — rollback posisi + ubah chunk size.
- `The last chunk is full.` — penanda chunk terakhir terisi.

Artinya: ukuran chunk **bukan konstanta**, melainkan hasil penyesuaian.

### 4.3 Penanganan error & korupsi (paling penting)
- `The file chunk of connection %d is reassigned to connection %d.` — bila satu
  koneksi gagal, chunk-nya **dipindahkan** ke koneksi lain.
- `IDM started to download the file using one connection because of data corruption
  when newly received data in several connections did not match the same data
  downloaded earlier.` — **fallback ke single-connection** saat terdeteksi korupsi.
- `Some chunks matched with start of file! Deleting all, except first.` — deduplikasi
  chunk yang ternyata mengunduh data yang sama.
- `Cannot rebuild downloaded file chunks.` / `Not enough disk space to rebuild
  downloaded chunks.` — fase *rebuild* (menyusun ulang file dari chunk).
- `Chunk order violation error, cInf.ID %ld, startPos %I64d` — validasi urutan.
- `Connection restarted because of timeout (1)/(2)/(3)` — ada 3 tingkat restart.
- `Last packet of this connection is skipped. Complete.` — kasus packet terakhir.

### 4.4 Penyelesaian & penggabungan file
- `ChunksList::JoinFile()` — gabungkan semua chunk jadi file akhir.
- `ChunksListItem::JoinWithNext()` — gabung dua chunk bersebelahan.
- `ChunksList::AppendFile()`, `InsertFromReg()` — persistensi daftar chunk ke registry.
- `ChunksList::EraseRegSubKey()` — bersihkan state setelah selesai.

## 5. Dukungan resume

- `Last-Modified` + `ETag` disimpan → dipakai memvalidasi bahwa file di server
  belum berubah sebelum melanjutkan (validator).
- Untuk FTP: `FTPConnection::SendRest()` / `CheckRestReply()` → perintah `REST`
  sebagai padanan `Range` di FTP.
- Pesan `The data transfer has been interrupted and the server does not support
  "resume". It's only possible to download this file from the beginning.` —
  konfirmasi bahwa IDM mendeteksi ketiadaan resume dan memaksa mulai ulang.
- `HTTPConnection::GetRetrRangeReply()`, `GetTempEndPos()` — menghitung posisi
  akhir sementara dari respons Range.

## 6. Media streaming (untuk Rilis 2)

- `CM3UParser::MediaSegment` → parser **M3U8/HLS**.
- `init_segment`, `segments`, `_Seg%ld-Frag%ld`, `%sSeg%%ld-Frag%%ld` → segmen
  fragmen + init segment (pola DASH/fMP4).
- `SegmentURL`, `Segment-Count: `, `Segment-Durations-Ms: ` → metadata playlist.
- `#ANVATO-SEGMENT-INFO:` → dukungan provider spesifik.
- `chunked` + `Recv transfer encoding chunked, size=0` → dukungan chunked transfer.
- Ada juga `MMSConnection` & `RTMPConnection` (legacy, **tidak** akan kita
  implementasikan).

## 7. Kebijakan koneksi & batas

- String setting: `MaxConnectionsNumber`.
- Peringatan UX: *"...when the number of connections is high, the servers can put
  your computer to a black list..."* → IDM sadar batas per-host itu nyata.
- *"Too many open network connections."* → ada guard global.
- Ada konsep **exceptions per-site** (set 1 koneksi untuk situs tertentu).
- Download limit harian/jadwal (`Download Limits`, scheduler).

## 8. Yang akan SwiftGet tiru vs. tinggalkan

### Ditiru (teknik, ditulis ulang sendiri)
| Teknik IDM | Rencana SwiftGet |
|---|---|
| Range `bytes=start-end` per koneksi | `HttpRangeClient` (Rilis 1) |
| `Accept-Ranges` / `Content-Range` parsing | `HttpRangeClient` (Rilis 1) |
| Validasi `ETag`/`Last-Modified` sebelum resume | field `validators` di manifest (Rilis 1) |
| N koneksi paralel per file | `Scheduler` + semaphore (Rilis 1) |
| Chunk dipindah saat satu koneksi gagal | *reassign* chunk (Rilis 1) |
| Fallback ke 1 koneksi saat korupsi | deteksi duplikasi/mismatch (Rilis 1) |
| `Content-Disposition` untuk nama file | Rilis 1 |
| Parser HLS M3U8 | Rilis 2 |
| Batas koneksi per-host + exceptions | Rilis 2 |

### Ditinggalkan (sengaja)
- FTP, MMS, RTMP — di luar cakupan.
- Registry sebagai storage state — kita pakai **JSON atomik** di folder profil
  (lebih portabel, mudah di-debug, mudah di-backup).
- Protokol internal/browser-integration bawaan — kita pakai endpoint lokal +
  bookmarklet (Rilis 2).

### Catatan penting: dynamic segmentation
IDM memakai *work-stealing* + koneksi opsional dinamis. Sesuai keputusan proyek,
**SwiftGet MVP memakai N segmen statis** (dibagi di awal), sedangkan dynamic
split/gabung masuk Rilis 2. Ini sengaja: jauh lebih mudah diuji dan diverifikasi.

## 9. Prioritas untuk MVP (berdasarkan temuan ini)

1. `Range` + `Accept-Ranges` + `Content-Range` + `ETag`/`Last-Modified` → **wajib**.
2. N segmen statis paralel + penulisan ber-offset → **wajib**.
3. Reassign chunk saat koneksi gagal → **penting** (murah diimplementasikan,
   pengaruh besar ke stabilitas).
4. Deteksi korupsi + fallback single-stream → **penting**.
5. Persistensi manifest atomik → **wajib** (syarat resume).
6. `Content-Disposition` → **wajib** untuk penamaan file.

---

## Sumber bukti (Evidence REA)

| Evidence ID | Operasi |
|---|---|
| `ev_af6cd7b6cd15c31f923d26740731d5a437c396be2523b17eb5164f24fb6da438` | `binary_overview` |
| `ev_34ee411fbe71a8d6f1f0ba8f221374b0ee9aa5130696f9630db7d5a5c2f09018` | `search_strings "bytes="` |
| `ev_af9e1f80c31871d696e3c6aae75e9534b7a8cefe6f7b29c76f5c6602121689dd` | `search_strings "Accept-Ranges"` |
| `ev_03d40e2b333686b3b3490d4889e58f2ca67cb7ce613c5cbfb31d45b0ca1ed58c` | `search_strings "ETag"` |
| `ev_a4f01c404f90ad02016e0214f105e9fc1d9cf1955c8a026978f5ac958f152141` | `search_strings "Last-Modified"` |
| `ev_ab578c28ef1b406ea01906e33b81242a9e3c97d3dfc133ad9cd2006ebf5611f1` | `search_strings "Content-Disposition"` |
| `ev_2ae0f44b0bf08dfbb1f63d588985d56436b7246629c6607495becfc5d1a2218e` | `search_strings "Content-Range"` |
| `ev_b2ed3c15eead2e3b6c818e2464a889d027da89ba33a28b057f45869b635975ea` | `search_strings "seg"` |
| `ev_48c7db4e85953e21b6f483cbd002809887687e3cee96ddec223bd617f6c57847` | `search_strings "Connection"` |
| `ev_a30cfbd297a0c8c01bb9e58888b2e836cd8e4777d134d2eb6fffd8bc0a24f7bf` | `search_strings "chunk"` |
| `ev_dac0d175208b4c552b8f70d1e15110190708a4e88841c78f1a5198c7ef19cfed` | `procedure_info` `FUN_005b2ae0` |
| `ev_bc02781e57d479a31dc5be42e1cee93d0c5feb2a9552cf13ad73572e6b8659d3` | `procedure_pseudo_code` `FUN_005b2ae0` |

**Batas kepercayaan**: analisis ini *statis*. Perilaku runtime (mis. nilai heuristik
untuk berhenti menambah koneksi) tidak dapat dipastikan dari binary dan tidak
diklaim di sini. Yang dipakai sebagai acuan hanyalah teknik yang terbukti dari
string/struktur, dan SwiftGet mengimplementasikannya secara independen berbasis
RFC + pengujian sendiri.
