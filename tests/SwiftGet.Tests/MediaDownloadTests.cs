using System.Collections.Concurrent;
using SwiftGet.Core;
using SwiftGet.Core.Downloads;
using SwiftGet.Core.Scheduling;
using SwiftGet.Core.Storage;
using SwiftGet.Tests.Fixtures;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji integrasi unduhan playlist media (HLS) lewat DownloadManager: penggabungan
/// segmen berurutan, pilihan varian, percobaan ulang segmen, pause/resume berbasis
/// index segmen, penolakan playlist terenkripsi, dan progres.
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

    private DownloadManager BuatManager(int maxConnectionsPerDownload)
    {
        var manager = new DownloadManager(StateDir, new SchedulerOptions
        {
            MaxConnectionsPerDownload = maxConnectionsPerDownload,
            MaxConcurrentDownloads = 1,
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
}
