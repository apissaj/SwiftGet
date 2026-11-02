namespace SwiftGet.Core.Downloads.Media;

public enum MediaPlaylistKind
{
    /// <summary>Daftar varian kualitas (master .m3u8 atau MPD dengan beberapa Representation).</summary>
    Master,

    /// <summary>Daftar segmen media siap diunduh.</summary>
    Media,
}

/// <summary>Satu pilihan kualitas di playlist master.</summary>
public sealed class MediaVariant
{
    public MediaVariant(Uri url, long bandwidth)
    {
        Url = url;
        Bandwidth = bandwidth;
    }

    public Uri Url { get; }

    /// <summary>Bitrate yang dijanjikan server (bps); 0 bila tidak disebut.</summary>
    public long Bandwidth { get; }
}

/// <summary>Satu segmen media (biasanya beberapa detik video) yang diunduh lalu digabung.</summary>
public sealed class MediaSegmentEntry
{
    public MediaSegmentEntry(Uri url, double? durationSeconds = null)
    {
        Url = url;
        DurationSeconds = durationSeconds;
    }

    public Uri Url { get; }

    public double? DurationSeconds { get; }
}

/// <summary>
/// Hasil penguraian playlist: daftar segmen berurutan (media) atau daftar
/// varian (master). URL segmen selalu absolut.
/// </summary>
public sealed class MediaPlaylist
{
    public required MediaPlaylistKind Kind { get; init; }

    public required Uri BaseUri { get; init; }

    public List<MediaVariant> Variants { get; } = [];

    public List<MediaSegmentEntry> Segments { get; } = [];
}

/// <summary>Deteksi playlist dari URL, tanpa I/O.</summary>
public static class MediaPlaylistDetector
{
    private static readonly string[] PlaylistExtensions = [".m3u8", ".mpd"];

    /// <summary>URL berakhiran ekstensi playlist (query dan fragment diabaikan).</summary>
    public static bool IsPlaylistUrl(Uri url)
    {
        string path = url.IsAbsoluteUri ? url.AbsolutePath : url.ToString();
        string ext = Path.GetExtension(path);
        return PlaylistExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }
}
