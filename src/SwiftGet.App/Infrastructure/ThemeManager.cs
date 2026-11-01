using System.Windows;
using System.Windows.Media;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Tema warna terang dan gelap.
/// <para>
/// Nilai di sini adalah sumber kebenaran palet; `DESIGN.md` bagian 4 memuat
/// alasan tiap warna. Kontras setiap pasangan dikunci oleh
/// <c>ThemeContrastTests</c>, jadi mengubah nilai tanpa mengukur ulang akan
/// menggagalkan test.
/// </para>
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    /// <summary>Brushes teks yang dipakai di atas tombol beraksen, per tema.</summary>
    public const string LightAccentInk = "#FFFFFF";
    public const string DarkAccentInk = "#0B201C";

    public static string AccentInk => IsDark ? DarkAccentInk : LightAccentInk;

    public static void Apply(bool dark)
    {
        IsDark = dark;

        ResourceDictionary resources = Application.Current.Resources;


        if (dark)
        {
            Set(resources, "AppBackgroundBrush", "#141518");
            Set(resources, "SurfaceBrush", "#1B1D21");
            Set(resources, "SurfaceAltBrush", "#24262B");
            Set(resources, "BorderBrush", "#33363C");
            Set(resources, "TextPrimaryBrush", "#EDEDEF");
            Set(resources, "TextSecondaryBrush", "#9B9EA6");
            Set(resources, "AccentBrush", "#3FB39C");
            Set(resources, "AccentHoverBrush", "#57C7B0");
            Set(resources, "AccentInkBrush", DarkAccentInk);
            Set(resources, "SuccessBrush", "#3DD68C");
            Set(resources, "DangerBrush", "#FF7A6E");
            Set(resources, "WarningBrush", "#FFB84D");
            Set(resources, "TrackBrush", "#111214");
            Set(resources, "GridLineBrush", "#2A2D32");
        }
        else
        {
            Set(resources, "AppBackgroundBrush", "#F7F7F4");
            Set(resources, "SurfaceBrush", "#FFFFFF");
            Set(resources, "SurfaceAltBrush", "#EFEFEA");
            Set(resources, "BorderBrush", "#E2E2DB");
            Set(resources, "TextPrimaryBrush", "#16171A");
            Set(resources, "TextSecondaryBrush", "#5F6169");
            Set(resources, "AccentBrush", "#0F6B5C");
            Set(resources, "AccentHoverBrush", "#0B5548");
            Set(resources, "AccentInkBrush", LightAccentInk);
            Set(resources, "SuccessBrush", "#0F7A46");
            Set(resources, "DangerBrush", "#B3261E");
            Set(resources, "WarningBrush", "#8A5B00");
            Set(resources, "TrackBrush", "#E6E6DF");
            Set(resources, "GridLineBrush", "#EFEFEA");
        }
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}


