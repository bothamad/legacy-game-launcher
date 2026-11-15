using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LegacyGameLauncher.Services;

/// <summary>Reads a game's own icon from its exe (largest size available up to the requested one), cached.</summary>
public static class GameIcons
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHDefExtractIconW(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint sizes);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint n);

    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string exePath, int size = 48)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        return Cache.GetOrAdd($"{exePath}|{size}", _ => Load(exePath, size));
    }

    static ImageSource? Load(string exe, int size)
    {
        try
        {
            if (!File.Exists(exe) || ExtractIconExW(exe, -1, null, null, 0) == 0) return null; // exe has no icon of its own
            if (SHDefExtractIconW(exe, 0, 0, out var large, out var small, (uint)((16 << 16) | size)) != 0 || large == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(large); if (small != IntPtr.Zero) DestroyIcon(small); }
        }
        catch { return null; }
    }
}

/// <summary>XAML: exe path → icon (ConverterParameter = pixel size).</summary>
public sealed class ExeIconConverter : IValueConverter
{
    public object? Convert(object value, Type t, object parameter, CultureInfo c) =>
        GameIcons.Get(value as string ?? "", int.TryParse(parameter as string, out var s) ? s : 48);
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>XAML: exe path → Visible when the exe has NO icon (to show the fallback glyph).</summary>
public sealed class NoIconVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        GameIcons.Get(value as string ?? "") == null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => throw new NotSupportedException();
}
