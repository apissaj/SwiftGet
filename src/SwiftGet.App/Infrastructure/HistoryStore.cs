using System.Text.Json;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Satu catatan riwayat: unduhan yang sudah selesai dan terverifikasi.
/// Riwayat hanya mencatat keberhasilan; kegagalan tetap terlihat di daftar
/// utama sampai pengguna membersihkannya.
/// </summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public string FinalPath { get; set; } = "";
    public long? TotalBytes { get; set; }

    /// <summary>Kategori saat dicatat, disimpan agar label tetap stabil.</summary>
    public string Category { get; set; } = "";

    public DateTimeOffset CompletedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Penyimpan riwayat unduhan antar sesi: satu berkas JSON di folder state,
/// ditulis atomik, dibatasi beberapa ratus entri terbaru.
/// <para>
/// Gagal membaca atau menulis tidak pernah menggagalkan alur unduhan —
/// riwayat adalah kenyamanan, bukan data kritikal.
/// </para>
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 500;

    private readonly string _directory;

    /// <summary>
    /// Folder penyimpanan riwayat. Default folder state aplikasi; parameter
    /// dipakai test untuk memakai folder sementara.
    /// </summary>
    public HistoryStore(string? directory = null)
    {
        _directory = directory ?? AppSettings.StateDirectory;
    }

    private string HistoryPath => Path.Combine(_directory, "history.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Muat seluruh riwayat, terbaru di depan. Gagal baca berarti kosong.</summary>
    public List<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return [];

            string json = File.ReadAllText(HistoryPath);
            List<HistoryEntry>? entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOptions);
            return entries ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Catat satu unduhan selesai: sisip di depan, potong ke batas, simpan.
    /// Kegagalan disk ditelan — pencatatan riwayat tidak boleh mengganggu
    /// penyelesaian unduhan.
    /// </summary>
    public void Record(HistoryEntry entry)
    {
        try
        {
            List<HistoryEntry> entries = Load();
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

            Save(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Riwayat tidak kritis; abaikan.
        }
    }

    /// <summary>Kosongkan seluruh riwayat.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(HistoryPath)) File.Delete(HistoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Idem.
        }
    }

    private void Save(List<HistoryEntry> entries)
    {
        Directory.CreateDirectory(_directory);

        string temp = HistoryPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, HistoryPath, overwrite: true);
    }
}
