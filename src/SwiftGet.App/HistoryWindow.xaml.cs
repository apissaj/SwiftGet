using System.Collections.ObjectModel;
using System.Windows;
using SwiftGet.App.Infrastructure;

namespace SwiftGet.App;

/// <summary>
/// Daftar unduhan yang selesai antar sesi. Hanya menampilkan dan membersihkan;
/// tidak ada logika unduhan di sini. Penyimpanannya ada di
/// <see cref="HistoryStore"/>.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryStore _store;
    private readonly ObservableCollection<HistoryRow> _rows = [];

    public HistoryWindow(HistoryStore store)
    {
        InitializeComponent();
        _store = store;
        HistoryList.ItemsSource = _rows;
        Reload();
    }

    private void Reload()
    {
        _rows.Clear();
        foreach (HistoryEntry entry in _store.Load())
            _rows.Add(new HistoryRow(entry));

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = _rows.Count > 0;
        SummaryText.Text = _rows.Count == 0
            ? "Unduhan yang selesai dicatat di sini."
            : $"{_rows.Count} unduhan selesai tercatat.";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _store.Clear();
        Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Baris tampilan untuk satu entri; memformat sekali saat dibangun.</summary>
    private sealed class HistoryRow
    {
        public HistoryRow(HistoryEntry entry)
        {
            FileName = entry.FileName;
            Category = entry.Category;
            SizeText = entry.TotalBytes is long bytes ? Humanize.Bytes(bytes) : "—";
            CompletedText = entry.CompletedUtc.ToLocalTime().ToString("d MMM yyyy HH:mm");
        }

        public string FileName { get; }
        public string Category { get; }
        public string SizeText { get; }
        public string CompletedText { get; }
    }
}
