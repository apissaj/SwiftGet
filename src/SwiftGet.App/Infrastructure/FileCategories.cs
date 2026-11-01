using System.Collections.Frozen;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Kategori unduhan yang diturunkan dari ekstensi nama berkas.
/// <para>
/// Kategori hanya menjawab satu pertanyaan: berkas ini jenisnya apa.
/// Ia tidak dipakai sebagai warna atau ikon; satu kata label di panel
/// periksa dan riwayat cukup (DESIGN.md bagian 3 dan 4 menolak palet
/// kategorikal dan warna dekoratif).
/// </para>
/// </summary>
public enum DownloadCategory
{
    Video,
    Audio,
    Gambar,
    Dokumen,
    Arsip,
    Program,
    Lainnya,
}

/// <summary>
/// Pemeta ekstensi ke kategori. Murni fungsi, tanpa keadaan, mudah dites.
/// </summary>
public static class FileCategories
{
    private static readonly FrozenDictionary<string, DownloadCategory> Map =
        Build().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, DownloadCategory> Build()
    {
        var map = new Dictionary<string, DownloadCategory>(StringComparer.OrdinalIgnoreCase);

        void Add(DownloadCategory category, params string[] extensions)
        {
            foreach (string ext in extensions) map[ext] = category;
        }

        Add(DownloadCategory.Video,
            "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "ts", "3gp");
        Add(DownloadCategory.Audio,
            "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus");
        Add(DownloadCategory.Gambar,
            "jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "ico", "tiff", "heic", "avif");
        Add(DownloadCategory.Dokumen,
            "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp", "epub", "csv", "md");
        Add(DownloadCategory.Arsip,
            "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab");
        Add(DownloadCategory.Program,
            "exe", "msi", "msix", "appx", "apk", "bat", "cmd", "ps1", "dmg", "deb", "rpm");

        return map;
    }

    /// <summary>Kategori dari nama berkas. Tanpa ekstensi yang dikenal menjadi Lainnya.</summary>
    public static DownloadCategory Classify(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return DownloadCategory.Lainnya;

        string ext = Path.GetExtension(fileName);
        if (ext.Length <= 1) return DownloadCategory.Lainnya; // titik saja atau tidak ada

        // Path.GetExtension menyertakan titik (".mp4") sedangkan kunci map
        // tanpa titik ("mp4"); buang titik di depan sebelum mencari.
        return Map.TryGetValue(ext[1..], out DownloadCategory category)
            ? category
            : DownloadCategory.Lainnya;
    }

    /// <summary>Label bahasa Indonesia untuk kategori, siap ditampilkan.</summary>
    public static string Label(DownloadCategory category) => category switch
    {
        DownloadCategory.Video => "Video",
        DownloadCategory.Audio => "Audio",
        DownloadCategory.Gambar => "Gambar",
        DownloadCategory.Dokumen => "Dokumen",
        DownloadCategory.Arsip => "Arsip",
        DownloadCategory.Program => "Program",
        _ => "Lainnya",
    };

    /// <summary>Klasifikasi sekaligus labelnya, untuk dipakai UI.</summary>
    public static string LabelOf(string? fileName) => Label(Classify(fileName));
}
