namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Format nilai untuk tampilan: ukuran byte, kecepatan, dan sisa waktu.
/// Dikumpulkan di satu tempat supaya format konsisten di seluruh UI.
/// </summary>
public static class Humanize
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "-";
        if (bytes < 1024) return $"{bytes} B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value >= 100 ? $"{value:F0} {SizeUnits[unit]}" : $"{value:F1} {SizeUnits[unit]}";
    }

    public static string BytesPerSecond(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "-";
        return Bytes((long)bytesPerSecond) + "/s";
    }

    public static string Duration(TimeSpan? span)
    {
        if (span is null) return "-";

        TimeSpan t = span.Value;
        if (t.TotalSeconds < 1) return "<1s";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}j {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }

    public static string Percent(double fraction) => $"{fraction * 100:F0}%";
}
