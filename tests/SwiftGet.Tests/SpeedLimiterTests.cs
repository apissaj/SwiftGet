using SwiftGet.Core.Downloads;
using SwiftGet.Core.Scheduling;
using SwiftGet.Tests.Fixtures;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji token bucket pembatas kecepatan total. Jam diinjeksi, jadi pengisian
/// jatah diuji tanpa tidur nyata; satu-satunya tidur nyata adalah jeda antar
/// iterasi di dalam TungguJatahAsync (maks. 250 ms per cek ulang).
/// </summary>
public sealed class SpeedLimiterTests : IAsyncLifetime, IDisposable
{
    private TestHttpServer _server = null!;
    private string _workDir = null!;

    public Task InitializeAsync()
    {
        _server = new TestHttpServer();
        _workDir = Path.Combine(Path.GetTempPath(), "swiftget-limiter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private string StateDir => Path.Combine(_workDir, "state");
    private string OutDir => Path.Combine(_workDir, "out");

    /// <summary>Jam milidetik tiruan yang hanya bergerak bila digerakkan uji.</summary>
    private sealed class JamTiruan
    {
        private long _ms;
        public long Sekarang() => Volatile.Read(ref _ms);
        public void Maju(long ms) => Interlocked.Add(ref _ms, ms);
    }

    // ---- Limit 0: tanpa batas ----

    [Fact]
    public async Task LimitNol_MengembalikanLangsung_TanpaMenunggu()
    {
        var limiter = new SpeedLimiter(); // limit default 0 (belum diset)

        // Permintaan raksasa pun harus selesai sinkron — tidak ada jatah yang
        // perlu dikumpulkan ketika tidak ada batas.
        Task tugas = limiter.TungguJatahAsync(1_000_000, CancellationToken.None);
        Assert.True(tugas.IsCompleted, "Limit 0 harus langsung lolos tanpa menunggu.");
        await tugas;
    }

    [Fact]
    public async Task SetLimitNol_MelepasSemuaTunggu()
    {
        var jam = new JamTiruan();
        var limiter = new SpeedLimiter(jam.Sekarang);
        limiter.SetLimit(1000);
        limiter.SetLimit(0); // pengguna mematikan batas di tengah jalan

        Task tugas = limiter.TungguJatahAsync(999_999, CancellationToken.None);
        Assert.True(tugas.IsCompleted);
        await tugas;
    }

    // ---- Pengisian jatah dari waktu (bucket) ----

    [Fact]
    public async Task JatahTerisiDariWaktu_ReqKecilLangsung_ReqBerikutnyaMenunggu()
    {
        var jam = new JamTiruan();
        var limiter = new SpeedLimiter(jam.Sekarang);
        limiter.SetLimit(1000); // 1000 Bps
        jam.Maju(600);          // bucket memuat 600 byte pada cek berikutnya

        // Permintaan pertama cukup dari jatah yang terkumpul: langsung.
        Task pertama = limiter.TungguJatahAsync(500, CancellationToken.None);
        Assert.True(pertama.IsCompleted, "500 byte dari jatah 600 harus langsung lolos.");
        await pertama;

        // Sisa jatah 100 — permintaan 500 tidak bisa lolos sebelum waktu berjalan.
        Task kedua = limiter.TungguJatahAsync(500, CancellationToken.None);
        Assert.False(kedua.IsCompleted, "Jatah tinggal 100; permintaan 500 harus menunggu.");

        // Tahanan ekstra: waktu nyata berjalan, jam tiruan tidak — tetap menunggu.
        var yangSelesai = await Task.WhenAny(kedua, Task.Delay(200));
        Assert.NotSame(kedua, yangSelesai);

        // Maju 600 ms memberi tambahan 600 byte: 100 + 600 = 700 >= 500.
        jam.Maju(600);
        yangSelesai = await Task.WhenAny(kedua, Task.Delay(3000));
        Assert.Same(kedua, yangSelesai);
        await kedua;
    }

    // ---- Burst: permintaan lebih besar dari limit ----

    [Fact]
    public async Task PermintaanLebihBesarDariLimit_SatuBurst_JatahMenjadiNegatif()
    {
        var jam = new JamTiruan();
        var limiter = new SpeedLimiter(jam.Sekarang);
        limiter.SetLimit(1000);
        jam.Maju(1000); // jatah 1000 tersedia

        // 5000 dengan limit 1000: dipenuhi sebagai satu burst, jatah -4000.
        Task burst = limiter.TungguJatahAsync(5000, CancellationToken.None);
        Assert.True(burst.IsCompleted, "Permintaan > limit harus dipenuhi sekaligus sebagai burst.");
        await burst;

        // Pemanggil berikutnya menanggung utang 4000 byte: butuh 4,5 detik
        // waktu tiruan sebelum jatah 500 terkumpul kembali.
        Task lanjutan = limiter.TungguJatahAsync(500, CancellationToken.None);
        Assert.False(lanjutan.IsCompleted);

        jam.Maju(600); // jatah: -4000 + 600 = -3400 — belum cukup
        await Task.Delay(300); // beri kesempatan satu cek ulang (jeda riil 250 ms)
        Assert.False(lanjutan.IsCompleted, "Utang 4000 byte belum lunas pada 600 ms.");

        jam.Maju(3900); // total 4500 ms sejak burst: jatah tepat 500
        var yangSelesai = await Task.WhenAny(lanjutan, Task.Delay(3000));
        Assert.Same(lanjutan, yangSelesai);
        await lanjutan;
    }

    // ---- SetLimit memotong jatah yang menumpuk ----

    [Fact]
    public async Task SetLimit_MenurunkanJatahYangSudahMenumpuk()
    {
        var jam = new JamTiruan();
        var limiter = new SpeedLimiter(jam.Sekarang);
        limiter.SetLimit(1000);
        jam.Maju(1000);

        // Ambil 100 dari jatah 1000: sisa 900.
        Task kecil = limiter.TungguJatahAsync(100, CancellationToken.None);
        Assert.True(kecil.IsCompleted);
        await kecil;

        // Turunkan limit ke 500: jatah 900 terpotong menjadi 500 (cap).
        limiter.SetLimit(500);

        // Permintaan 600 (perlu = min(600, 500) = 500) masih lolos dengan
        // jatah terpotong, tetapi membuatnya negatif (-100).
        Task besar = limiter.TungguJatahAsync(600, CancellationToken.None);
        Assert.True(besar.IsCompleted, "Jatah terpotong 500 cukup untuk permintaan perlu 500.");
        await besar;

        // Bila jatah tidak terpotong (masih 900), permintaan 50 berikutnya
        // pasti langsung lolos. Menunggu justru bukti cap sudah bekerja.
        Task sisa = limiter.TungguJatahAsync(50, CancellationToken.None);
        Assert.False(sisa.IsCompleted, "Jatah harusnya -100 setelah cap; permintaan 50 harus menunggu.");

        jam.Maju(300); // -100 + 150 = 50 — tepat cukup
        var yangSelesai = await Task.WhenAny(sisa, Task.Delay(3000));
        Assert.Same(sisa, yangSelesai);
        await sisa;
    }

    // ---- Integrasi ringan: unduhan dengan limit tetap selesai ----

    [Fact]
    public async Task UnduhanDenganBatasKecepatan_TetapSelesai()
    {
        // Batas 1024 Bps menunda potongan pertama sekitar satu detik; limiter
        // hanya boleh memperlambat, tidak boleh menggagalkan atau menggantung.
        _server.Content = BuatPayload(4 * 1024);
        Directory.CreateDirectory(OutDir);

        await using var manager = new DownloadManager(StateDir,
            new SchedulerOptions { MaxConnectionsPerDownload = 2 });
        manager.SetSpeedLimit(1024);

        DownloadTask task = manager.Add(_server.UrlFor("kecil.bin").ToString(), OutDir);
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Completed, task.State);
        byte[] actual = await File.ReadAllBytesAsync(task.FinalPath);
        Assert.Equal(_server.Content, actual);
    }

    private static byte[] BuatPayload(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i * 31 + (i >> 8));
        return data;
    }
}
