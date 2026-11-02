using SwiftGet.Core;
using SwiftGet.Core.Downloads.Media;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji pengurai playlist HLS tanpa I/O: master (varian), media (segmen),
/// init segment fMP4, penolakan enkripsi/byterange, dan toleransi line ending.
/// </summary>
public class HlsParserTests
{
    private static readonly Uri BaseUri = new("http://cdn.test/live/index.m3u8");

    [Theory]
    [InlineData("http://cdn.test/v/index.m3u8", true)]
    [InlineData("http://cdn.test/v/index.M3U8", true)]
    [InlineData("http://cdn.test/v/stream.mpd", true)]
    [InlineData("http://cdn.test/v/Stream.MpD", true)]
    [InlineData("http://cdn.test/v/index.m3u8?token=abc&w=1", true)]
    [InlineData("http://cdn.test/v/file.zip", false)]
    [InlineData("http://cdn.test/v/playlist?fallback=.m3u8", false)]
    public void IsPlaylistUrl_Mengikuti_Ekstensi_Path(string url, bool harap)
    {
        Assert.Equal(harap, MediaPlaylistDetector.IsPlaylistUrl(new Uri(url)));
    }

    [Fact]
    public void Master_Playlist_Memuat_Varian_Dengan_Resolve_Relatif()
    {
        MediaPlaylist playlist = HlsPlaylistParser.Parse(
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH="800000",RESOLUTION=640x360
            360p/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH="2400000",RESOLUTION=1920x1080
            http://mirror.cdn.test/hd/index.m3u8
            """,
            BaseUri);

        Assert.Equal(MediaPlaylistKind.Master, playlist.Kind);
        Assert.Equal(2, playlist.Variants.Count);

        Assert.Equal(800000, playlist.Variants[0].Bandwidth);
        Assert.Equal("http://cdn.test/live/360p/index.m3u8", playlist.Variants[0].Url.ToString());

        // URL absolut dibiarkan apa adanya.
        Assert.Equal(2400000, playlist.Variants[1].Bandwidth);
        Assert.Equal("http://mirror.cdn.test/hd/index.m3u8", playlist.Variants[1].Url.ToString());
    }

    [Fact]
    public void PickVariant_Memilih_Bandwidth_Terbesar()
    {
        MediaPlaylist master = HlsPlaylistParser.Parse(
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1200000
            rendah.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=3800000
            tinggi.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2500000
            menengah.m3u8
            """,
            BaseUri);

        Assert.Equal(3800000, HlsPlaylistParser.PickVariant(master).Bandwidth);
        Assert.Equal("http://cdn.test/live/tinggi.m3u8", HlsPlaylistParser.PickVariant(master).Url.ToString());
    }

    [Fact]
    public void Master_Tanpa_Varian_PickVariant_Gagal_CorruptResponse()
    {
        // Tag STREAM-INF ada tetapi tidak ada baris URL yang mengikutinya.
        MediaPlaylist master = HlsPlaylistParser.Parse("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n", BaseUri);

        DownloadException ex = Assert.Throws<DownloadException>(() => HlsPlaylistParser.PickVariant(master));
        Assert.Equal(DownloadErrorKind.CorruptResponse, ex.Kind);
    }

    [Fact]
    public void Media_Playlist_Memuat_Segmen_Dengan_Durasi()
    {
        MediaPlaylist playlist = HlsPlaylistParser.Parse(
            """
            #EXTM3U
            #EXT-X-TARGETDURATION:4
            #EXT-X-VERSION:3
            #EXTINF:4.0,
            seg-1.ts
            #EXTINF:4.0,
            seg-2.ts
            #EXTINF:4.5,
            https://mirror.cdn.test/seg/seg-3.ts
            """,
            BaseUri);

        Assert.Equal(MediaPlaylistKind.Media, playlist.Kind);
        Assert.Equal(3, playlist.Segments.Count);

        // URL relatif di-resolve terhadap URI playlist.
        Assert.Equal("http://cdn.test/live/seg-1.ts", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/live/seg-2.ts", playlist.Segments[1].Url.ToString());

        // URL absolut dibiarkan.
        Assert.Equal("https://mirror.cdn.test/seg/seg-3.ts", playlist.Segments[2].Url.ToString());

        Assert.Equal(4.0, playlist.Segments[0].DurationSeconds);
        Assert.Equal(4.0, playlist.Segments[1].DurationSeconds);
        Assert.Equal(4.5, playlist.Segments[2].DurationSeconds);
    }

    [Fact]
    public void ExtMap_Init_Segment_Ditempatkan_Paling_Awal()
    {
        MediaPlaylist playlist = HlsPlaylistParser.Parse(
            """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:6.0,
            chunk-1.m4s
            #EXTINF:6.0,
            chunk-2.m4s
            """,
            BaseUri);

        Assert.Equal(3, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/live/init.mp4", playlist.Segments[0].Url.ToString());
        Assert.Null(playlist.Segments[0].DurationSeconds);
        Assert.Equal("http://cdn.test/live/chunk-1.m4s", playlist.Segments[1].Url.ToString());
        Assert.Equal(6.0, playlist.Segments[1].DurationSeconds);
    }

    [Fact]
    public void Key_Method_None_Tidak_Menghalangi_Penguraian()
    {
        MediaPlaylist playlist = HlsPlaylistParser.Parse(
            """
            #EXTM3U
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:4.0,
            seg-1.ts
            #EXTINF:4.0,
            seg-2.ts
            """,
            BaseUri);

        Assert.Equal(2, playlist.Segments.Count);
    }

    [Fact]
    public void Key_Aes128_Menghasilkan_UnsupportedMedia_Terenkripsi()
    {
        DownloadException ex = Assert.Throws<DownloadException>(() => HlsPlaylistParser.Parse(
            """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0X9C7DB8778570D05C3177C349FD9236AA
            #EXTINF:4.0,
            seg-1.ts
            """,
            BaseUri));

        Assert.Equal(DownloadErrorKind.UnsupportedMedia, ex.Kind);
        Assert.Contains("terenkripsi", ex.Message);
    }

    [Fact]
    public void ByteRange_Menghasilkan_UnsupportedMedia()
    {
        DownloadException ex = Assert.Throws<DownloadException>(() => HlsPlaylistParser.Parse(
            """
            #EXTM3U
            #EXT-X-BYTERANGE:1000@0
            #EXTINF:4.0,
            seg-1.ts
            """,
            BaseUri));

        Assert.Equal(DownloadErrorKind.UnsupportedMedia, ex.Kind);
    }

    [Fact]
    public void Line_Ending_CRLF_Ditoleransi()
    {
        MediaPlaylist playlist = HlsPlaylistParser.Parse(
            "#EXTM3U\r\n#EXT-X-TARGETDURATION:4\r\n" +
            "#EXTINF:4.0,\r\nseg-1.ts\r\n#EXTINF:4.0,\r\nseg-2.ts\r\n#EXTINF:4.0,\r\nseg-3.ts\r\n",
            BaseUri);

        Assert.Equal(MediaPlaylistKind.Media, playlist.Kind);
        Assert.Equal(3, playlist.Segments.Count);
        Assert.All(playlist.Segments, s => Assert.Equal(4.0, s.DurationSeconds));
        Assert.Equal("http://cdn.test/live/seg-3.ts", playlist.Segments[2].Url.ToString());
    }
}
