using SwiftGet.Core.Http;
using Xunit;

namespace SwiftGet.Tests;

public sealed class HeaderParsingTests
{
    [Theory]
    [InlineData("bytes 0-499/1234", 0L, 500L, 1234L)]
    [InlineData("bytes 500-1233/1234", 500L, 1234L, 1234L)]
    [InlineData("bytes 100-199/*", 100L, 200L, null)]
    [InlineData("BYTES 0-0/10", 0L, 1L, 10L)]
    public void ParseContentRange_Membaca_Rentang_Dan_Total(
        string input, long expectedStart, long expectedEndExclusive, long? expectedTotal)
    {
        var span = HeaderParsing.ParseContentRange(input);

        Assert.NotNull(span);
        Assert.Equal(expectedStart, span!.Value.Start);
        Assert.Equal(expectedEndExclusive, span.Value.End);
        Assert.Equal(expectedTotal, span.Value.TotalLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bytes")]
    [InlineData("bytes 0-499")]
    [InlineData("items 0-499/1234")]
    [InlineData("bytes 499-0/1234")]
    public void ParseContentRange_Menolak_Format_Rusak(string? input)
    {
        Assert.Null(HeaderParsing.ParseContentRange(input));
    }

    [Theory]
    [InlineData("attachment; filename=\"report.pdf\"", "report.pdf")]
    [InlineData("attachment;filename=simple.zip", "simple.zip")]
    [InlineData("inline; filename=my file.txt", "my file.txt")]
    public void ParseContentDisposition_Membaca_Nama_File(string header, string expected)
    {
        Assert.Equal(expected, HeaderParsing.ParseContentDispositionFileName(header));
    }

    [Fact]
    public void ParseContentDisposition_Menghormati_Rfc5987()
    {
        // "laporan tahunan.pdf" dengan karakter non-ASCII ter-encode persen.
        string header = "attachment; filename=\"fallback.pdf\"; filename*=UTF-8''laporan%20tahunan.pdf";

        Assert.Equal("laporan tahunan.pdf", HeaderParsing.ParseContentDispositionFileName(header));
    }

    [Fact]
    public void ParseContentDisposition_Memprioritaskan_Filename_Bintang()
    {
        string header = "attachment; filename=\"ascii.pdf\"; filename*=UTF-8''%C3%A9t%C3%A9.pdf";

        Assert.Equal("été.pdf", HeaderParsing.ParseContentDispositionFileName(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("attachment")]
    public void ParseContentDisposition_Mengembalikan_Null_Bila_Tidak_Ada_Nama(string? header)
    {
        Assert.Null(HeaderParsing.ParseContentDispositionFileName(header));
    }

    [Theory]
    [InlineData("bytes", true)]
    [InlineData("bytes, bytes", true)]
    [InlineData("none", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void AcceptsByteRanges_Mengenali_Dukungan(string? header, bool expected)
    {
        Assert.Equal(expected, HeaderParsing.AcceptsByteRanges(header));
    }

    [Fact]
    public void Sanitize_Membuang_Karakter_Ilegal()
    {
        string input = "na:me?with*illegal\"chars.txt";
        string result = HeaderParsing.Sanitize(input);

        foreach (char c in Path.GetInvalidFileNameChars())
            Assert.DoesNotContain(c, result);

        Assert.EndsWith(".txt", result);
    }
}

/// <summary>
/// Uji ekstraksi checksum dari header respons: Digest, X-Checksum-*,
/// X-Goog-Hash, dan Content-MD5 beserta urutan prioritasnya.
/// </summary>
public sealed class ExtractChecksumTests
{
    private static (string? Algorithm, string? ValueHex) Extract(
        params (string Name, string Value)[] headers)
    {
        var dict = headers.ToDictionary(h => h.Name, h => h.Value);
        return HeaderParsing.ExtractChecksum(h => dict.GetValueOrDefault(h));
    }

    [Fact]
    public void Digest_Sha256_Menang_Atas_Md5()
    {
        // SHA-256 dari "abc" dalam base64.
        string sha256B64 = Convert.ToBase64String(
            Convert.FromHexString("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        string md5B64 = Convert.ToBase64String(
            Convert.FromHexString("900150983cd24fb0d6963f7d28e17f72"));

        var (algo, hex) = Extract(("Digest", $"md5={md5B64}, sha-256={sha256B64}"));

        Assert.Equal("sha256", algo);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hex);
    }

    [Fact]
    public void Digest_Sha512_Dipilih_Diatas_Sha256()
    {
        string sha512B64 = Convert.ToBase64String(new byte[64] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
            11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
            31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50,
            51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64 });
        string sha256B64 = Convert.ToBase64String(new byte[32] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
            10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31 });

        var (algo, hex) = Extract(("Digest", $"sha-256={sha256B64}, sha-512={sha512B64}"));

        Assert.Equal("sha512", algo);
        Assert.Equal("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40", hex);
    }

    [Fact]
    public void XChecksumSha256_Hex_Valid_Diterima()
    {
        string hex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        var (algo, value) = Extract(("X-Checksum-SHA256", hex));

        Assert.Equal("sha256", algo);
        Assert.Equal(hex, value);
    }

    [Fact]
    public void Hex_64_Karakter_Tapi_Bukan_Hex_Ditolak()
    {
        // Panjang tepat 64, tetapi mengandung karakter di luar 0-9a-f.
        string notHex = new string('g', 64);

        var (algo, value) = Extract(("X-Checksum-SHA256", notHex));

        Assert.Null(algo);
        Assert.Null(value);
    }

    [Fact]
    public void XChecksumSha1_Diterima()
    {
        string hex = "a9993e364706816aba3e25717850c26c9cd0d89d";

        var (algo, value) = Extract(("X-File-SHA1", hex));

        Assert.Equal("sha1", algo);
        Assert.Equal(hex, value);
    }

    [Fact]
    public void XChecksumMd5_Diterima()
    {
        string hex = "900150983cd24fb0d6963f7d28e17f72";

        var (algo, value) = Extract(("X-Checksum-MD5", hex));

        Assert.Equal("md5", algo);
        Assert.Equal(hex, value);
    }

    [Fact]
    public void XGoogHash_Md5_Dipakai_Dan_Crc32c_Diabaikan()
    {
        string md5B64 = Convert.ToBase64String(
            Convert.FromHexString("900150983cd24fb0d6963f7d28e17f72"));

        var (algo, value) = Extract(("X-Goog-Hash", $"crc32c=AAAAAA==, md5={md5B64}"));

        Assert.Equal("md5", algo);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", value);
    }

    [Fact]
    public void XGoogHash_Tanpa_Md5_Melanjutkan_Ke_ContentMd5()
    {
        string md5B64 = Convert.ToBase64String(
            Convert.FromHexString("900150983cd24fb0d6963f7d28e17f72"));

        var (algo, value) = Extract(
            ("X-Goog-Hash", "crc32c=AAAAAA=="),
            ("Content-MD5", md5B64));

        Assert.Equal("md5", algo);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", value);
    }

    [Fact]
    public void ContentMd5_Base64_Dikonversi_Ke_Hex()
    {
        string md5B64 = Convert.ToBase64String(
            Convert.FromHexString("900150983cd24fb0d6963f7d28e17f72"));

        var (algo, value) = Extract(("Content-MD5", md5B64));

        Assert.Equal("md5", algo);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", value);
    }

    [Fact]
    public void Tanpa_Header_Apakapun_Mengembalikan_Null()
    {
        var dict = new Dictionary<string, string>();

        var (algo, value) = HeaderParsing.ExtractChecksum(h => dict.GetValueOrDefault(h));

        Assert.Null(algo);
        Assert.Null(value);
    }

    [Fact]
    public void Prioritas_Digest_Atas_XChecksum_Atas_XGoogHash_Atas_ContentMd5()
    {
        string sha256B64 = Convert.ToBase64String(
            Convert.FromHexString("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        string md5B64 = Convert.ToBase64String(
            Convert.FromHexString("900150983cd24fb0d6963f7d28e17f72"));
        const string hexSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        // Digest hadir: harus menang atas semua kandidat lain.
        var (algo1, _) = Extract(
            ("Digest", $"sha-256={sha256B64}"),
            ("X-Checksum-SHA256", hexSha256),
            ("X-Goog-Hash", $"md5={md5B64}"),
            ("Content-MD5", md5B64));
        Assert.Equal("sha256", algo1);

        // Tanpa Digest: X-Checksum-SHA256 menang atas X-Goog-Hash dan Content-MD5.
        var (algo2, _) = Extract(
            ("X-Checksum-SHA256", hexSha256),
            ("X-Goog-Hash", $"md5={md5B64}"),
            ("Content-MD5", md5B64));
        Assert.Equal("sha256", algo2);

        // Tanpa Digest dan X-Checksum-*: X-Goog-Hash menang atas Content-MD5.
        var (algo3, _) = Extract(
            ("X-Goog-Hash", $"md5={md5B64}"),
            ("Content-MD5", md5B64));
        Assert.Equal("md5", algo3);
    }
}
