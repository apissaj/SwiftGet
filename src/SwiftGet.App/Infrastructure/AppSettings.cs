using System.Text.Json;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Pengaturan aplikasi yang dipersistensikan ke profil pengguna.
/// Semua folder default berada di bawah <c>%USERPROFILE%</c>.
/// </summary>
public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SwiftGet");

    public int MaxConcurrentDownloads { get; set; } = 3;

    public int MaxConnectionsPerDownload { get; set; } = 8;

    public int MaxConnectionsPerHost { get; set; } = 12;

    public bool WatchClipboard { get; set; } = true;

    public bool StartDownloadsAutomatically { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// Endpoint lokal untuk bookmarklet: menerima URL dari browser lewat
    /// http://127.0.0.1:{LocalEndpointPort}/. Mati secara bawaan; pengguna
    /// yang memang menghendaki integrasi browser yang menyalakannya.
    /// </summary>
    public bool EnableLocalEndpoint { get; set; }

    /// <summary>Port endpoint lokal. Hanya mengikat 127.0.0.1, bukan antarmuka lain.</summary>
    public int LocalEndpointPort { get; set; } = 49160;

    /// <summary>
    /// Tema terakhir yang dipilih pengguna. Null berarti ikuti tema Windows
    /// pada saat aplikasi dijalankan.
    /// </summary>
    public bool? DarkTheme { get; set; }

    /// <summary>Folder profil: konfigurasi, state unduhan, log.</summary>
    public static string ProfileDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftGet");

    public static string StateDirectory => Path.Combine(ProfileDirectory, "state");

    private static string SettingsPath => Path.Combine(ProfileDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(SettingsPath), JsonOptions);

                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (IOException) { /* pakai default */ }
        catch (JsonException) { /* pakai default */ }
        catch (UnauthorizedAccessException) { /* pakai default */ }

        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ProfileDirectory);
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch (IOException) { /* pengaturan tidak kritis */ }
        catch (UnauthorizedAccessException) { /* idem */ }
    }

    /// <summary>Batasi nilai ke rentang aman supaya file yang diedit manual tidak merusak aplikasi.</summary>
    public void Normalize()
    {
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 8);
        MaxConnectionsPerDownload = Math.Clamp(MaxConnectionsPerDownload, 1, 32);
        MaxConnectionsPerHost = Math.Clamp(MaxConnectionsPerHost, 1, 32);
        LocalEndpointPort = Math.Clamp(LocalEndpointPort, 1024, 65535);

        if (string.IsNullOrWhiteSpace(DownloadDirectory))
        {
            DownloadDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SwiftGet");
        }
    }
}

