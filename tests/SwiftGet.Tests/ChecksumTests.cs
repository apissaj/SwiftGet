using SwiftGet.Core.Storage;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji unit untuk pemeriksa integritas: parsing header Digest dan perhitungan
/// hash berkas. Nilai hash "abc" adalah vektor uji standar (NIST/FIPS).
/// </summary>
public sealed class ChecksumTests : IDisposable
{
    private readonly string _dir;

    public ChecksumTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "swiftget-checksum-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteTempFile(string content = "abc")
    {
        string path = Path.Combine(_dir, $"input-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, content);
        return path;
    }

    // ---- ParseDigestHeader ----

    [Fact]
    public void ParseDigestHeader_Menormalisasi_Nama_Algoritma()
    {
        // sha-256 -> sha256, sha-512 -> sha512, sha/sha-1 -> sha1.
        var hasil = Checksum.ParseDigestHeader("sha-256=AAAA, sha-512=BBBB, sha=CCCC");

        Assert.Equal(3, hasil.Count);
        Assert.Equal(("sha256", "AAAA"), (hasil[0].Algorithm, hasil[0].ValueBase64));
        Assert.Equal(("sha512", "BBBB"), (hasil[1].Algorithm, hasil[1].ValueBase64));
        Assert.Equal(("sha1", "CCCC"), (hasil[2].Algorithm, hasil[2].ValueBase64));
    }

    [Fact]
    public void ParseDigestHeader_Membaca_Beberapa_Pasangan()
    {
        var hasil = Checksum.ParseDigestHeader("md5=abc=, sha256=xyz=");

        Assert.Equal(2, hasil.Count);
        Assert.Equal("md5", hasil[0].Algorithm);
        Assert.Equal("abc=", hasil[0].ValueBase64);
        Assert.Equal("sha256", hasil[1].Algorithm);
        Assert.Equal("xyz=", hasil[1].ValueBase64);
    }

    [Fact]
    public void ParseDigestHeader_Membuang_Kutip()
    {
        var hasil = Checksum.ParseDigestHeader("sha256=\"AAAA\"");

        var satu = Assert.Single(hasil);
        Assert.Equal("sha256", satu.Algorithm);
        Assert.Equal("AAAA", satu.ValueBase64);
    }

    [Fact]
    public void ParseDigestHeader_Mengabaikan_Algoritma_Tak_Dikenal()
    {
        // unixsum dan crc32c tidak didukung; unixsum juga tidak punya '='
        // pada formatnya sehingga dilewati, crc32c=... sengaja diberi nilai.
        var hasil = Checksum.ParseDigestHeader("unixsum=42, crc32c=AAAA, sha256=BBBB");

        var satu = Assert.Single(hasil);
        Assert.Equal("sha256", satu.Algorithm);
        Assert.Equal("BBBB", satu.ValueBase64);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDigestHeader_Nilai_Kosong_Menghasilkan_Daftar_Kosong(string? input)
    {
        Assert.Empty(Checksum.ParseDigestHeader(input));
    }

    // ---- ComputeAsync ----

    [Fact]
    public async Task ComputeAsync_Sha256_Sesuai_Vektor_Uji()
    {
        string path = WriteTempFile();

        string? hash = await Checksum.ComputeAsync(path, "sha256");

        // Hash SHA-256 standar dari string "abc".
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Fact]
    public async Task ComputeAsync_Md5_Sesuai_Vektor_Uji()
    {
        string path = WriteTempFile();

        string? hash = await Checksum.ComputeAsync(path, "md5");

        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", hash);
    }

    [Fact]
    public async Task ComputeAsync_Sha1_Sesuai_Vektor_Uji()
    {
        string path = WriteTempFile();

        string? hash = await Checksum.ComputeAsync(path, "sha1");

        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", hash);
    }

    [Fact]
    public async Task ComputeAsync_Nama_Algoritma_Uppercase_Tetap_Berjalan()
    {
        string path = WriteTempFile();

        string? hash = await Checksum.ComputeAsync(path, "SHA256");

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Fact]
    public async Task ComputeAsync_Algoritma_Tak_Dikenal_Mengembalikan_Null()
    {
        string path = WriteTempFile();

        string? hash = await Checksum.ComputeAsync(path, "crc32c");

        Assert.Null(hash);
    }
}
