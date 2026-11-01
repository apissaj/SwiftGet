namespace SwiftGet.Core.Downloads;

/// <summary>
/// Cuplikan progres yang aman dikirim ke UI. Nilainya adalah snapshot sesaat,
/// bukan referensi hidup ke objek yang sedang dimutasi worker.
/// </summary>
public sealed class DownloadProgress
{
    public required string Id { get; init; }
    public required DownloadState State { get; init; }
    public required long BytesDownloaded { get; init; }
    public required long? TotalBytes { get; init; }

    /// <summary>Kecepatan rata-rata jendela terakhir, byte per detik.</summary>
    public required double BytesPerSecond { get; init; }

    /// <summary>Jumlah segmen yang sudah selesai.</summary>
    public required int CompletedSegments { get; init; }

    public required int TotalSegments { get; init; }

    public double Fraction => TotalBytes is > 0
        ? Math.Clamp((double)BytesDownloaded / TotalBytes.Value, 0d, 1d)
        : 0d;

    /// <summary>Perkiraan sisa waktu; <c>null</c> bila belum bisa dihitung.</summary>
    public TimeSpan? Eta
    {
        get
        {
            if (TotalBytes is not > 0 || BytesPerSecond <= 1) return null;
            double remaining = TotalBytes.Value - BytesDownloaded;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }
}
