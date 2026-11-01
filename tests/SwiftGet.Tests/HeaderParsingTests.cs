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
