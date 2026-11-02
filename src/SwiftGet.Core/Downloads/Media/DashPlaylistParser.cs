using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SwiftGet.Core.Http;

namespace SwiftGet.Core.Downloads.Media;

/// <summary>
/// Pengurai DASH MPD (.mpd) untuk kasus yang lazim: satu AdaptationSet,
/// Representation terpilih dengan SegmentURL berurutan atau SegmentTemplate
/// ber-$Number$. DASH menempatkan video dan audio di AdaptationSet terpisah;
/// menggabungkan keduanya butuh muxing yang jauh lebih rumit, jadi yang
/// dipilih jalur pertama dan hal itu dinyatakan terbuka di pesan galat bila
/// playlist jelas multi-jalur.
/// </summary>
public static class DashPlaylistParser
{
    private static readonly XNamespace Ns = "urn:mpeg:dash:schema:mpd:2011";

    public static MediaPlaylist Parse(string xml, Uri mpdUri)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "MPD DASH tidak bisa diurai: " + ex.Message, ex);
        }

        XElement? mpd = doc.Root;
        if (mpd is null || mpd.Name.LocalName != "MPD")
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "Berkas ini bukan MPD DASH yang dikenal.");

        XElement? period = mpd.Descendants(Ns + "Period").FirstOrDefault();
        XElement? adaptation = period?.Descendants(Ns + "AdaptationSet").FirstOrDefault();
        XElement? representation = adaptation?.Descendants(Ns + "Representation")
            .OrderByDescending(r => (long?)r.Attribute("bandwidth") ?? 0)
            .FirstOrDefault();

        if (period is null || adaptation is null || representation is null)
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "MPD DASH tidak memuat Representation apa pun.");

        int adaptationCount = period.Descendants(Ns + "AdaptationSet").Count();
        if (adaptationCount > 1)
            throw new DownloadException(DownloadErrorKind.UnsupportedMedia,
                "MPD DASH memisahkan video dan audio di beberapa AdaptationSet; " +
                "penggabungan keduanya belum didukung.");

        string repId = (string?)representation.Attribute("id") ?? "";
        long repBandwidth = (long?)representation.Attribute("bandwidth") ?? 0;

        var playlist = new MediaPlaylist { Kind = MediaPlaylistKind.Media, BaseUri = mpdUri };
        Uri ResolveIn(string? reference) => Resolve(mpdUri, reference);

        // Jalur 1: SegmentList dengan daftar SegmentURL eksplisit.
        XElement? segmentList = representation.Element(Ns + "SegmentList")
            ?? adaptation.Element(Ns + "SegmentList");
        if (segmentList is not null)
        {
            string? initRef = (string?)segmentList.Element(Ns + "Initialization")?.Attribute("sourceURL");
            if (!string.IsNullOrWhiteSpace(initRef))
                playlist.Segments.Add(new MediaSegmentEntry(ResolveIn(initRef)));

            foreach (XElement seg in segmentList.Descendants(Ns + "SegmentURL"))
            {
                string? mediaRef = (string?)seg.Attribute("media");
                if (!string.IsNullOrWhiteSpace(mediaRef))
                    playlist.Segments.Add(new MediaSegmentEntry(ResolveIn(mediaRef)));
            }

            if (playlist.Segments.Count == 0)
                throw new DownloadException(DownloadErrorKind.CorruptResponse,
                    "SegmentList DASH tidak memuat SegmentURL apa pun.");
            return playlist;
        }

        // Jalur 2: SegmentTemplate dengan $Number$ berurutan.
        XElement? template = representation.Element(Ns + "SegmentTemplate")
            ?? adaptation.Element(Ns + "SegmentTemplate")
            ?? period.Element(Ns + "SegmentTemplate");
        if (template is not null)
        {
            ExpandTemplate(playlist, mpd, period, representation, template, repId, ResolveIn);
            return playlist;
        }

        // Tanpa segmentasi: Representation punya BaseURL tunggal (berkas utuh).
        string? single = (string?)representation.Element(Ns + "BaseURL")
            ?? (string?)adaptation.Element(Ns + "BaseURL");
        if (!string.IsNullOrWhiteSpace(single))
        {
            playlist.Segments.Add(new MediaSegmentEntry(ResolveIn(single)));
            return playlist;
        }

        throw new DownloadException(DownloadErrorKind.UnsupportedMedia,
            "Skema segmentasi DASH ini belum didukung (hanya SegmentList dan SegmentTemplate $Number$).");
    }

    private static void ExpandTemplate(
        MediaPlaylist playlist, XElement mpd, XElement period,
        XElement representation, XElement template,
        string repId, Func<string?, Uri> resolveIn)
    {
        string mediaTemplate = (string?)template.Attribute("media")
            ?? throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "SegmentTemplate DASH tanpa atribut media.");

        string initTemplate = (string?)template.Attribute("initialization") ?? "";
        long startNumber = (long?)template.Attribute("startNumber") ?? 1;
        long duration = (long?)template.Attribute("duration") ?? 0;
        long timescale = (long?)template.Attribute("timescale") ?? 1;
        long? endNumber = (long?)template.Attribute("endNumber");

        if (duration <= 0 || timescale <= 0)
            throw new DownloadException(DownloadErrorKind.UnsupportedMedia,
                "SegmentTemplate DASH dengan SegmentTimeline belum didukung; " +
                "hanya @duration yang bisa dihitung.");

        long segmentSeconds = duration / timescale;
        long count;
        if (endNumber is long known)
        {
            count = known - startNumber + 1;
        }
        else
        {
            TimeSpan? presentation = ParseDuration((string?)mpd.Attribute("mediaPresentationDuration"));
            if (presentation is null)
                throw new DownloadException(DownloadErrorKind.UnsupportedMedia,
                    "Panjang media DASH tidak dinyatakan; jumlah segmen tidak bisa dihitung.");
            count = (long)Math.Ceiling(presentation.Value.TotalSeconds / (double)segmentSeconds);
        }

        if (count <= 0)
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "Jumlah segmen DASH terhitung nol atau negatif.");

        string repBandwidth = ((long?)representation.Attribute("bandwidth") ?? 0)
            .ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(initTemplate))
            playlist.Segments.Add(new MediaSegmentEntry(
                resolveIn(Expand(initTemplate, repId, 0, repBandwidth))));

        for (long n = startNumber; n < startNumber + count; n++)
        {
            playlist.Segments.Add(new MediaSegmentEntry(
                resolveIn(Expand(mediaTemplate, repId, n, repBandwidth))));
        }
    }

    private static string Expand(string template, string repId, long number, string bandwidth) =>
        template
            .Replace("$RepresentationID$", repId, StringComparison.Ordinal)
            .Replace("$Bandwidth$", bandwidth, StringComparison.Ordinal)
            .Replace("$Number$", number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$$", "$", StringComparison.Ordinal);

    /// <summary>Durasi ISO 8601 ("PT1H2M3S") seperti pada atribut MPD.</summary>
    private static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return XmlConvert.ToTimeSpan(value); }
        catch (FormatException) { return null; }
    }

    private static Uri Resolve(Uri baseUri, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new DownloadException(DownloadErrorKind.CorruptResponse,
                "MPD DASH memuat referensi URL kosong.");
        return Uri.TryCreate(reference, UriKind.Absolute, out Uri? absolute)
            ? absolute
            : new Uri(baseUri, reference);
    }
}
