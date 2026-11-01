using System.Windows;
using System.Windows.Media;

namespace SwiftGet.App.Themes;

/// <summary>
/// Peta segmen: satu batang per bagian unduhan, tersusun sesuai urutan byte.
/// <para>
/// Menjawab pertanyaan yang tidak bisa dijawab persen (`DESIGN.md` bagian 8):
/// bagian mana yang tertinggal. Segmen selesai memakai aksen, segmen berjalan
/// dinaikkan kontrasnya dan diberi tanda, segmen menunggu memakai warna rel.
/// </para>
/// </summary>
public sealed class SegmentMap : FrameworkElement
{
    private IReadOnlyList<SegmentView> _segments = Array.Empty<SegmentView>();

    public static readonly DependencyProperty SegmentsSourceProperty =
        DependencyProperty.Register(
            nameof(SegmentsSource),
            typeof(IEnumerable<SegmentView>),
            typeof(SegmentMap),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnSegmentsChanged));

    public IEnumerable<SegmentView>? SegmentsSource
    {
        get => (IEnumerable<SegmentView>?)GetValue(SegmentsSourceProperty);
        set => SetValue(SegmentsSourceProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (SegmentMap)d;
        map._segments = e.NewValue is IEnumerable<SegmentView> items ? items.ToArray() : Array.Empty<SegmentView>();
        map.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        if (_segments.Count == 0)
        {
            // Belum ada segmen (fase periksa): satu batang penuh berwarna rel
            // dengan tanda, supaya ruang tidak tampak rusak.
            Brush empty = FindBrush("TrackBrush", Color.FromRgb(0xE6, 0xE6, 0xDF));
            dc.DrawRoundedRectangle(empty, null, new Rect(0, 0, width, height), 2, 2);
            return;
        }

        Brush done = FindBrush("AccentBrush", Color.FromRgb(0x0F, 0x6B, 0x5C));
        Brush pending = FindBrush("TrackBrush", Color.FromRgb(0xE6, 0xE6, 0xDF));
        Brush running = FindBrush("WarningBrush", Color.FromRgb(0x8A, 0x5B, 0x00));

        const double gap = 2;
        double slot = (width - gap * (_segments.Count - 1)) / _segments.Count;
        if (slot <= 0)
        {
            slot = width / _segments.Count;
        }

        for (int i = 0; i < _segments.Count; i++)
        {
            SegmentView segment = _segments[i];
            double x = i * (slot + gap);

            var full = new Rect(x, 0, slot, height);

            // Latar selalu rel: panjang batang tetap sama, jadi mata bisa
            // membandingkan bagian satu dengan lainnya.
            dc.DrawRoundedRectangle(pending, null, full, 2, 2);

            if (segment.Fraction > 0)
            {
                double fillWidth = Math.Max(1, slot * segment.Fraction);
                var fill = new Rect(x, 0, fillWidth, height);
                Brush brush = segment.IsComplete ? done : running;
                dc.DrawRoundedRectangle(brush, null, fill, 2, 2);
            }
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

/// <summary>
/// Satu batang di <see cref="SegmentMap"/>. Nilainya potret sesaat, bukan
/// referensi hidup ke segmen di engine.
/// </summary>
public sealed class SegmentView
{
    public SegmentView(int index, double fraction, bool isComplete)
    {
        Index = index;
        Fraction = Math.Clamp(fraction, 0, 1);
        IsComplete = isComplete;
    }

    public int Index { get; }

    public double Fraction { get; }

    public bool IsComplete { get; }

    /// <summary>Deskripsi untuk pembaca layar dan tooltip.</summary>
    public string Description => IsComplete
        ? $"Bagian {Index + 1}, selesai"
        : $"Bagian {Index + 1}, {Fraction * 100:F0} persen";
}
