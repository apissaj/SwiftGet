using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwiftGet.App.Themes;

/// <summary>
/// Panel kecepatan total: grafik batang sederhana yang digambar langsung di
/// <see cref="OnRender"/>. Tidak memakai library chart supaya tetap ringan.
/// </summary>
public sealed class SpeedGraph : FrameworkElement
{
    private IReadOnlyList<double> _values = Array.Empty<double>();

    public static readonly DependencyProperty ValuesSourceProperty =
        DependencyProperty.Register(
            nameof(ValuesSource),
            typeof(IEnumerable<double>),
            typeof(SpeedGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (SpeedGraph)d;
        graph._values = e.NewValue is IEnumerable<double> values
            ? values.ToArray()
            : Array.Empty<double>();

        // Ikuti perubahan koleksi (ObservableCollection) agar grafik bergerak.
        if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged oldIncc)
            oldIncc.CollectionChanged -= graph.OnCollectionChanged;
        if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged newIncc)
            newIncc.CollectionChanged += graph.OnCollectionChanged;

        graph.InvalidateVisual();
    }

    public IEnumerable<double>? ValuesSource
    {
        get => (IEnumerable<double>?)GetValue(ValuesSourceProperty);
        set => SetValue(ValuesSourceProperty, value);
    }

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _values = sender is IEnumerable<double> values ? values.ToArray() : Array.Empty<double>();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Latar transparan agar mengikuti warna surface induk.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        if (_values.Count == 0) return;

        double max = _values.Max();
        if (max <= 0)
        {
            // Garis dasar tipis saat tidak ada transfer.
            var baseline = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 160)), 1);
            baseline.Freeze();
            dc.DrawLine(baseline, new Point(0, height - 1), new Point(width, height - 1));
            return;
        }

        double slot = width / _values.Count;
        double barWidth = Math.Max(1.5, slot - 1.5);

        var brush = FindBrush("AccentBrush", Color.FromRgb(0x4C, 0x8D, 0xFF));

        for (int i = 0; i < _values.Count; i++)
        {
            double value = _values[i];
            if (value <= 0) continue;

            double barHeight = Math.Max(1.5, (value / max) * (height - 2));
            double x = i * slot;
            double y = height - barHeight;

            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, barHeight), 1.5, 1.5);
        }
    }

    private static Brush FindBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}


