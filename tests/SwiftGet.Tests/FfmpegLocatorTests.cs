using SwiftGet.Core;
using SwiftGet.Core.Downloads.Media;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji pencari lokasi ffmpeg: lokasi yang diatur pengguna bersifat mutlak,
/// lalu folder aplikasi, lalu PATH berurutan — dan kegagalan penggabungan
/// jalur muncul sebagai galat unduhan, bukan galat proses.
/// </summary>
public sealed class FfmpegLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "swiftget-ffmpeg-tests", Guid.NewGuid().ToString("N"));

    public FfmpegLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string BuatDirektori(string nama, bool denganFfmpeg = false)
    {
        string dir = Path.Combine(_root, nama);
        Directory.CreateDirectory(dir);
        if (denganFfmpeg) File.WriteAllBytes(Path.Combine(dir, "ffmpeg.exe"), Array.Empty<byte>());
        return dir;
    }

    [Fact]
    public void ConfiguredPath_Ada_DikembalikanPersis()
    {
        string configured = Path.Combine(BuatDirektori("cfg", denganFfmpeg: true), "ffmpeg.exe");

        string? hasil = FfmpegLocator.Find(configured, BuatDirektori("base"), BuatDirektori("path"));

        Assert.Equal(configured, hasil);
    }

    [Fact]
    public void ConfiguredPath_TidakAda_MengembalikanNull_Tanpa_Menebak_PATH()
    {
        // Lokasi pengguna mutlak: meski PATH memuat ffmpeg lain, salah lokasi
        // tetap null supaya tidak diam-diam memakai versi yang berbeda.
        string pathBerisiFfmpeg = BuatDirektori("path-ada", denganFfmpeg: true);

        string? hasil = FfmpegLocator.Find(
            Path.Combine(_root, "tidak-ada", "ffmpeg.exe"),
            BuatDirektori("base"),
            pathBerisiFfmpeg);

        Assert.Null(hasil);
    }

    [Fact]
    public void BaseDirectory_Dicari()
    {
        string baseDir = BuatDirektori("base-ada", denganFfmpeg: true);

        string? hasil = FfmpegLocator.Find(null, baseDir, pathEnv: "");

        Assert.Equal(Path.Combine(baseDir, "ffmpeg.exe"), hasil);
    }

    [Fact]
    public void PATH_Dirinci_Berurutan()
    {
        string dirKosong = BuatDirektori("path-kosong");
        string dirBerisi = BuatDirektori("path-berisi", denganFfmpeg: true);

        string? hasil = FfmpegLocator.Find(
            null, BuatDirektori("base-kosong"), $"{dirKosong};{dirBerisi}");

        Assert.Equal(Path.Combine(dirBerisi, "ffmpeg.exe"), hasil);
    }

    [Fact]
    public void PATH_Tanpa_Ffmpeg_MengembalikanNull()
    {
        string? hasil = FfmpegLocator.Find(
            null, BuatDirektori("base-kosong"), BuatDirektori("path-seluwer"));

        Assert.Null(hasil);
    }

    [Fact]
    public async Task Mux_InputTidakAda_Menghasilkan_CorruptResponse()
    {
        // Jalur gagal dibuktikan dengan ffmpeg sungguhan dan input yang hilang;
        // ffmpeg.exe palsu bahkan tidak bisa dijalankan Windows (Win32Exception).
        string? ffmpeg = FfmpegLocator.Find(null);
        if (ffmpeg is null) return; // lingkungan tanpa ffmpeg: kegagalan proses tidak bisa disimulasikan

        DownloadException ex = await Assert.ThrowsAsync<DownloadException>(() =>
            FfmpegLocator.MuxAsync(ffmpeg,
                Path.Combine(_root, "video-tidak-ada.m4s"),
                Path.Combine(_root, "audio-tidak-ada.m4s"),
                Path.Combine(_root, "hasil.mp4")));

        Assert.Equal(DownloadErrorKind.CorruptResponse, ex.Kind);
        Assert.Contains("ffmpeg gagal menggabungkan jalur", ex.Message);
    }
}
