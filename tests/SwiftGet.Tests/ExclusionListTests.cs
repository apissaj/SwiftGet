using SwiftGet.Core.Downloads;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji daftar pengecualian URL (pola ala IDM): berkas satu-polanya-per-baris,
/// wildcard * dan ?, abai huruf besar/kecil, baris kosong dan komentar
/// dilewati, dan berkas yang tidak ada tidak boleh menggagalkan aplikasi.
/// </summary>
public sealed class ExclusionListTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "swiftget-tests", "exclusions", Guid.NewGuid().ToString("N"));

    private string Berkas(string nama) => Path.Combine(_dir, nama);

    private ExclusionList Buat(string nama, params string[] isi)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Berkas(nama), isi);
        return new ExclusionList(Berkas(nama));
    }

    private void Tulis(string nama, params string[] isi)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Berkas(nama), isi);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- WriteSeed ----

    [Fact]
    public void WriteSeed_BerkasBelumAda_MembuatBerkasContoh()
    {
        string path = Berkas("exclusions.txt");
        ExclusionList.WriteSeed(path);

        Assert.True(File.Exists(path));
        string[] baris = File.ReadAllLines(path);
        Assert.NotEmpty(baris);
        Assert.StartsWith("# Daftar pengecualian SwiftGet", baris[0]);
    }

    [Fact]
    public void WriteSeed_BerkasSudahAda_TidakMenimpa()
    {
        Tulis("exclusions.txt", "http://milik-pengguna.com/*");
        string path = Berkas("exclusions.txt");

        ExclusionList.WriteSeed(path);

        // Isi editan pengguna harus tetap utuh — seed hanya untuk berkas baru.
        Assert.Equal(new[] { "http://milik-pengguna.com/*" }, File.ReadAllLines(path));
    }

    // ---- Wildcard * ----

    [Fact]
    public void IsExcluded_WildcardBintang_CocokDenganSubdomainDanPath()
    {
        ExclusionList daftar = Buat("e.txt", "http://*.contoh.com/*.wav");

        Assert.True(daftar.IsExcluded("http://cdn.contoh.com/lagu/jernih.wav"));
        Assert.True(daftar.IsExcluded("http://a.b.contoh.com/x/y.wav")); // * bisa berlapis
    }

    [Fact]
    public void IsExcluded_WildcardBintang_TidakCocokDenganURLDiLuarPola()
    {
        ExclusionList daftar = Buat("e.txt", "http://*.contoh.com/*.wav");

        // Tanpa subdomain: * butuh paling tidak satu karakter sebelum titik.
        Assert.False(daftar.IsExcluded("http://contoh.com/lagu/jernih.wav"));
        // Skema beda: pola dijangkar penuh termasuk skema.
        Assert.False(daftar.IsExcluded("https://cdn.contoh.com/lagu/jernih.wav"));
        // Ekstensi beda.
        Assert.False(daftar.IsExcluded("http://cdn.contoh.com/lagu/jernih.mp3"));
        // URL lain sama sekali.
        Assert.False(daftar.IsExcluded("http://contoh.org/a.wav"));
    }

    // ---- Wildcard ? ----

    [Theory]
    [InlineData("http://contoh.com/file1.zip", true)]
    [InlineData("http://contoh.com/filea.zip", true)]
    [InlineData("http://contoh.com/file12.zip", false)] // ? hanya satu karakter
    [InlineData("http://contoh.com/file.zip", false)]   // ? tidak boleh kosong
    public void IsExcluded_TandaTanya_MewakiliTepatSatuKarakter(string url, bool diharapkan)
    {
        ExclusionList daftar = Buat("e.txt", "http://contoh.com/file?.zip");

        Assert.Equal(diharapkan, daftar.IsExcluded(url));
    }

    // ---- Huruf besar/kecil ----

    [Fact]
    public void IsExcluded_TidakPekaHurufBesarKecil()
    {
        ExclusionList daftar = Buat("e.txt", "HTTP://CONTOH.COM/iklan/*.exe");

        Assert.True(daftar.IsExcluded("http://contoh.com/iklan/PASANG.EXE"));
        Assert.True(daftar.IsExcluded("Http://Contoh.Com/iklan/pasang.exe"));
    }

    // ---- Baris kosong dan komentar ----

    [Fact]
    public void Patterns_BarisKosongDanKomentar_DilewatiDanUrut()
    {
        ExclusionList daftar = Buat("e.txt",
            "# komentar pembuka",
            "",
            "   ",
            "http://pertama.com/*",
            "# komentar tengah",
            "\t",
            "http://kedua.com/*");

        // Hanya pola sungguhan yang tersimpan, urut seperti di berkas.
        Assert.Equal(
            new[] { "http://pertama.com/*", "http://kedua.com/*" },
            daftar.Patterns);
    }

    // ---- Berkas tidak ada ----

    [Fact]
    public void BerkasTidakAda_DaftarKosong_TanpaMelempar()
    {
        string path = Berkas("tidak-ada.txt");
        var daftar = new ExclusionList(path);

        Assert.Empty(daftar.Patterns);
        Assert.False(daftar.IsExcluded("http://contoh.com/apa-pun.wav"));
    }

    // ---- Reload ----

    [Fact]
    public void Reload_MembacaUlangBerkasYangSudahBerubah()
    {
        Tulis("e.txt", "http://lama.com/*");
        string path = Berkas("e.txt");
        var daftar = new ExclusionList(path);
        Assert.True(daftar.IsExcluded("http://lama.com/a.zip"));

        File.WriteAllLines(path, new[] { "# daftar baru", "http://baru.com/*" });

        // Tanpa Reload, daftar lama masih berlaku (pemuatan tidak otomatis).
        Assert.True(daftar.IsExcluded("http://lama.com/a.zip"));
        Assert.False(daftar.IsExcluded("http://baru.com/a.zip"));

        daftar.Reload();

        Assert.False(daftar.IsExcluded("http://lama.com/a.zip"));
        Assert.True(daftar.IsExcluded("http://baru.com/a.zip"));
        Assert.Equal(new[] { "http://baru.com/*" }, daftar.Patterns);
    }

    [Fact]
    public void Reload_BerkasHilang_DaftarMenjadiKosong()
    {
        ExclusionList daftar = Buat("e.txt", "http://contoh.com/*");
        Assert.True(daftar.IsExcluded("http://contoh.com/a.zip"));

        File.Delete(Berkas("e.txt"));
        daftar.Reload();

        Assert.Empty(daftar.Patterns);
        Assert.False(daftar.IsExcluded("http://contoh.com/a.zip"));
    }
}
