using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SwiftGet.Core.Downloads;

namespace SwiftGet.App.Infrastructure;

/// <summary>Pecahan 0..1 menjadi persen 0..100 untuk progress bar.</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double fraction = value is double d ? d : 0;
        return Math.Clamp(fraction * 100d, 0d, 100d);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Bool menjadi visibilitas (true = Visible).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Angka 0 menjadi Visible (untuk empty state).</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value is int i ? i : 0;
        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Status unduhan menjadi warna teks.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is DownloadState state
            ? state switch
            {
                DownloadState.Completed => "SuccessBrush",
                DownloadState.Failed => "DangerBrush",
                DownloadState.Downloading => "AccentBrush",
                DownloadState.Probing => "WarningBrush",
                DownloadState.Paused => "WarningBrush",
                DownloadState.Queued => "TextSecondaryBrush",
                DownloadState.Canceled => "TextSecondaryBrush",
                _ => "TextSecondaryBrush",
            }
            : "TextSecondaryBrush";

        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;

        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

