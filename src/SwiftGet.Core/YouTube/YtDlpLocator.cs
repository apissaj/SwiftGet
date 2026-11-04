namespace SwiftGet.Core.YouTube;

/// <summary>
/// Mencari yt-dlp.exe: lokasi yang diatur pengguna, folder aplikasi, lalu PATH.
/// Pola sama dengan FfmpegLocator; yt-dlp tidak dibundel karena diperbarui
/// komunitas secara berkala.
/// </summary>
public static class YtDlpLocator
{
    public static string? Find(string? configuredPath) =>
        Find(configuredPath, AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("PATH"));

    internal static string? Find(string? configuredPath, string? baseDirectory, string? pathEnv)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        if (!string.IsNullOrEmpty(baseDirectory) &&
            File.Exists(Path.Combine(baseDirectory, "yt-dlp.exe")))
            return Path.Combine(baseDirectory, "yt-dlp.exe");

        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, "yt-dlp.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Entri PATH yang tidak sah dilewati.
            }
        }

        return null;
    }
}
