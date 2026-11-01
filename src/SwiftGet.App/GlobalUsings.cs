// Alias global: proyek ini mengaktifkan WPF dan WinForms sekaligus (WinForms
// hanya untuk NotifyIcon), sehingga beberapa nama tipe bertabrakan. Alias di
// sini membuat semua file memakai tipe WPF secara default.
global using Application = System.Windows.Application;
global using Clipboard = System.Windows.Clipboard;
global using MessageBox = System.Windows.MessageBox;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using Pen = System.Windows.Media.Pen;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using FontFamily = System.Windows.Media.FontFamily;

// System.IO tidak diimpor otomatis saat UseWindowsForms aktif.
global using File = System.IO.File;
global using FileStream = System.IO.FileStream;
global using Directory = System.IO.Directory;
global using Path = System.IO.Path;
global using IOException = System.IO.IOException;
global using Stream = System.IO.Stream;
global using MemoryStream = System.IO.MemoryStream;
global using FileAccess = System.IO.FileAccess;
global using FileMode = System.IO.FileMode;
global using FileShare = System.IO.FileShare;
global using FileOptions = System.IO.FileOptions;
global using SearchOption = System.IO.SearchOption;
