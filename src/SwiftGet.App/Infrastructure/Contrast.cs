using System.Globalization;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Pemeriksa kontras WCAG 2.x berbasis rumus luminans relatif.
/// <para>
/// Dipakai untuk memverifikasi pasangan warna tema tanpa menebak. Rasio
/// dihitung dari nilai mentah: 4,49 tidak pernah dibulatkan menjadi lolos.
/// </para>
/// </summary>
public static class Contrast
{
    /// <summary>Ambang WCAG AA untuk teks normal.</summary>
    public const double NormalTextMinimum = 4.5;

    /// <summary>Ambang WCAG AA untuk teks besar (18pt+/14pt tebal) dan elemen non-teks.</summary>
    public const double LargeTextMinimum = 3.0;

    /// <summary>
    /// Rasio kontras antara dua warna hex (<c>#RRGGBB</c>). Urutan argumen tidak
    /// memengaruhi hasil.
    /// </summary>
    public static double Ratio(string foregroundHex, string backgroundHex)
    {
        double a = RelativeLuminance(foregroundHex);
        double b = RelativeLuminance(backgroundHex);
        double lighter = Math.Max(a, b);
        double darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Luminans relatif menurut WCAG 2.x.</summary>
    public static double RelativeLuminance(string hex)
    {
        (int r, int g, int b) = ParseHex(hex);

        double Channel(int raw)
        {
            double c = raw / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    /// <summary>True bila pasangan warna memenuhi ambang yang diminta.</summary>
    public static bool Passes(string foregroundHex, string backgroundHex, double minimum = NormalTextMinimum) =>
        Ratio(foregroundHex, backgroundHex) >= minimum;

    private static (int R, int G, int B) ParseHex(string hex)
    {
        string value = hex.Trim().TrimStart('#');

        if (value.Length == 3)
        {
            value = string.Concat(value[0], value[0], value[1], value[1], value[2], value[2]);
        }

        if (value.Length != 6)
            throw new FormatException($"Warna harus #RRGGBB atau #RGB, diterima '{hex}'.");

        return (
            int.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
