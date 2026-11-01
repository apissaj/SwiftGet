using SwiftGet.App.Infrastructure;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Mengunci setiap pasangan warna tema ke ambang WCAG AA.
/// <para>
/// Nilai warna di sini harus sama dengan `ThemeManager` dan `DESIGN.md` bagian 4.
/// Mengubah palet tanpa mengukur ulang akan menggagalkan test ini, dan itu
/// memang tujuannya.
/// </para>
/// </summary>
public sealed class ThemeContrastTests
{
    // Mode terang (DESIGN.md bagian 4)
    private const string LightCanvas = "#F7F7F4";
    private const string LightSurface = "#FFFFFF";
    private const string LightSurfaceAlt = "#EFEFEA";
    private const string LightText = "#16171A";
    private const string LightTextSecondary = "#5F6169";
    private const string LightAccent = "#0F6B5C";
    private const string LightAccentHover = "#0B5548";
    private const string LightAccentInk = "#FFFFFF";
    private const string LightSuccess = "#0F7A46";
    private const string LightDanger = "#B3261E";
    private const string LightWarning = "#8A5B00";

    // Mode gelap (DESIGN.md bagian 4)
    private const string DarkCanvas = "#141518";
    private const string DarkSurface = "#1B1D21";
    private const string DarkSurfaceAlt = "#24262B";
    private const string DarkText = "#EDEDEF";
    private const string DarkTextSecondary = "#9B9EA6";
    private const string DarkAccent = "#3FB39C";
    private const string DarkAccentHover = "#57C7B0";
    private const string DarkAccentInk = "#0B201C";
    private const string DarkSuccess = "#3DD68C";
    private const string DarkDanger = "#FF7A6E";
    private const string DarkWarning = "#FFB84D";

    public static TheoryData<string, string, string> LightPairs => new()
    {
        { LightText, LightSurface, "tinta di permukaan" },
        { LightText, LightCanvas, "tinta di kanvas" },
        { LightText, LightSurfaceAlt, "tinta di permukaan alt" },
        { LightTextSecondary, LightSurface, "tinta sekunder di permukaan" },
        { LightTextSecondary, LightCanvas, "tinta sekunder di kanvas" },
        { LightTextSecondary, LightSurfaceAlt, "tinta sekunder di permukaan alt" },
        { LightSuccess, LightSurface, "sukses di permukaan" },
        { LightSuccess, LightCanvas, "sukses di kanvas" },
        { LightDanger, LightSurface, "bahaya di permukaan" },
        { LightDanger, LightCanvas, "bahaya di kanvas" },
        { LightWarning, LightSurface, "peringatan di permukaan" },
        { LightWarning, LightCanvas, "peringatan di kanvas" },
    };

    public static TheoryData<string, string, string> DarkPairs => new()
    {
        { DarkText, DarkSurface, "tinta di permukaan" },
        { DarkText, DarkCanvas, "tinta di kanvas" },
        { DarkText, DarkSurfaceAlt, "tinta di permukaan alt" },
        { DarkTextSecondary, DarkSurface, "tinta sekunder di permukaan" },
        { DarkTextSecondary, DarkCanvas, "tinta sekunder di kanvas" },
        { DarkTextSecondary, DarkSurfaceAlt, "tinta sekunder di permukaan alt" },
        { DarkSuccess, DarkSurface, "sukses di permukaan" },
        { DarkSuccess, DarkCanvas, "sukses di kanvas" },
        { DarkDanger, DarkSurface, "bahaya di permukaan" },
        { DarkDanger, DarkCanvas, "bahaya di kanvas" },
        { DarkWarning, DarkSurface, "peringatan di permukaan" },
        { DarkWarning, DarkCanvas, "peringatan di kanvas" },
    };

    [Theory]
    [MemberData(nameof(LightPairs))]
    public void LightMode_Text_Meets_AA(string foreground, string background, string label)
    {
        double ratio = Contrast.Ratio(foreground, background);
        Assert.True(ratio >= Contrast.NormalTextMinimum,
            $"[terang] {label}: {foreground} di atas {background} = {ratio:F2}:1, minimum 4,5:1.");
    }

    [Theory]
    [MemberData(nameof(DarkPairs))]
    public void DarkMode_Text_Meets_AA(string foreground, string background, string label)
    {
        double ratio = Contrast.Ratio(foreground, background);
        Assert.True(ratio >= Contrast.NormalTextMinimum,
            $"[gelap] {label}: {foreground} di atas {background} = {ratio:F2}:1, minimum 4,5:1.");
    }

    [Fact]
    public void Button_Label_Meets_AA_In_Light_Theme()
    {
        double ratio = Contrast.Ratio(LightAccentInk, LightAccent);
        Assert.True(ratio >= Contrast.NormalTextMinimum,
            $"Putih di atas aksen terang = {ratio:F2}:1.");

        double hover = Contrast.Ratio(LightAccentInk, LightAccentHover);
        Assert.True(hover >= Contrast.NormalTextMinimum,
            $"Putih di atas aksen hover terang = {hover:F2}:1.");
    }

    [Fact]
    public void Button_Label_Meets_AA_In_Dark_Theme()
    {
        // Mode gelap memakai tinta gelap di atas teal terang, bukan putih.
        // Putih di atas #3FB39C hanya sekitar 2,4:1 dan gagal.
        double ratio = Contrast.Ratio(DarkAccentInk, DarkAccent);
        Assert.True(ratio >= Contrast.NormalTextMinimum,
            $"Tinta gelap di atas aksen gelap = {ratio:F2}:1.");

        double whiteWouldFail = Contrast.Ratio("#FFFFFF", DarkAccent);
        Assert.True(whiteWouldFail < Contrast.NormalTextMinimum,
            $"Asumsi desain salah: putih di atas aksen gelap ternyata {whiteWouldFail:F2}:1, " +
            "jadi tinta gelap tidak lagi diperlukan. Perbarui DESIGN.md bagian 4.");
    }

    [Fact]
    public void Accent_Is_Visible_Against_Surfaces()
    {
        // Aksen juga dipakai untuk batang segmen dan garis grafik: butuh 3:1.
        Assert.True(Contrast.Passes(LightAccent, LightSurface, Contrast.LargeTextMinimum),
            $"Aksen terang di permukaan = {Contrast.Ratio(LightAccent, LightSurface):F2}:1.");
        Assert.True(Contrast.Passes(DarkAccent, DarkSurface, Contrast.LargeTextMinimum),
            $"Aksen gelap di permukaan = {Contrast.Ratio(DarkAccent, DarkSurface):F2}:1.");
    }

    [Fact]
    public void Ratio_Is_Symmetric_And_Extremes_Are_Exact()
    {
        Assert.Equal(Contrast.Ratio("#000000", "#FFFFFF"), Contrast.Ratio("#FFFFFF", "#000000"), 6);
        Assert.Equal(21.0, Contrast.Ratio("#000000", "#FFFFFF"), 2);
        Assert.Equal(1.0, Contrast.Ratio("#777777", "#777777"), 6);
    }

    [Fact]
    public void ThemeManager_AccentInk_Follows_The_Measured_Palette()
    {
        Assert.Equal(LightAccentInk, ThemeManager.LightAccentInk);
        Assert.Equal(DarkAccentInk, ThemeManager.DarkAccentInk);
    }
}
