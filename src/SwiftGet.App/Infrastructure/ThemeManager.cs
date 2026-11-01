using System.Windows;
using System.Windows.Media;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Tema warna terang/gelap. Nilai dipasang sebagai resource aplikasi sehingga
/// XAML cukup memakai <c>DynamicResource</c>.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    public static void Apply(bool dark)
    {
        IsDark = dark;

        var resources = Application.Current.Resources;

        if (dark)
        {
            Set(resources, "AppBackgroundBrush", "#1E1E22");
            Set(resources, "SurfaceBrush", "#26262B");
            Set(resources, "SurfaceAltBrush", "#2E2E34");
            Set(resources, "BorderBrush", "#3A3A42");
            Set(resources, "TextPrimaryBrush", "#F0F0F4");
            Set(resources, "TextSecondaryBrush", "#A0A0AC");
            Set(resources, "AccentBrush", "#4C8DFF");
            Set(resources, "AccentHoverBrush", "#6AA0FF");
            Set(resources, "SuccessBrush", "#3DD68C");
            Set(resources, "DangerBrush", "#FF6B6B");
            Set(resources, "WarningBrush", "#FFB84D");
            Set(resources, "TrackBrush", "#1A1A1E");
        }
        else
        {
            Set(resources, "AppBackgroundBrush", "#F5F6F8");
            Set(resources, "SurfaceBrush", "#FFFFFF");
            Set(resources, "SurfaceAltBrush", "#F0F1F4");
            Set(resources, "BorderBrush", "#DFE1E6");
            Set(resources, "TextPrimaryBrush", "#1A1A20");
            Set(resources, "TextSecondaryBrush", "#6B6B76");
            Set(resources, "AccentBrush", "#2D6CDF");
            Set(resources, "AccentHoverBrush", "#4A85F0");
            Set(resources, "SuccessBrush", "#1FA463");
            Set(resources, "DangerBrush", "#D93B3B");
            Set(resources, "WarningBrush", "#C88000");
            Set(resources, "TrackBrush", "#E6E8EC");
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

