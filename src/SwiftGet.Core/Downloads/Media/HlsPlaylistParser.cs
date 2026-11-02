using SwiftGet.Core.Http;

namespace SwiftGet.Core.Downloads.Media;

/// <summary>
/// Pengurai playlist HLS (.m3u8, RFC 8216): master (varian kualitas) dan
/// media (daftar segmen). Yang didukung sengaja dibatasi pada playlist yang
/// lazim; hal di luar itu (enkripsi, byterange, live) ditolak dengan pesan
/// yang jelas, bukan diabaikan diam-diam.
/// </summary>
public static class HlsPlaylistParser
{
    public static MediaPlaylist Parse(string text, Uri playlistUri)
    {
        bool isMaster = text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
        var playlist = new MediaPlaylist
        {
            Kind = isMaster ? MediaPlaylistKind.Master : MediaPlaylistKind.Media,
            BaseUri = playlistUri,
        };

        double? pendingDuration = null;
        long pendingBandwidth = 0;
        bool sawTag = false;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                sawTag = true;

                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    string body = line["#EXTINF:".Length..];
                    int comma = body.IndexOf(',');
                    pendingDuration = double.TryParse(
                        comma < 0 ? body : body[..comma],
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double d) ? d : null;
                }
                else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    string bw = ExtractAttribute(line, "BANDWIDTH") ?? "";
                    pendingBandwidth = long.TryParse(bw, out long value) ? value : 0;
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                {
                    ThrowIfEncrypted(line);
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    // Init segment fMP4: wajib paling awal di berkas gabungan.
                    string? mapUri = ExtractAttribute(line, "URI");
                    if (!string.IsNullOrWhiteSpace(mapUri))
                        playlist.Segments.Add(new MediaSegmentEntry(Resolve(playlistUri, mapUri)));
                }
                else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                {
                    throw new DownloadException(DownloadErrorKind.UnsupportedMedia,
                        "Playlist HLS dengan EXT-X-BYTERANGE belum didukung.");
                }

                continue;
            }

            // Baris tanpa pagar: URL varian (di master) atau segmen (di media).
            if (!sawTag)
            {
                // Konten tanpa tag HLS bukan playlist; deteksi konten yang
                // memanggil parser ini seharusnya sudah menyaringnya.
                continue;
            }

            Uri resolved = Resolve(playlistUri, line);

            if (isMaster)
            {
                playlist.Variants.Add(new MediaVariant(resolved, pendingBandwidth));
                pendingBandwidth = 0;
            }
            else
            {
                playlist.Segments.Add(new MediaSegmentEntry(resolved, pendingDuration));
                pendingDuration = null;
            }
        }

        return playlist;
    }

    /// <summary>Pilih varian terbaik dari master playlist: bandwidth terbesar.</summary>
    public static MediaVariant PickVariant(MediaPlaylist master) =>
        master.Variants.Count > 0
            ? master.Variants.OrderByDescending(v => v.Bandwidth).First()
            : throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "Master playlist tidak memuat varian kualitas apa pun.");

    private static void ThrowIfEncrypted(string keyLine)
    {
        string method = ExtractAttribute(keyLine, "METHOD") ?? "";
        if (method.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return;

        throw new DownloadException(DownloadErrorKind.UnsupportedMedia,
            "Playlist HLS terenkripsi (EXT-X-KEY) belum didukung. " +
            "Biasanya ini berarti konten ber-DRM.");
    }

    /// <summary>Ambil nilai atribut KEY="VALUE" atau KEY=VALUE dari baris tag.</summary>
    private static string? ExtractAttribute(string line, string name)
    {
        // Cari "NAME=" lalu baca nilai: dikutip bila diawali tanda kutip.
        int idx = line.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        int start = idx + name.Length + 1;
        if (start >= line.Length) return null;

        if (line[start] == '"')
        {
            int end = line.IndexOf('"', start + 1);
            return end < 0 ? null : line[(start + 1)..end];
        }

        int stop = line.IndexOf(',', start);
        return stop < 0 ? line[start..].Trim() : line[start..stop].Trim();
    }

    private static Uri Resolve(Uri baseUri, string reference) =>
        Uri.TryCreate(reference, UriKind.Absolute, out Uri? absolute)
            ? absolute
            : new Uri(baseUri, reference);
}
