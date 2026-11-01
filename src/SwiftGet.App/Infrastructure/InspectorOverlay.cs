using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SwiftGet.App.Infrastructure;

/// <summary>
/// Mengurung fokus papan ketik di dalam satu panel.
/// <para>
/// Dipakai saat panel periksa menjadi lapisan penuh di atas daftar pada
/// jendela sempit. Tanpa kurungan, Tab membawa fokus ke kontrol di belakang
/// lapisan yang tidak terlihat: pengguna mengetik ke tempat yang salah tanpa
/// tahu. Escape menutup panel, sesuai kebiasaan dialog Windows.
/// </para>
/// </summary>
public sealed class FocusTrap
{
    private readonly Window _owner;
    private readonly FrameworkElement _panel;
    private readonly Action _close;
    private bool _armed;
    private IInputElement? _returnFocus;

    public FocusTrap(Window owner, FrameworkElement panel, Action close)
    {
        _owner = owner;
        _panel = panel;
        _close = close;
        _owner.PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// Nyalakan kurungan fokus dan pindahkan fokus ke kontrol pertama panel.
    /// </summary>
    public void Arm()
    {
        if (_armed) return;
        _armed = true;
        _returnFocus = Keyboard.FocusedElement;

        // Harus menunggu tata letak: panel baru saja diubah kolomnya.
        _owner.Dispatcher.BeginInvoke(new Action(FocusFirst), DispatcherPriority.Input);
    }

    /// <summary>
    /// Matikan kurungan dan kembalikan fokus ke kontrol yang memicu panel.
    /// </summary>
    public void Disarm()
    {
        if (!_armed) return;
        _armed = false;

        IInputElement? target = _returnFocus;
        _returnFocus = null;

        if (target is null) return;

        _owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (target is UIElement element && element.IsVisible && element.IsEnabled)
            {
                Keyboard.Focus(element);
            }
        }), DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_armed) return;

        if (e.Key == Key.Escape)
        {
            _close();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Tab) return;

        List<UIElement> order = new();
        Collect(_panel, order);
        if (order.Count == 0) return;

        int current = order.FindIndex(i => ReferenceEquals(i, Keyboard.FocusedElement));
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
        int next = current < 0 ? 0 : (current + step + order.Count) % order.Count;

        order[next].Focus();
        e.Handled = true;
    }

    private void FocusFirst()
    {
        List<UIElement> order = new();
        Collect(_panel, order);
        if (order.Count > 0) order[0].Focus();
    }

    /// <summary>
    /// Kumpulkan kontrol yang bisa difokus dalam urutan kedalaman visual,
    /// melewati yang tersembunyi atau tidak aktif.
    /// </summary>
    private static void Collect(DependencyObject root, List<UIElement> results)
    {
        if (root is UIElement element &&
            element.Focusable &&
            element.IsEnabled &&
            element.IsVisible &&
            element.Visibility == Visibility.Visible)
        {
            results.Add(element);
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            Collect(VisualTreeHelper.GetChild(root, i), results);
        }
    }
}
