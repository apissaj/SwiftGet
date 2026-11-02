using SwiftGet.Core;
using SwiftGet.Core.Downloads.Media;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Uji pengurai MPD DASH tanpa I/O: SegmentList, SegmentTemplate ber-$Number$,
/// SegmentTimeline eksplisit, pengisi nol dan token template lainnya,
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

    // ---- SegmentTemplate: pengisi nol $Number%0Nd$ ----

    [Fact]
    public void SegmentTemplate_NumberDenganPengisiNol_MenghasilkanNomorBerlebarTetap()
    {
        // PT10S dengan durasi 4 detik → 3 segmen, mulai dari startNumber 7.
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Number%05d$.m4s" duration="4" timescale="1" startNumber="7"/>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        Assert.Equal(3, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/dash/seg-00007.m4s", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-00008.m4s", playlist.Segments[1].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-00009.m4s", playlist.Segments[2].Url.ToString());
    }

    // ---- SegmentTimeline eksplisit ----

    [Fact]
    public void SegmentTimeline_Eksplisit_MenghasilkanInitDanSegmenSesuaiEntri()
    {
        // S pertama: satu entri pada t=0. S kedua: t eksplisit 4 dengan r=2 →
        // tiga entri pada t=4, 8, 12. Jumlah segmen dibaca dari timeline,
        // bukan dihitung dari durasi presentasi.
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Time$.m4s" initialization="init.mp4" startNumber="1">
                  <SegmentTimeline>
                    <S t="0" d="4"/>
                    <S t="4" d="4" r="2"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        Assert.Equal(5, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/dash/init.mp4", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-0.m4s", playlist.Segments[1].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-4.m4s", playlist.Segments[2].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-8.m4s", playlist.Segments[3].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-12.m4s", playlist.Segments[4].Url.ToString());
    }

    [Fact]
    public void SegmentTimeline_TanpaTeksplisit_WaktuBerlanjutOtomatis()
    {
        // S kedua tanpa @t: waktunya melanjutkan akumulasi S sebelumnya
        // (0, lalu 4, 6, 8 mengikuti durasi masing-masing S).
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Time$.m4s" startNumber="1">
                  <SegmentTimeline>
                    <S t="0" d="4"/>
                    <S d="2" r="2"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        // Tanpa @initialization, daftar hanya memuat segmen media.
        Assert.Equal(4, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/dash/seg-0.m4s", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-4.m4s", playlist.Segments[1].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-6.m4s", playlist.Segments[2].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-8.m4s", playlist.Segments[3].Url.ToString());
    }

    [Fact]
    public void SegmentTimeline_TokenNumber_BernomorUrutDariStartNumber()
    {
        // Timeline juga boleh memakai $Number$: nomornya urut dari startNumber,
        // tidak peduli berapa waktu tiap segmen.
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Number$.m4s" startNumber="10">
                  <SegmentTimeline>
                    <S t="0" d="4"/>
                    <S d="4"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        Assert.Equal(2, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/dash/seg-10.m4s", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-11.m4s", playlist.Segments[1].Url.ToString());
    }

    [Fact]
    public void SegmentTimeline_Timescale_TidakMengubahNilaiTime()
    {
        // Nilai t/d di timeline sudah dalam satuan @timescale, jadi $Time$
        // memakai angka mentahnya tanpa dibagi lagi.
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Time$.m4s" timescale="90000" startNumber="1">
                  <SegmentTimeline>
                    <S t="0" d="90000"/>
                    <S d="90000" r="1"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        Assert.Equal(3, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/dash/seg-0.m4s", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-90000.m4s", playlist.Segments[1].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-180000.m4s", playlist.Segments[2].Url.ToString());
    }

    [Fact]
    public void SegmentTimeline_Rnol_MenghasilkanSatuEntri()
    {
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Time$.m4s" initialization="init.mp4" startNumber="1">
                  <SegmentTimeline>
                    <S t="0" d="4" r="0"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        Assert.Equal(2, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/dash/init.mp4", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-0.m4s", playlist.Segments[1].Url.ToString());
    }

    [Fact]
    public void SegmentTimeline_Rnegatif_Menghasilkan_UnsupportedMedia()
    {
        // r negatif berarti segmen tak terbatas (siaran langsung) — tidak
        // ada akhir daftar yang bisa diunduh sampai selesai.
        DownloadException ex = Assert.Throws<DownloadException>(() => DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Time$.m4s" startNumber="1">
                  <SegmentTimeline>
                    <S t="0" d="4" r="-1"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
            """), BaseUri));

        Assert.Equal(DownloadErrorKind.UnsupportedMedia, ex.Kind);
    }

    [Fact]
    public void SegmentTimeline_STanpaDurasi_Menghasilkan_CorruptResponse()
    {
        DownloadException ex = Assert.Throws<DownloadException>(() => DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Time$.m4s" startNumber="1">
                  <SegmentTimeline>
                    <S t="0"/>
                  </SegmentTimeline>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
            """), BaseUri));

        Assert.Equal(DownloadErrorKind.CorruptResponse, ex.Kind);
    }

    [Fact]
    public void SegmentTimeline_Kosong_Menghasilkan_CorruptResponse()
    {
        DownloadException ex = Assert.Throws<DownloadException>(() => DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Time$.m4s" startNumber="1">
                  <SegmentTimeline/>
                </SegmentTemplate>
              </Representation>
            </AdaptationSet>
            """), BaseUri));

        Assert.Equal(DownloadErrorKind.CorruptResponse, ex.Kind);
    }

    [Fact]
    public void SegmentTemplate_TimeTanpaTimeline_Menghasilkan_UnsupportedMedia()
    {
        // $Time$ hanya bisa dihitung dari timeline eksplisit; @duration saja
        // tidak memberi nilai waktu per segmen.
        DownloadException ex = Assert.Throws<DownloadException>(() => DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$Time$.m4s" duration="4" timescale="1" startNumber="1"/>
              </Representation>
            </AdaptationSet>
            """), BaseUri));

        Assert.Equal(DownloadErrorKind.UnsupportedMedia, ex.Kind);
    }

    // ---- Token template lainnya ----

    [Fact]
    public void SegmentTemplate_DollarGanda_MenjadiDolarTunggal()
    {
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="900000" mimeType="video/mp4">
                <SegmentTemplate media="seg-$$-$Number$.m4s" duration="4" timescale="1" startNumber="1"/>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        Assert.Equal(3, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/dash/seg-$-1.m4s", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-$-2.m4s", playlist.Segments[1].Url.ToString());
        Assert.Equal("http://cdn.test/dash/seg-$-3.m4s", playlist.Segments[2].Url.ToString());
    }

    [Fact]
    public void SegmentTemplate_KombinasiToken_SemuaTokenDiekspansi()
    {
        // $RepresentationID$, $Bandwidth$, dan $Number%03d$ muncul bersamaan
        // dalam satu media template.
        MediaPlaylist playlist = DashPlaylistParser.Parse(BungkusMpd(
            """
            <AdaptationSet mimeType="video/mp4">
              <Representation id="avc1" bandwidth="1200000" mimeType="video/mp4">
                <SegmentTemplate media="v-$RepresentationID$-$Bandwidth$-$Number%03d$.m4s" duration="5" timescale="1" startNumber="5"/>
              </Representation>
            </AdaptationSet>
            """), BaseUri);

        // PT10S dengan durasi 5 detik → 2 segmen.
        Assert.Equal(2, playlist.Segments.Count);
        Assert.Equal("http://cdn.test/dash/v-avc1-1200000-005.m4s", playlist.Segments[0].Url.ToString());
        Assert.Equal("http://cdn.test/dash/v-avc1-1200000-006.m4s", playlist.Segments[1].Url.ToString());
    }
}
