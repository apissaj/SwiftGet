namespace SwiftGet.Core.Downloads;

/// <summary>
/// Kebijakan percobaan ulang yang dipakai saat satu segmen gagal.
/// <para>
/// Backoff eksponensial dengan jitter: tanpa jitter, N segmen yang gagal
/// bersamaan akan mencoba ulang bersamaan pula dan menghujani server dengan
/// gelombang request yang identik.
/// </para>
/// </summary>
public sealed class RetryPolicy
{
    public int MaxAttempts { get; init; } = 5;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Batas atas faktor jitter acak relatif (0.3 = plus/minus 30 persen).</summary>
    public double JitterFactor { get; init; } = 0.3;

    private readonly Random _random = new();

    /// <summary>Hitung jeda untuk percobaan ke-<paramref name="attempt"/> (1-based).</summary>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt < 1) attempt = 1;

        double exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        double capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);

        double jitter;
        lock (_random)
        {
            jitter = (1 - JitterFactor) + (_random.NextDouble() * 2 * JitterFactor);
        }

        return TimeSpan.FromMilliseconds(Math.Max(50, capped * jitter));
    }

    /// <summary>
    /// Menentukan apakah kesalahan layak dicoba ulang. Kesalahan permanen
    /// (403, disk penuh, cancel) tidak diulang — mengulang hanya membuang waktu.
    /// </summary>
    public static bool IsTransient(DownloadException ex) => ex.Kind switch
    {
        DownloadErrorKind.ConnectionFailed => true,
        DownloadErrorKind.CorruptResponse => true,
        DownloadErrorKind.HttpStatus => true, // 5xx/429 ditangani pemanggil lewat pesan
        DownloadErrorKind.DiskFull => false,
        DownloadErrorKind.AccessDenied => false,
        DownloadErrorKind.InvalidUrl => false,
        DownloadErrorKind.ContentChanged => false,
        DownloadErrorKind.Canceled => false,
        _ => false,
    };
}
