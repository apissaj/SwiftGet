using System.Collections.Concurrent;
using System.Diagnostics;
using SwiftGet.Core;
using SwiftGet.Core.Downloads;
using SwiftGet.Core.Downloads.Media;
using SwiftGet.Core.Scheduling;
using SwiftGet.Core.Storage;
using SwiftGet.Tests.Fixtures;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji integrasi unduhan playlist media (HLS dan DASH) lewat DownloadManager:
/// penggabungan segmen berurutan, pilihan varian, percobaan ulang segmen,
/// pause/resume berbasis index segmen, penolakan playlist terenkripsi, progres,
/// dan unduhan DASH dua jalur (video + audio) yang digabung lewat ffmpeg.
/// </summary>
public sealed class MediaDownloadTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private string _workDir = null!;
    private readonly List<DownloadManager> _managers = new();

    public Task InitializeAsync()
    {
        _server = new TestHttpServer();
        _workDir = Path.Combine(Path.GetTempPath(), "swiftget-media-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(OutDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (DownloadManager manager in _managers)
            await manager.DisposeAsync();

        await _server.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private string StateDir => Path.Combine(_workDir, "state");
    private string OutDir => Path.Combine(_workDir, "out");

    private DownloadManager BuatManager(int maxConnectionsPerDownload, string? ffmpegPath = null)
    {
        var manager = new DownloadManager(StateDir, new SchedulerOptions
        {
            MaxConnectionsPerDownload = maxConnectionsPerDownload,
            MaxConcurrentDownloads = 1,
            FfmpegPath = ffmpegPath,
        });
        _managers.Add(manager);
        return manager;
    }

    /// <summary>Isi segmen deterministik; indeks berbeda menghasilkan byte berbeda.</summary>
    private static byte[] Isi(int index, int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(index * 53 + i);
        return data;
    }

    private static byte[] Gabung(params byte[][] bagian) =>
        bagian.SelectMany(b => b).ToArray();

    private static string M3u8Media(IReadOnlyList<string> namaSegmen)
    {
        var baris = new List<string> { "#EXTM3U", "#EXT-X-TARGETDURATION:4" };
        foreach (string nama in namaSegmen)
        {
            baris.Add("#EXTINF:4.0,");
            baris.Add(nama);
        }
        return string.Join("\n", baris) + "\n";
    }

    /// <summary>Daftarkan segmen ke routing dan kembalikan isinya, berurutan.</summary>
    private byte[][] RouteSegmen(string folder, int jumlah, int indeksAwal = 1)
    {
        var isi = new byte[jumlah][];
        for (int i = 0; i < jumlah; i++)
        {
            isi[i] = Isi(indeksAwal + i, 200 + 40 * i);
            _server.Route($"/{folder}/seg-{i + 1}.ts", isi[i]);
        }
        return isi;
    }

    private DownloadTask TambahUnduhan(DownloadManager manager, string pathUrl) =>
        manager.Add(_server.UrlFor(pathUrl).ToString(), OutDir);

    // ---------- 1. Penggabungan segmen berurutan ----------

    [Fact]
    public async Task Unduh_M3u8_Media_Menggabungkan_Segmen_Berurutan()
    {
        _server.RouteText("/video/index.m3u8", M3u8Media(["seg-1.ts", "seg-2.ts", "seg-3.ts"]));
        byte[][] segmen = RouteSegmen("video", 3);

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 4);
        DownloadTask task = TambahUnduhan(manager, "video/index.m3u8");
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.True(task.IsMedia);
        Assert.Equal(3, task.MediaSegmentCount);

        // Nama berkas berubah dari playlist (.m3u8) ke ekstensi segmen (.ts).
        Assert.Equal("index.ts", task.FileName);
        string hasil = Path.Combine(OutDir, task.FileName);
        Assert.True(File.Exists(hasil), $"Berkas hasil tidak ada: {hasil}");
        Assert.Equal(Gabung(segmen), await File.ReadAllBytesAsync(hasil));

        // Tiap segmen diunduh tepat sekali.
        for (int i = 1; i <= 3; i++)
            Assert.Equal(1, _server.GetRequestCountFor($"/video/seg-{i}.ts"));

        // Manifest dan berkas sementara dibersihkan setelah selesai.
        var store = new ManifestStore(StateDir);
        Assert.False(File.Exists(store.GetManifestPath(task.Id)));
        Assert.False(File.Exists(store.GetPartPath(task.Id)));
    }

    // ---------- 2. Pemilihan varian pada master playlist ----------

    [Fact]
    public async Task Master_Playlist_Mengunduh_Varian_Bandwidth_Terbesar_Saja()
    {
        _server.RouteText("/video/master.m3u8",
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000
            480p/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2500000
            1080p/index.m3u8
            """);
        _server.RouteText("/video/480p/index.m3u8", M3u8Media(["seg-1.ts", "seg-2.ts", "seg-3.ts"]));
        _server.RouteText("/video/1080p/index.m3u8", M3u8Media(["seg-1.ts", "seg-2.ts", "seg-3.ts"]));
        RouteSegmen("video/480p", 3, indeksAwal: 1);
        byte[][] varianBesar = RouteSegmen("video/1080p", 3, indeksAwal: 11);

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 4);
        DownloadTask task = TambahUnduhan(manager, "video/master.m3u8");
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal("index.ts", task.FileName);
        Assert.Equal(Gabung(varianBesar), await File.ReadAllBytesAsync(Path.Combine(OutDir, task.FileName)));

        // Varian kecil sama sekali tidak disentuh: playlist maupun segmennya.
        Assert.Equal(0, _server.GetRequestCountFor("/video/480p/index.m3u8"));
        for (int i = 1; i <= 3; i++)
            Assert.Equal(0, _server.GetRequestCountFor($"/video/480p/seg-{i}.ts"));

        // Varian besar diunduh masing-masing sekali.
        for (int i = 1; i <= 3; i++)
            Assert.Equal(1, _server.GetRequestCountFor($"/video/1080p/seg-{i}.ts"));
    }

    // ---------- 3. Percobaan ulang segmen yang gagal ----------

    [Fact]
    public async Task Segmen_Yang_Putus_Sekali_Dicoba_Ulang_Sampai_Sukses()
    {
        _server.RouteText("/video/index.m3u8", M3u8Media(["seg-1.ts", "seg-2.ts", "seg-3.ts"]));
        byte[][] segmen = RouteSegmen("video", 3);
        _server.FailFirstPerPath["/video/seg-2.ts"] = 1;

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 2);
        DownloadTask task = TambahUnduhan(manager, "video/index.m3u8");
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal(Gabung(segmen), await File.ReadAllBytesAsync(Path.Combine(OutDir, task.FileName)));

        // Bukti percobaan ulang: segmen ke-2 diminta dua kali (gagal lalu sukses).
        Assert.Equal(2, _server.GetRequestCountFor("/video/seg-2.ts"));
        Assert.Equal(1, _server.GetRequestCountFor("/video/seg-1.ts"));
        Assert.Equal(1, _server.GetRequestCountFor("/video/seg-3.ts"));
    }

    // ---------- 4. Pause di tengah lalu resume per segmen ----------

    [Fact]
    public async Task Pause_Di_Tengah_Lalu_Resume_Tidak_Mengunduh_Ulang_Segmen_Awal()
    {
        _server.RouteText("/video/index.m3u8",
            M3u8Media(["seg-1.ts", "seg-2.ts", "seg-3.ts", "seg-4.ts", "seg-5.ts"]));
        byte[][] segmen = RouteSegmen("video", 5);

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 1);
        DownloadTask task = TambahUnduhan(manager, "video/index.m3u8");

        // Satu koneksi saja supaya penulisan berurutan dan titik pause deterministik.
        // Jeda pada progres segmen pertama memberi waktu worker menuntaskan semua
        // antrean unduh, jadi saat pause tidak ada pengambilan segmen yang menggantung.
        int jedaDilakukan = 0;
        int pauseDiminta = 0;
        manager.ProgressChanged += p =>
        {
            if (p.CompletedSegments == 1 && Interlocked.Exchange(ref jedaDilakukan, 1) == 0)
                Thread.Sleep(300);

            if (p.CompletedSegments >= 2 && Interlocked.Exchange(ref pauseDiminta, 1) == 0)
                manager.Pause(task.Id);
        };

        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Paused, task.State);
        Assert.Equal(2, task.MediaResumeIndex);
        Assert.Equal(5, task.MediaSegmentCount);

        int[] hitungSebelumPause = Enumerable.Range(1, 5)
            .Select(i => _server.GetRequestCountFor($"/video/seg-{i}.ts"))
            .ToArray();

        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal(5, task.MediaResumeIndex);
        Assert.Equal(Gabung(segmen), await File.ReadAllBytesAsync(Path.Combine(OutDir, task.FileName)));

        // Segmen yang sudah tertulis (1-2) tidak diminta ulang saat resume.
        Assert.Equal(hitungSebelumPause[0], _server.GetRequestCountFor("/video/seg-1.ts"));
        Assert.Equal(hitungSebelumPause[1], _server.GetRequestCountFor("/video/seg-2.ts"));

        // Sisa segmen (3-5) baru diambil pada fase resume.
        for (int i = 3; i <= 5; i++)
            Assert.True(
                _server.GetRequestCountFor($"/video/seg-{i}.ts") > hitungSebelumPause[i - 1],
                $"Segmen {i} seharusnya baru diunduh saat resume.");
    }

    // ---------- 5. Playlist terenkripsi ----------

    [Fact]
    public async Task Playlist_Terenkripsi_Menghasilkan_Failed_Tanpa_Berkas_Akhir()
    {
        _server.RouteText("/video/index.m3u8",
            """
            #EXTM3U
            #EXT-X-TARGETDURATION:4
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0X9C7DB8778570D05C3177C349FD9236AA
            #EXTINF:4.0,
            seg-1.ts
            """);
        RouteSegmen("video", 1);

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 2);
        DownloadTask task = TambahUnduhan(manager, "video/index.m3u8");
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Failed, task.State);
        Assert.NotNull(task.LastError);
        Assert.Contains("terenkripsi", task.LastError);
        Assert.False(File.Exists(task.FinalPath),
            "Playlist yang tidak bisa diunduh tidak boleh meninggalkan berkas akhir.");
    }

    // ---------- 6. URL playlist berisi sesuatu yang lain ----------

    [Fact]
    public async Task Url_M3u8_Dengan_Isi_Bukan_Playlist_Menghasilkan_Failed()
    {
        _server.RouteText("/video/index.m3u8", "ini hanya berkas teks biasa.", "text/plain");

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 2);
        DownloadTask task = TambahUnduhan(manager, "video/index.m3u8");
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Failed, task.State);
        Assert.NotNull(task.LastError);
        Assert.Contains("playlist", task.LastError);
        Assert.False(File.Exists(task.FinalPath));
    }

    // ---------- 7. Progres sesi media ----------

    [Fact]
    public async Task Progres_Media_Melaporkan_TotalBytes_Null_Dan_Segmen_Naik()
    {
        _server.RouteText("/video/index.m3u8", M3u8Media(["seg-1.ts", "seg-2.ts", "seg-3.ts"]));
        RouteSegmen("video", 3);

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 2);
        var progres = new ConcurrentQueue<DownloadProgress>();
        manager.ProgressChanged += progres.Enqueue;

        DownloadTask task = TambahUnduhan(manager, "video/index.m3u8");
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.NotEmpty(progres);

        List<DownloadProgress> snapshot = progres.ToList();

        // Playlist tidak menjanjikan ukuran total di muka.
        Assert.All(snapshot, p => Assert.Null(p.TotalBytes));
        Assert.All(snapshot, p => Assert.True(p.BytesPerSecond >= 0));

        // Jumlah segmen dilaporkan, dan penulisan segmen naik satu per satu.
        Assert.Contains(snapshot, p => p.TotalSegments == 3);
        Assert.Equal(new[] { 1, 2, 3 },
            snapshot.Where(p => p.CompletedSegments > 0)
                .Select(p => p.CompletedSegments)
                .Distinct()
                .OrderBy(n => n)
                .ToArray());
    }

    // ---------- 8. Referer halaman asal ----------

    [Fact]
    public async Task Referer_Diteruskan_Pada_Playlist_Dan_Segmen()
    {
        _server.RouteText("/video/index.m3u8", M3u8Media(["seg-1.ts", "seg-2.ts", "seg-3.ts"]));
        byte[][] segmen = RouteSegmen("video", 3);

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 2);
        string referer = _server.UrlFor("halaman/video").ToString();
        DownloadTask task = manager.Add(_server.UrlFor("video/index.m3u8").ToString(), OutDir, referer: referer);
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.Equal(Gabung(segmen), await File.ReadAllBytesAsync(Path.Combine(OutDir, task.FileName)));

        // Segmen diambil paralel, jadi assertion referer dilakukan per path,
        // bukan pada permintaan terakhir global.
        lock (_server.LastRefererPerPath)
        {
            Assert.Equal(referer, _server.LastRefererPerPath["/video/index.m3u8"]);
            for (int i = 1; i <= 3; i++)
                Assert.Equal(referer, _server.LastRefererPerPath[$"/video/seg-{i}.ts"]);
        }
    }

    // ---------- 9. DASH dua jalur (video + audio) ----------

    private static readonly string[] NamaSegmenDash =
        ["video-init.m4s", "video-1.m4s", "video-2.m4s", "audio-init.m4s", "audio-1.m4s", "audio-2.m4s"];

    private static string MpdDuaJalur() =>
        """
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
          <Period>
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="2000000" mimeType="video/mp4">
                <SegmentList timescale="1" duration="1">
                  <Initialization sourceURL="video-init.m4s"/>
                  <SegmentURL media="video-1.m4s"/>
                  <SegmentURL media="video-2.m4s"/>
                </SegmentList>
              </Representation>
            </AdaptationSet>
            <AdaptationSet mimeType="audio/mp4">
              <Representation id="a" bandwidth="128000" mimeType="audio/mp4">
                <SegmentList timescale="1" duration="1">
                  <Initialization sourceURL="audio-init.m4s"/>
                  <SegmentURL media="audio-1.m4s"/>
                  <SegmentURL media="audio-2.m4s"/>
                </SegmentList>
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    private void RouteDashDuaJalur(byte[] videoInit, byte[] video1, byte[] video2,
        byte[] audioInit, byte[] audio1, byte[] audio2)
    {
        _server.RouteText("/dash/manifest.mpd", MpdDuaJalur());
        _server.Route("/dash/video-init.m4s", videoInit);
        _server.Route("/dash/video-1.m4s", video1);
        _server.Route("/dash/video-2.m4s", video2);
        _server.Route("/dash/audio-init.m4s", audioInit);
        _server.Route("/dash/audio-1.m4s", audio1);
        _server.Route("/dash/audio-2.m4s", audio2);
    }

    /// <summary>
    /// Segmen DASH asli (init + dua fragmen) hasil ffmpeg. Mux -c copy hanya
    /// menerima data media yang valid, jadi uji penggabungan tidak bisa memakai
    /// byte pola; encoder mpeg4/aac dipilih karena ada di setiap build ffmpeg.
    /// </summary>
    private static async Task<Dictionary<string, byte[]>> HasilkanSegmenDash(
        string ffmpegPath, string direktori, string sumber, string encoder)
    {
        Directory.CreateDirectory(direktori);
        // Muxer dash memperlakukan output sebagai URL; backslash membuatnya
        // gagal mengurai folder manifest sehingga segmen ditulis di CWD.
        string tujuan = Path.Combine(direktori, "manifest.mpd").Replace('\\', '/');
        var start = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -loglevel error -f lavfi -i {sumber} {encoder} " +
                        "-f dash -seg_duration 1 -window_size 5 " +
                        "-init_seg_name init.m4s -media_seg_name seg-$Number$.m4s " +
                        $"\"{tujuan}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using Process proses = Process.Start(start)
            ?? throw new InvalidOperationException("ffmpeg tidak bisa dijalankan.");
        string stderr = await proses.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proses.WaitForExitAsync().ConfigureAwait(false);
        Assert.True(proses.ExitCode == 0, $"ffmpeg gagal membuat segmen uji ({sumber}):\n{stderr}");

        return new Dictionary<string, byte[]>
        {
            ["init.m4s"] = await File.ReadAllBytesAsync(Path.Combine(direktori, "init.m4s")),
            ["seg-1.m4s"] = await File.ReadAllBytesAsync(Path.Combine(direktori, "seg-1.m4s")),
            ["seg-2.m4s"] = await File.ReadAllBytesAsync(Path.Combine(direktori, "seg-2.m4s")),
        };
    }

    [Fact]
    public async Task Dash_MultiTrack_Tanpa_Ffmpeg_Gagal_Sebelum_Mengunduh()
    {
        _server.RouteText("/dash/manifest.mpd", MpdDuaJalur());
        foreach (string nama in NamaSegmenDash)
            _server.Route($"/dash/{nama}", Isi(1, 64));

        // Lokasi ffmpeg sengaja menunjuk ke tempat yang pasti kosong: salinan
        // pengguna bersifat mutlak, tidak boleh jatuh menebak ke PATH.
        string ffmpegHilang = Path.Combine(
            Path.GetTempPath(), "swiftget-tidak-ada", Guid.NewGuid().ToString("N"), "ffmpeg.exe");

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 2, ffmpegPath: ffmpegHilang);
        DownloadTask task = TambahUnduhan(manager, "dash/manifest.mpd");
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Failed, task.State);
        Assert.NotNull(task.LastError);
        Assert.Contains("ffmpeg", task.LastError);

        // Gagal lebih awal: MPD dibaca, tetapi tidak ada satu byte segmen pun
        // yang diunduh dan tidak ada berkas akhir yang tertinggal.
        Assert.Equal(1, _server.GetRequestCountFor("/dash/manifest.mpd"));
        foreach (string nama in NamaSegmenDash)
            Assert.Equal(0, _server.GetRequestCountFor($"/dash/{nama}"));
        Assert.False(File.Exists(task.FinalPath));
    }

    [Fact]
    public async Task Dash_MultiTrack_Mux_Sukses_Dengan_Ffmpeg_Sungguhan()
    {
        // Tanpa ffmpeg di lingkungan (mis. CI) penggabungan tidak bisa dibuktikan.
        string? ffmpeg = FfmpegLocator.Find(null);
        if (ffmpeg is null) return;

        string gen = Path.Combine(_workDir, "dashgen");
        Dictionary<string, byte[]> video = await HasilkanSegmenDash(ffmpeg, Path.Combine(gen, "video"),
            "testsrc=duration=2:size=128x128:rate=10", "-c:v mpeg4 -q:v 6");
        Dictionary<string, byte[]> audio = await HasilkanSegmenDash(ffmpeg, Path.Combine(gen, "audio"),
            "sine=frequency=440:duration=2", "-c:a aac -b:a 24k");
        RouteDashDuaJalur(video["init.m4s"], video["seg-1.m4s"], video["seg-2.m4s"],
            audio["init.m4s"], audio["seg-1.m4s"], audio["seg-2.m4s"]);

        var progres = new ConcurrentQueue<DownloadProgress>();
        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 2);
        manager.ProgressChanged += progres.Enqueue;

        DownloadTask task = TambahUnduhan(manager, "dash/manifest.mpd");
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Completed, task.State);
        Assert.True(task.IsMedia);

        // Nama hasil dari segmen video pertama; .m4s diganti .mp4.
        Assert.Equal("manifest.mp4", task.FileName);
        string hasil = Path.Combine(OutDir, task.FileName);
        Assert.True(File.Exists(hasil), $"Berkas hasil tidak ada: {hasil}");
        Assert.True(new FileInfo(hasil).Length > 0);

        // Kedua part dan manifest dibersihkan setelah mux sukses.
        var store = new ManifestStore(StateDir);
        Assert.False(File.Exists(store.GetPartPath(task.Id)));
        Assert.False(File.Exists(store.GetPartPath(task.Id) + ".audio"));
        Assert.False(File.Exists(store.GetManifestPath(task.Id)));

        // MPD dan tiap segmen (init + 2 media per jalur) diminta tepat satu kali.
        Assert.Equal(1, _server.GetRequestCountFor("/dash/manifest.mpd"));
        foreach (string nama in NamaSegmenDash)
            Assert.Equal(1, _server.GetRequestCountFor($"/dash/{nama}"));

        // Total segmen progres = jumlah entri playlist video + audio
        // (init ikut dihitung: 3 + 3).
        List<DownloadProgress> snapshot = progres.ToList();
        Assert.Contains(snapshot, p => p.TotalSegments == 6);
        Assert.Equal(6, snapshot.Last().TotalSegments);
        Assert.Equal(6, snapshot.Last().CompletedSegments);
    }

    [Fact]
    public async Task Dash_MultiTrack_Resume_Dari_MediaTracksState()
    {
        // Resume juga berakhir di mux, jadi butuh ffmpeg dan segmen asli.
        string? ffmpeg = FfmpegLocator.Find(null);
        if (ffmpeg is null) return;

        string gen = Path.Combine(_workDir, "dashgen");
        Dictionary<string, byte[]> video = await HasilkanSegmenDash(ffmpeg, Path.Combine(gen, "video"),
            "testsrc=duration=2:size=128x128:rate=10", "-c:v mpeg4 -q:v 6");
        Dictionary<string, byte[]> audio = await HasilkanSegmenDash(ffmpeg, Path.Combine(gen, "audio"),
            "sine=frequency=440:duration=2", "-c:a aac -b:a 24k");
        RouteDashDuaJalur(video["init.m4s"], video["seg-1.m4s"], video["seg-2.m4s"],
            audio["init.m4s"], audio["seg-1.m4s"], audio["seg-2.m4s"]);

        // Keadaan setelah sesi terputus: video tuntas, audio baru init + satu
        // segmen. Part diisi sesuai itu, seperti yang ditulis sesi aslinya.
        var store = new ManifestStore(StateDir);
        string id = "dashresumemulti";
        var awal = new DownloadTask(id, _server.UrlFor("dash/manifest.mpd"), OutDir)
        {
            IsMedia = true,
            MediaSegmentCount = 3,
            MediaTracksState = "3:3,2:3",
        };
        await store.SaveAsync(awal.ToManifest());
        File.WriteAllBytes(store.GetPartPath(id),
            Gabung(video["init.m4s"], video["seg-1.m4s"], video["seg-2.m4s"]));
        File.WriteAllBytes(store.GetPartPath(id) + ".audio",
            Gabung(audio["init.m4s"], audio["seg-1.m4s"]));

        DownloadManager manager = BuatManager(maxConnectionsPerDownload: 2);
        await manager.RestoreAsync();
        DownloadTask? task = manager.Get(id);
        Assert.NotNull(task);
        Assert.Equal(DownloadState.Paused, task!.State);
        Assert.Equal("3:3,2:3", task.MediaTracksState);

        await manager.StartAsync(id);

        Assert.Equal(DownloadState.Completed, task.State);
        string hasil = Path.Combine(OutDir, task.FileName);
        Assert.True(File.Exists(hasil), $"Berkas hasil tidak ada: {hasil}");
        Assert.True(new FileInfo(hasil).Length > 0);

        // Jalur video sudah tuntas: tidak satu pun URL video diminta ulang.
        Assert.Equal(0, _server.GetRequestCountFor("/dash/video-init.m4s"));
        Assert.Equal(0, _server.GetRequestCountFor("/dash/video-1.m4s"));
        Assert.Equal(0, _server.GetRequestCountFor("/dash/video-2.m4s"));

        // Audio: yang sudah tertulis tidak diulang, sisanya diambil sekali.
        Assert.Equal(0, _server.GetRequestCountFor("/dash/audio-init.m4s"));
        Assert.Equal(0, _server.GetRequestCountFor("/dash/audio-1.m4s"));
        Assert.Equal(1, _server.GetRequestCountFor("/dash/audio-2.m4s"));
        Assert.Equal(1, _server.GetRequestCountFor("/dash/manifest.mpd"));
    }

    [Fact]
    public void MediaTracksState_RoundTrip_Melalui_Manifest()
    {
        var task = new DownloadTask("roundtrip01", new Uri("http://test.local/dash/manifest.mpd"),
            Path.GetTempPath())
        {
            IsMedia = true,
            MediaTracksState = "2:2,1:2",
        };

        DownloadManifest manifest = task.ToManifest();
        Assert.Equal("2:2,1:2", manifest.MediaTracksState);

        DownloadTask restored = DownloadTask.FromManifest(manifest);
        Assert.Equal("2:2,1:2", restored.MediaTracksState);
        Assert.True(restored.IsMedia);
    }
}
