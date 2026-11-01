namespace SwiftGet.App.ViewModels;

/// <summary>
/// Penyaring daftar unduhan.
/// <para>
/// Hanya dua filter yang dibuat: "semua" dan "yang berjalan", plus "yang
/// gagal". Filter yang tidak bisa dijawab pengguna tidak dibuat (R-26):
/// tidak ada kotak pencarian yang tidak melakukan apa pun.
/// </para>
/// </summary>
public enum DownloadFilter
{
    /// <summary>Semua unduhan, apa pun statusnya.</summary>
    All,

    /// <summary>Hanya yang sedang berjalan atau dijeda.</summary>
    Active,

    /// <summary>Hanya yang gagal.</summary>
    Failed,

    /// <summary>Hanya yang selesai.</summary>
    Completed,
}

/// <summary>
/// Urutan daftar unduhan.
/// <para>
/// Hanya dua kolom yang bisa diurutkan, dan keduanya menjawab keputusan
/// pengguna: mana yang perlu perhatian, dan mana yang paling besar.
/// </para>
/// </summary>
public enum DownloadSort
{
    /// <summary>Urut saat ditambahkan, yang terbaru di atas.</summary>
    Newest,

    /// <summary>Status: yang gagal dulu, lalu berjalan, dijeda, selesai.</summary>
    Status,

    /// <summary>Ukuran, dari yang terbesar.</summary>
    Size,
}
