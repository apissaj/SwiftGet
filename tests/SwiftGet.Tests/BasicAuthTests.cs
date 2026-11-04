using System.Text;
using SwiftGet.Core.Downloads;
using SwiftGet.Core.Storage;
using SwiftGet.Tests.Fixtures;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji HTTP Basic auth preemptive: kredensial di URL (http://user:sandi@host/)
/// menjadi header Authorization yang dikirim pada setiap permintaan sejak
/// yang pertama, tanpa menunggu tantangan 401 — dan ikut tersimpan di manifest.
/// </summary>
public sealed class BasicAuthTests : IAsyncLifetime
{
    private TestHttpServer _server = null!;
    private string _workDir = null!;

    public Task InitializeAsync()
    {
        _server = new TestHttpServer();
        _workDir = Path.Combine(Path.GetTempPath(), "swiftget-basicauth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private string StateDir => Path.Combine(_workDir, "state");
    private string OutDir => Path.Combine(_workDir, "out");

    /// <summary>URL dengan userinfo di depan host server uji.</summary>
    private string UrlDenganKredensial(string userinfo, string namaBerkas) =>
        $"http://{userinfo}@{_server.BaseUri.Authority}/{namaBerkas}";

    // ---- Unit: Add menerjemahkan userinfo URL menjadi header Basic ----

    [Fact]
    public async Task Add_UserInfoDiURL_MenghasilkanHeaderBasicPreemptive()
    {
        Directory.CreateDirectory(OutDir);
        await using var manager = new DownloadManager(StateDir);

        DownloadTask task = manager.Add(UrlDenganKredensial("user:sandi", "berkas.bin"), OutDir);

        // base64("user:sandi") — nilai eksplisit supaya perubahan format
        // header terdeteksi, bukan sekadar konsisten dengan dirinya.
        Assert.Equal("Basic dXNlcjpzYW5kaQ==", task.BasicAuth);
    }

    [Theory]
    [InlineData("useronly", "useronly:")]             // tanpa sandi -> pasangan kosong di ujung
    [InlineData("user:sandi:ekstra", "user:sandi:ekstra")] // titik dua tambahan ikut sandi
    public async Task Add_VariansUserinfo_DiuraiSesuaiKonvensi(string userinfo, string kredensialAsli)
    {
        Directory.CreateDirectory(OutDir);
        await using var manager = new DownloadManager(StateDir);

        DownloadTask task = manager.Add(UrlDenganKredensial(userinfo, "berkas.bin"), OutDir);

        string diharapkan = "Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(kredensialAsli));
        Assert.Equal(diharapkan, task.BasicAuth);
    }

    [Fact]
    public async Task Add_TanpaUserInfo_BasicAuthTetapNull()
    {
        Directory.CreateDirectory(OutDir);
        await using var manager = new DownloadManager(StateDir);

        DownloadTask task = manager.Add(_server.UrlFor("berkas.bin").ToString(), OutDir);

        Assert.Null(task.BasicAuth);
    }

    [Fact]
    public async Task Add_UserinfoTerEscape_DidekodeSebelumDiEnkode()
    {
        Directory.CreateDirectory(OutDir);
        await using var manager = new DownloadManager(StateDir);

        // %40 adalah '@' — sandi yang mengandung titik dua/@ tetap benar
        // setelah dekode, bukan dibawa mentah-mentah sebagai teks escape.
        DownloadTask task = manager.Add(UrlDenganKredensial("user:p%40ss%21", "berkas.bin"), OutDir);

        string diharapkan = "Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes("user:p@ss!"));
        Assert.Equal(diharapkan, task.BasicAuth);
    }

    // ---- Unit: BasicAuth ikut round-trip manifest ----

    [Fact]
    public async Task BasicAuth_RoundTripManifest_TersimpanDanDipulihkan()
    {
        Directory.CreateDirectory(OutDir);
        await using var manager = new DownloadManager(StateDir);

        DownloadTask task = manager.Add(UrlDenganKredensial("user:sandi", "berkas.bin"), OutDir);
        DownloadManifest manifest = task.ToManifest();
        Assert.Equal("Basic dXNlcjpzYW5kaQ==", manifest.BasicAuth);

        DownloadTask pulih = DownloadTask.FromManifest(manifest);
        Assert.Equal("Basic dXNlcjpzYW5kaQ==", pulih.BasicAuth);
    }

    [Fact]
    public async Task BasicAuth_TanpaKredensial_ManifestTetapNull()
    {
        Directory.CreateDirectory(OutDir);
        await using var manager = new DownloadManager(StateDir);

        DownloadTask task = manager.Add(_server.UrlFor("berkas.bin").ToString(), OutDir);
        Assert.Null(task.ToManifest().BasicAuth);
        Assert.Null(DownloadTask.FromManifest(task.ToManifest()).BasicAuth);
    }

    // ---- Integrasi: server mewajibkan Basic auth ----

    [Fact]
    public async Task UnduhanDenganKredensial_Selesai_AuthPreemptiveSejakPermintaanPertama()
    {
        _server.Content = BuatPayload(256 * 1024);
        _server.RequireBasicAuth = "user:sandi";
        Directory.CreateDirectory(OutDir);

        await using var manager = new DownloadManager(StateDir);
        DownloadTask task = manager.Add(UrlDenganKredensial("user:sandi", "terlindung.bin"), OutDir);
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Completed, task.State);

        // Server mewajibkan auth pada semua permintaan; unduhan selesai hanya
        // mungkin bila permintaan pertama (probe) sudah membawa header.
        Assert.Equal("Basic dXNlcjpzYW5kaQ==", _server.LastAuthorization);

        byte[] actual = await File.ReadAllBytesAsync(task.FinalPath);
        Assert.Equal(_server.Content, actual);
    }

    [Fact]
    public async Task UnduhanDenganSandiSalah_Gagal_LastErrorMenyebut401()
    {
        _server.Content = BuatPayload(16 * 1024);
        _server.RequireBasicAuth = "user:sandi";
        Directory.CreateDirectory(OutDir);

        await using var manager = new DownloadManager(StateDir);
        DownloadTask task = manager.Add(UrlDenganKredensial("user:salah", "terlindung.bin"), OutDir);
        await manager.StartAsync(task.Id);

        Assert.Equal(DownloadState.Failed, task.State);
        Assert.Contains("401", task.LastError);

        // Kredensial yang salah memang dikirim (preemptive), bukan kosong.
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:salah")),
            _server.LastAuthorization);
    }

    private static byte[] BuatPayload(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i * 31 + (i >> 8));
        return data;
    }
}
