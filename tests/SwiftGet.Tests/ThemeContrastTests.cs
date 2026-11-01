using SwiftGet.App.Infrastructure;
using Xunit;

namespace SwiftGet.Tests;

/// <summary>
/// Mengunci pasangan warna tema ke ambang WCAG AA. Bila palet diubah dan
/// kontras turun, test ini gagal sebelum perubahan itu sampai ke pengguna.
/// </summary>
public sealed class ThemeContrastTests
{
    // Mode terang
    private const string LightSurface = "#FFFFFF";
    private const string LightAppBackground = "#F5F6F8";
    private const string LightTextPrimary = "#1A1A20";
    private const string LightTextSecondary = "#6B6B76";
    private const string LightAccent = "#2D6CDF";
    private const string LightSuccess = "#0F7A46";
    private const string LightDanger = "#C42B2B";
    private const string LightWarning = "#8A5B00";

    // Mode gelap
    private const string DarkSurface = "#26262B";
    private const string DarkAppBackground = "#1E1E22";
    private const string DarkTextPrimary = "#F0F0F4";
    private const string DarkTextSecondary = "#A0A0AC";
    private const string DarkAccent = "#366FE0";
    private const string DarkSuccess = "#3DD68C";
    private const string DarkDanger = "#FF6B6B";
    private const string DarkWarning = "#FFB84D";

    [Theory]
    [InlineData(LightTextPrimary, LightSurface)]
    [InlineData(LightTextPrimary, LightAppBackground)]
    [InlineData(LightTextSecondary, LightSurface)]
    [InlineData(LightTextSecondary, LightAppBackground)]
    [InlineData(LightSuccess, LightSurface)]
    [InlineData(LightSuccess, LightAppBackground)]
    [InlineData(LightDanger, LightSurface)]
    [InlineData(LightDanger, LightAppBackground)]
    [InlineData(LightWarning, LightSurface)]
    [InlineData(LightWarning, LightAppBackground)]
    public void LightMode_Text_Passes_AA(string foreground, string background)
    {
        double ratio = Contrast.Ratio(foreground, background);
        Assert.True(ratio >= Contrast.NormalTextMinimum,
            $"Kontras {foreground} di atas {background} hanya {ratio:F2}:1, minimum 4,5:1.");
    }

    [Theory]
    [InlineData(DarkTextPrimary, DarkSurface)]
    [InlineData(DarkTextPrimary, DarkAppBackground)]
    [InlineData(DarkTextSecondary, DarkSurface)]
    [InlineData(DarkTextSecondary, DarkAppBackground)]
    [InlineData(DarkSuccess, DarkSurface)]
    [InlineData(DarkSuccess, DarkAppBackground)]
    [InlineData(DarkDanger, DarkSurface)]
    [InlineData(DarkDanger, DarkAppBackground)]
    [InlineData(DarkWarning, DarkSurface)]
    [InlineData(DarkWarning, DarkAppBackground)]
    public void DarkMode_Text_Passes_AA(string foreground, string background)
    {
        double ratio = Contrast.Ratio(foreground, background);
        Assert.True(ratio >= Contrast.NormalTextMinimum,
            $"Kontras {foreground} di atas {background} hanya {ratio:F2}:1, minimum 4,5:1.");
    }

    [Fact]
    public void Button_Label_Passes_AA_In_Both_Themes()
    {
        // Putih di atas aksen: ini teks normal, bukan teks besar.
        Assert.True(Contrast.Passes("#FFFFFF", LightAccent),
            $"Putih di atas aksen terang: {Contrast.Ratio("#FFFFFF", LightAccent):F2}:1");
        Assert.True(Contrast.Passes("#FFFFFF", DarkAccent),
            $"Putih di atas aksen gelap: {Contrast.Ratio("#FFFFFF", DarkAccent):F2}:1");
    }

    [Fact]
    public void Accent_Is_Visible_Against_Surface()
    {
        // Aksen dipakai juga sebagai ikon/garis: butuh 3:1 terhadap latar.
        Assert.True(Contrast.Passes(LightAccent, LightSurface, Contrast.LargeTextMinimum));
        Assert.True(Contrast.Passes(DarkAccent, DarkSurface, Contrast.LargeTextMinimum));
    }

    [Fact]
    public void Ratio_Is_Order_Independent_And_Extremes_Are_Correct()
    {
        Assert.Equal(Contrast.Ratio("#000000", "#FFFFFF"), Contrast.Ratio("#FFFFFF", "#000000"), 6);
        Assert.Equal(21.0, Contrast.Ratio("#000000", "#FFFFFF"), 2);
        Assert.Equal(1.0, Contrast.Ratio("#777777", "#777777"), 6);
    }

    [Fact]
    public void Unrounded_Ratio_Does_Not_Round_Up_To_A_Pass()
    {
        // 4,49 tidak boleh dianggap lolos hanya karena mendekati 4,5.
        double ratio = Contrast.Ratio(LightTextSecondary, LightSurface);
        Assert.True(ratio >= Contrast.NormalTextMinimum);
    }
}
