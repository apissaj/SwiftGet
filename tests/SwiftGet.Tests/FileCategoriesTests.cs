using SwiftGet.App.Infrastructure;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji klasifikasi kategori unduhan dari ekstensi nama berkas.
/// <para>
/// Semua kasus murni [Theory]/[InlineData]: klasifikasi harus stabil untuk
/// huruf besar/kecil, dan nama tanpa ekstensi yang dikenal selalu jatuh ke
/// Lainnya — bukan melempar pengecualian.
/// </para>
/// </summary>
public sealed class FileCategoriesTests
{
    // ---- Classify: satu teori per kategori, minimal 3 ekstensi representatif ----

    [Theory]
    [InlineData("film.mp4", DownloadCategory.Video)]
    [InlineData("seri.mkv", DownloadCategory.Video)]
    [InlineData("video.avi", DownloadCategory.Video)]
    [InlineData("klip.MOV", DownloadCategory.Video)]      // huruf besar tetap dikenali
    [InlineData("rekaman.webm", DownloadCategory.Video)]
    [InlineData("siaran.ts", DownloadCategory.Video)]
    public void Classify_EkstensiVideo_Menghasilkan_Video(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, FileCategories.Classify(fileName));
    }

    [Theory]
    [InlineData("lagu.mp3", DownloadCategory.Audio)]
    [InlineData("rekaman.FLAC", DownloadCategory.Audio)]  // huruf besar tetap dikenali
    [InlineData("nada.wav", DownloadCategory.Audio)]
    [InlineData("bicara.aAc", DownloadCategory.Audio)]
    [InlineData("podcast.opus", DownloadCategory.Audio)]
    public void Classify_EkstensiAudio_Menghasilkan_Audio(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, FileCategories.Classify(fileName));
    }

    [Theory]
    [InlineData("foto.JPG", DownloadCategory.Gambar)]     // huruf besar tetap dikenali
    [InlineData("gambar.jpeg", DownloadCategory.Gambar)]
    [InlineData("tangkapan.png", DownloadCategory.Gambar)]
    [InlineData("animasi.gif", DownloadCategory.Gambar)]
    [InlineData("Ikon.ico", DownloadCategory.Gambar)]
    public void Classify_EkstensiGambar_Menghasilkan_Gambar(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, FileCategories.Classify(fileName));
    }

    [Theory]
    [InlineData("laporan.pdf", DownloadCategory.Dokumen)]
    [InlineData("anggaran.XLSX", DownloadCategory.Dokumen)] // huruf besar tetap dikenali
    [InlineData("catatan.txt", DownloadCategory.Dokumen)]
    [InlineData("buku.epub", DownloadCategory.Dokumen)]
    [InlineData("data.csv", DownloadCategory.Dokumen)]
    public void Classify_EkstensiDokumen_Menghasilkan_Dokumen(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, FileCategories.Classify(fileName));
    }

    [Theory]
    [InlineData("paket.zip", DownloadCategory.Arsip)]
    [InlineData("cadangan.7Z", DownloadCategory.Arsip)]   // huruf besar tetap dikenali
    [InlineData("sumber.tar", DownloadCategory.Arsip)]
    [InlineData("image.iso", DownloadCategory.Arsip)]
    [InlineData("kompres.Rar", DownloadCategory.Arsip)]
    public void Classify_EkstensiArsip_Menghasilkan_Arsip(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, FileCategories.Classify(fileName));
    }

    [Theory]
    [InlineData("setup.exe", DownloadCategory.Program)]
    [InlineData("instal.msi", DownloadCategory.Program)]
    [InlineData("pasang.MSIX", DownloadCategory.Program)] // huruf besar tetap dikenali
    [InlineData("skrip.ps1", DownloadCategory.Program)]
    [InlineData("aplikasi.apk", DownloadCategory.Program)]
    public void Classify_EkstensiProgram_Menghasilkan_Program(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, FileCategories.Classify(fileName));
    }

    // ---- Classify: ekstensi ganda ----

    /// <summary>
    /// Ekstensi ganda: hanya segmen setelah titik terakhir yang dinilai,
    /// sehingga "arsip.tar.gz" punya ekstensi ".gz" → Arsip.
    /// </summary>
    [Theory]
    [InlineData("arsip.tar.gz", DownloadCategory.Arsip)]   // ekstensi terakhir .gz
    [InlineData("bundel.tar.bz2", DownloadCategory.Arsip)] // ekstensi terakhir .bz2
    [InlineData("berkas.tar.txt", DownloadCategory.Dokumen)] // ekstensi terakhir .txt, bukan tar
    public void Classify_EkstensiGanda_MemakaiSegmenTerakhir(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, FileCategories.Classify(fileName));
    }

    // ---- Classify: tanpa ekstensi / tak dikenal / masukan kosong ----

    [Theory]
    [InlineData("berkas")]        // tanpa titik sama sekali
    [InlineData("berkas.")]       // titik saja di akhir → GetExtension ""
    [InlineData(".gitignore")]    // GetExtension(".gitignore") menghasilkan ""
    [InlineData("berkas.xyz")]    // ekstensi tidak ada dalam pemetaan
    [InlineData(".xyz")]          // GetExtension ".xyz" tapi tidak dikenal
    public void Classify_TanpaEkstensiDikenal_Menghasilkan_Lainnya(string fileName)
    {
        Assert.Equal(DownloadCategory.Lainnya, FileCategories.Classify(fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Classify_MasukanKosong_Menghasilkan_Lainnya(string? fileName)
    {
        Assert.Equal(DownloadCategory.Lainnya, FileCategories.Classify(fileName));
    }

    // ---- Label ----

    [Fact]
    public void Label_SemuaAnggotaEnum_TerisiDanTidakKosong()
    {
        // Label tidak boleh kosong untuk anggota enum mana pun, termasuk
        // anggota yang lupa ditangani switch (harus jatuh ke "Lainnya").
        foreach (DownloadCategory kategori in Enum.GetValues<DownloadCategory>())
        {
            string label = FileCategories.Label(kategori);
            Assert.False(string.IsNullOrWhiteSpace(label), $"Label untuk {kategori} kosong.");
        }
    }

    [Theory]
    [InlineData(DownloadCategory.Video, "Video")]
    [InlineData(DownloadCategory.Audio, "Audio")]
    [InlineData(DownloadCategory.Gambar, "Gambar")]
    [InlineData(DownloadCategory.Dokumen, "Dokumen")]
    [InlineData(DownloadCategory.Arsip, "Arsip")]
    [InlineData(DownloadCategory.Program, "Program")]
    [InlineData(DownloadCategory.Lainnya, "Lainnya")]
    public void Label_KategoriDikenal_MenghasilkanLabelIndonesia(DownloadCategory kategori, string expected)
    {
        Assert.Equal(expected, FileCategories.Label(kategori));
    }

    // ---- LabelOf: gabungan Classify + Label ----

    [Theory]
    [InlineData("film.mp4", "Video")]
    [InlineData("foto.JPG", "Gambar")]
    [InlineData("arsip.tar.gz", "Arsip")]
    [InlineData("setup.exe", "Program")]
    [InlineData("berkas", "Lainnya")]
    [InlineData("berkas.xyz", "Lainnya")]
    [InlineData(null, "Lainnya")]
    [InlineData("", "Lainnya")]
    public void LabelOf_GabunganKlasifikasiDanLabel_Selaras(string? fileName, string expected)
    {
        Assert.Equal(expected, FileCategories.LabelOf(fileName));
    }
}
