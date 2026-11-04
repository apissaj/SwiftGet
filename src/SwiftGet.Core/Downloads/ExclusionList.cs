using System.Text;
using System.Text.RegularExpressions;

namespace SwiftGet.Core.Downloads;

/// <summary>
/// Daftar pola URL yang tidak boleh ditangkap otomatis (clipboard,
/// ekstensi, bookmarklet) — pola ala IDM: wildcard <c>*</c> dan <c>?</c>,
/// satu pola per baris, termasuk skema. Unduhan yang ditambahkan pengguna
/// secara eksplisit lewat kotak URL tetap diizinkan.
/// </summary>
public sealed class ExclusionList
{
    private readonly string _path;
    private readonly List<(Regex Regex, string Pattern)> _entries = new();

    public ExclusionList(string path)
    {
        _path = path;
        Reload();
    }

    /// <summary>Pola aktif, urut seperti di berkas.</summary>
    public IReadOnlyList<string> Patterns => _entries.Select(e => e.Pattern).ToList();

    public void Reload()
    {
        _entries.Clear();
        try
        {
            if (!File.Exists(_path)) return;

            foreach (string line in File.ReadAllLines(_path))
            {
                string pattern = line.Trim();
                if (pattern.Length == 0 || pattern.StartsWith('#')) continue;
                _entries.Add((Compile(pattern), pattern));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Daftar tak terbaca: perlakukan kosong, jangan gagal menangkap.
        }
    }

    public bool IsExcluded(string url)
    {
        foreach (var (regex, _) in _entries)
            if (regex.IsMatch(url)) return true;
        return false;
    }

    /// <summary>Buat berkas contoh bila belum ada, supaya pengguna tahu di mana mengedit.</summary>
    public static void WriteSeed(string path)
    {
        if (File.Exists(path)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, new[]
        {
            "# Daftar pengecualian SwiftGet — satu pola per baris.",
            "# URL yang cocok tidak ditangkap otomatis dari clipboard/ekstensi.",
            "# Wildcard: * = apa saja, ? = satu karakter. Baris yang diawali # diabaikan.",
            "# Contoh:",
            "# http://*.contoh.com/*.wav",
            "# https://cdn.contoh.org/iklan/*",
        });
    }

    private static Regex Compile(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (char c in pattern)
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
