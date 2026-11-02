using SwiftGet.Core;
using SwiftGet.Core.Downloads.Media;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji pengurai MPD DASH tanpa I/O: SegmentList, SegmentTemplate ber-$Number$,
/// pilihan Representation, penolakan multi-AdaptationSet, dan galat XML.
/// </summary>
public class DashParserTests
{
    private const string Ns = "urn:mpeg:dash:schema:mpd:2011";
    private static readonly Uri BaseUri = new("http://cdn.test/dash/manifest.mpd");

    private static string BungkusMpd(string isi, string atributMpd = "type=\"static\" mediaPresentationDuration=\"PT10S\"") =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <MPD xmlns="{Ns}" {atributMpd}>
          <Period>
            {isi}
          </Period>
        </MPD>
        """;

    [Fact]
    public void SegmentList_Dengan_Initialization_Menghasilkan_Init_Di_Depan()
    {
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="720" bandwidth="1500000" mimeType="video/mp4">
                <SegmentList timescale="1" duration="4">
                  <Initialization sourceURL="init-720.m4s"/>
                  <SegmentURL media="seg-1.m4s"/>
                  <SegmentURL media="seg-2.m4s"/>
                </SegmentList>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        Assert.Equal(MediaPlaylistKind.Media, playlist.Kind);
        Assert.Equal(3, playlist.Segments.Count);

        // Referensi relatif di-resolve terhadap URI MPD.
        Assert.Equal("http://cdn.test/dash/init-720.m4s", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-1.m4s", playlist.Segments[1].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-2.m4s", playlist.Segments[2].Url.ToString());
    }

    [Theory]
    [InlineData(4, 1, true, 3)]   // PT10S dengan durasi 4 detik per segmen → 3 segmen
    [InlineData(48000, 24000, false, 5)] // timescale non-detik: 2 detik per segmen → 5 segmen
    public void SegmentTemplate_Number_Diekspansi_Sesuai_Durasi_Presentasi(
        long duration, long timescale, bool pakaiInit, int segmenHarapan)
    {
        string init = pakaiInit ? "initialization=\"init-$RepresentationID$.m4s\" " : "";
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            $"""
            <AdaptationSet mimeType="video/mp4">
              <Representation id="video-720p" bandwidth="2000000" mimeType="video/mp4">
                <SegmentTemplate {init}media="chunk-$RepresentationID$-$Bandwidth$-$Number$.m4s" duration="{duration}" timescale="{timescale}" startNumber="1"/>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        int totalHarapan = segmenHarapan + (pakaiInit ? 1 : 0);
        Assert.Equal(totalHarapan, playlist.Segments.Count);

        int i = 0;
        if (pakaiInit)
        {
            Assert.Equal("http://cdn.test/dash/init-video-720p.m4s", playlist.Segments[i++].Url.ToString());
        }

        // Ekspansi $RepresentationID$, $Bandwidth$, dan $Number$ berurutan.
        for (int n = 1; i < playlist.Segments.Count; n++, i++)
        {
            Assert.Equal(
                $"http://cdn.test/dash/chunk-video-720p-2000000-{n}.m4s",
                playlist.Segments[i].Url.ToString());
        }
    }

    [Fact]
    public void SegmentTemplate_Memilih_Representation_Bandwidth_Terbesar()
    {
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="video-360p" bandwidth="600000" mimeType="video/mp4">
                <SegmentTemplate media="kecil-$Number$.m4s" duration="4" timescale="1" startNumber="1"/>
              </Representation>
              <Representation id="video-720p" bandwidth="2000000" mimeType="video/mp4">
                <SegmentTemplate media="besar-$Number$.m4s" duration="4" timescale="1" startNumber="1"/>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        Assert.Equal(3, playlist.Segments.Count);
        Assert.All(playlist.Segments, s =>
            Assert.Contains("besar-", s.Url.ToString()));
    }

    [Fact]
    public void SegmentTemplate_EndNumber_Mengakhiri_Hitungan_Tanpa_Durasi_Presentasi()
    {
        // Tanpa mediaPresentationDuration: endNumber yang menentukan jumlah segmen.
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Number$.m4s" duration="4" timescale="1" startNumber="1" endNumber="4"/>
              </Representation>
            </AdaptationSet>
            """,
            atributMpd: "type=\"static\""), BaseUri);

        Assert.Equal(4, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/dash/seg-1.m4s", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-4.m4s", playlist.Segments[3].Url.ToString());
    }

    [Fact]
    public void SegmentTemplate_Tanpa_Durasi_Menghasilkan_UnsupportedMedia()
    {
        DownloadException ex = Assert.Throws<DownloadException>(() => DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Number$.m4s" timescale="1" startNumber="1"/>
              </Representation>
            </AdaptationSet>
            """), BaseUri));

        Assert.Equal(DownloadErrorKind.UnsupportedMedia, ex.Kind);
    }

    [Fact]
    public void SegmentTemplate_Tanpa_Durasi_Presentasi_Dan_EndNumber_Menghasilkan_UnsupportedMedia()
    {
        DownloadException ex = Assert.Throws<DownloadException>(() => DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Number$.m4s" duration="4" timescale="1" startNumber="1"/>
              </Representation>
            </AdaptationSet>
            """,
            atributMpd: "type=\"static\""), BaseUri));

        Assert.Equal(DownloadErrorKind.UnsupportedMedia, ex.Kind);
    }

    [Fact]
    public void Dua_AdaptationSet_Menghasilkan_UnsupportedMedia()
    {
        DownloadException ex = Assert.Throws<DownloadException>(() => DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <BaseURL>video.mp4</BaseURL>
              </Representation>
            </AdaptationSet>
            <AdaptationSet mimeType="audio/mp4">
              <Representation id="a" bandwidth="128000" mimeType="audio/mp4">
                <BaseURL>audio.mp4</BaseURL>
              </Representation>
            </AdaptationSet>
            """), BaseUri));

        Assert.Equal(DownloadErrorKind.UnsupportedMedia, ex.Kind);
        Assert.Contains("AdaptationSet", ex.Message);
    }

    [Fact]
    public void BaseURL_Tunggal_Tanpa_Segmentasi_Menghasilkan_Satu_Segmen()
    {
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <BaseURL>film-utuh.mp4</BaseURL>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        Assert.Single(playlist.Segments);
        Assert.Equal("http://cdn.test/dash/film-utuh.mp4", playlist.Segments[0].Url.ToString());
    }

    [Fact]
    public void Xml_Rusak_Menghasilkan_CorruptResponse()
    {
        DownloadException ex = Assert.Throws<DownloadException>(() =>
            DashPlaylistParser.Parse($"<MPD xmlns=\"{Ns}\"><Period>", BaseUri));

        Assert.Equal(DownloadErrorKind.CorruptResponse, ex.Kind);
    }

    [Fact]
    public void Root_Bukan_Mpd_Menghasilkan_CorruptResponse()
    {
        DownloadException ex = Assert.Throws<DownloadException>(() =>
            DashPlaylistParser.Parse("<rss version=\"2.0\"/>", BaseUri));

        Assert.Equal(DownloadErrorKind.CorruptResponse, ex.Kind);
    }
}
