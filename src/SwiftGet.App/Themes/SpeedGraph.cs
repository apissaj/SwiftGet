using System.Windows;
using System.Windows.Media;

namespace SwiftGet.App.Themes;

/// <summary>
/// Sparkline kecepatan unduhan.
/// <para>
/// Menjawab satu pertanyaan (`DESIGN.md` bagian 9): apakah bandwidth terpakai
/// penuh, dan trennya naik atau turun. Karena itu bentuknya garis dengan area
/// tipis di bawahnya, bukan batang. Angka persisnya ada di teks sebelah grafik,
/// jadi grafik ini tidak menggambar sumbu maupun label.
/// </para>
/// </summary>
public sealed class SpeedGraph : FrameworkElement
{
    private IReadOnlyList<double> _values = Array.Empty<double>();

    public static readonly DependencyProperty ValuesSourceProperty =
        DependencyProperty.Register(
            nameof(ValuesSource),
            typeof(IEnumerable<double>),
            typeof(SpeedGraph),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnValuesChanged));

    /// <summary>Garis dasar selalu digambar, termasuk saat tidak ada transfer.</summary>
    public static readonly DependencyProperty ShowBaselineProperty =
        DependencyProperty.Register(
            nameof(ShowBaseline),
            typeof(bool),
            typeof(SpeedGraph),
            new PropertyMetadata(true, OnVisualChanged));

    public IEnumerable<double>? ValuesSource
    {
        get => (IEnumerable<double>?)GetValue(ValuesSourceProperty);
        set => SetValue(ValuesSourceProperty, value);
    }

    public bool ShowBaseline
    {
        get => (bool)GetValue(ShowBaselineProperty);
        set => SetValue(ShowBaselineProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SpeedGraph)d).InvalidateVisual();

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (SpeedGraph)d;

        graph._values = e.NewValue is IEnumerable<double> values
            ? values.ToArray()
            : Array.Empty<double>();

        // Ikuti perubahan koleksi supaya grafik bergerak tanpa perlu repaint manual.
        if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged previous)
            previous.CollectionChanged -= graph.OnCollectionChanged;
        if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged current)
            current.CollectionChanged += graph.OnCollectionChanged;

        graph.InvalidateVisual();
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
        if (width <= 1 || height <= 1) return;

        double padding = 3;
        double plotWidth = width - padding * 2;
        double plotHeight = height - padding * 2;
        if (plotWidth <= 1 || plotHeight <= 1) return;

        Brush accent = FindBrush("AccentBrush", Color.FromRgb(0x0F, 0x6B, 0x5C));
        Brush gridLine = FindBrush("GridLineBrush", Color.FromRgb(0xEF, 0xEF, 0xEA));

        bool hasData = _values.Count > 0 && _values.Any(v => v > 0);

        if (!hasData)
        {
            // Garis dasar datar di posisi nol. Kotak kosong terbaca sebagai
            // grafik rusak, garis dasar terbaca sebagai "tidak ada transfer".
            if (ShowBaseline)
            {
                var baselinePen = new Pen(gridLine, 1);
                baselinePen.Freeze();
                double y = height - padding;
                dc.DrawLine(baselinePen, new Point(padding, y), new Point(width - padding, y));
            }
            return;
        }

        double max = _values.Max();
        if (max <= 0) max = 1;

        int count = _values.Count;
        double stepX = count > 1 ? plotWidth / (count - 1) : 0;

        // Sumbu waktu selalu memakai lebar penuh: data terbaru di kanan.
        // Slot kosong di kiri (saat riwayat belum penuh) tidak digambar,
        // supaya garis mulai dari titik pertama yang ada.
        var points = new List<Point>(count);
        for (int i = 0; i < count; i++)
        {
            double x = padding + i * stepX;
            double normalized = Math.Clamp(_values[i] / max, 0, 1);
            double y = padding + plotHeight * (1 - normalized);
            points.Add(new Point(x, y));
        }

        if (points.Count >= 2)
        {
            // Area di bawah garis: memberi bobot supaya garis tipis tidak hilang.
            var areaBrush = accent is SolidColorBrush solid
                ? new SolidColorBrush(solid.Color) { Opacity = 0.12 }
                : accent;
            areaBrush.Freeze();

            var area = new StreamGeometry();
            using (StreamGeometryContext ctx = area.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, height - padding), true, true);
                foreach (Point p in points) ctx.LineTo(p, true, false);
                ctx.LineTo(new Point(points[^1].X, height - padding), true, false);
            }
            area.Freeze();
            dc.DrawGeometry(areaBrush, null, area);

            var linePen = new Pen(accent, 1.6) { LineJoin = PenLineJoin.Round };
            linePen.Freeze();

            var line = new StreamGeometry();
            using (StreamGeometryContext ctx = line.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                for (int i = 1; i < points.Count; i++) ctx.LineTo(points[i], true, false);
            }
            line.Freeze();
            dc.DrawGeometry(null, linePen, line);
        }

        // Titik "sekarang" menandai ujung data yang masih hidup.
        Point last = points[^1];
        dc.DrawEllipse(accent, null, last, 2.4, 2.4);
    }

    private static Brush FindBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush) return brush;

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
