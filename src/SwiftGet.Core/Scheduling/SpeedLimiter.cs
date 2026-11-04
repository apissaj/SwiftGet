namespace SwiftGet.Core.Scheduling;

/// <summary>
/// Token bucket untuk pembatas kecepatan total. Satu instance dibagi semua
/// sesi lewat <see cref="ConnectionBudget"/>, jadi batasnya untuk seluruh
/// aplikasi — pola Speed Limiter IDM, bukan per unduhan. Limit 0 berarti
/// tanpa batas. Jam diinjeksi supaya bisa diuji tanpa tidur nyata.
/// </summary>
public sealed class SpeedLimiter
{
    private readonly object _gate = new();
    private readonly Func<long> _nowMs;
    private long _limitBps;
    private long _available;
    private long _lastMs;

    public SpeedLimiter(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _lastMs = _nowMs();
    }

    public void SetLimit(long bytesPerSecond)
    {
        lock (_gate)
        {
            _limitBps = Math.Max(0, bytesPerSecond);
            _available = Math.Min(_available, _limitBps);
            _lastMs = _nowMs();
        }
    }

    /// <summary>
    /// Tunggu sampai jatah <paramref name="bytes"/> tersedia. Permintaan yang
    /// lebih besar dari limit tetap dipenuhi sebagai satu burst — jatah
    /// menjadi negatif dan pemanggil berikutnya menunggu lebih lama.
    /// </summary>
    public async Task TungguJatahAsync(long bytes, CancellationToken ct)
    {
        while (true)
        {
            long tungguMs;
            lock (_gate)
            {
                if (_limitBps <= 0) return;

                long now = _nowMs();
                long dt = Math.Max(0, now - _lastMs);
                _lastMs = now;
                _available = Math.Min(_limitBps, _available + _limitBps * dt / 1000);

                long perlu = Math.Min(bytes, _limitBps);
                if (_available >= perlu)
                {
                    _available -= bytes;
                    return;
                }

                tungguMs = Math.Clamp((perlu - _available) * 1000 / _limitBps + 1, 1, 250);
            }

            await Task.Delay((int)tungguMs, ct).ConfigureAwait(false);
        }
    }
}
