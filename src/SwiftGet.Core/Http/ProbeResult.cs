namespace SwiftGet.Core.Downloads;

/// <summary>
/// Ringkasan hasil pemeriksaan URL (probe) sebelum unduhan dimulai.
/// Semua nilai adalah hasil pengamatan langsung dari respons server, bukan tebakan.
/// </summary>
public sealed class ProbeResult
{
    public required Uri FinalUri { get; init; }

    /// <summary>Panjang konten total dalam byte; <c>null</c> bila server tidak menyatakannya.</summary>
    public long? ContentLength { get; init; }

    /// <summary>
    /// True bila server menyatakan dukungan Range (lewat <c>Accept-Ranges: bytes</c>
    /// atau dengan menjawab permintaan Range memakai status 206).
    /// </summary>
    public bool SupportsRanges { get; init; }

    /// <summary>Validator entitas, bila dikirim server. Dipakai untuk memvalidasi resume.</summary>
    public string? ETag { get; init; }

    /// <summary>Nilai header <c>Last-Modified</c> mentah, bila ada.</summary>
    public string? LastModifiedRaw { get; init; }

    /// <summary>Nama file yang disarankan server lewat <c>Content-Disposition</c>.</summary>
    public string? SuggestedFileName { get; init; }

    /// <summary>Nilai <c>Content-Type</c> akhir.</summary>
    public string? ContentType { get; init; }

    /// <summary>True bila nama file berasal dari <c>Content-Disposition</c>, bukan dari URL.</summary>
    public bool FileNameFromServer => !string.IsNullOrWhiteSpace(SuggestedFileName);

    /// <summary>True bila unduhan bisa dipecah menjadi beberapa segmen.
    /// Butuh panjang konten yang diketahui dan server yang menghormati Range.
    /// </summary>
    public bool CanSegment => SupportsRanges && ContentLength is > 0;

    /// <summary>Algoritma checksum dari server (sha256/md5), jika ada.</summary>
    public string? ExpectedChecksumAlgorithm { get; init; }

    /// <summary>Nilai checksum dalam lowercase hex, jika ada.</summary>
    public string? ExpectedChecksumValue { get; init; }

    public override string ToString() =>
        $"len={ContentLength?.ToString() ?? "?"} ranges={SupportsRanges} etag={ETag ?? "-"}";

    /// <summary>
    /// Salinan hasil probe dengan nama file pengganti. Dipakai saat respons probe
    /// (206) tidak membawa <c>Content-Disposition</c> tetapi respons sebelumnya membawanya.
    /// </summary>
    public ProbeResult WithFallbackFileName(string fileName) => new()
    {
        FinalUri = FinalUri,
        ContentLength = ContentLength,
        SupportsRanges = SupportsRanges,
        ETag = ETag,
        LastModifiedRaw = LastModifiedRaw,
        SuggestedFileName = fileName,
        ContentType = ContentType,
        ExpectedChecksumAlgorithm = ExpectedChecksumAlgorithm,
        ExpectedChecksumValue = ExpectedChecksumValue,
    };
}
