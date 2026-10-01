using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Roxa.Converters;

public class FlagToImageConverter : IValueConverter
{
    public static readonly FlagToImageConverter Instance = new();

    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static Bitmap? _fallback;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string code = value?.ToString()?.Trim().ToLowerInvariant() ?? "";
        if (code.Length != 2) return GetFallback();

        if (Cache.TryGetValue(code, out var cached)) return cached;

        try
        {
            var uri = new Uri($"avares://Roxa/Assets/Flags/{code}.png");
            var stream = AssetLoader.Open(uri);
            var bmp = new Bitmap(stream);
            Cache[code] = bmp;
            return bmp;
        }
        catch
        {
            return GetFallback();
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Bitmap GetFallback()
    {
        if (_fallback != null) return _fallback;
        try
        {
            var uri = new Uri("avares://Roxa/Assets/Flags/_unknown.png");
            _fallback = new Bitmap(AssetLoader.Open(uri));
        }
        catch
        {
            _fallback = null;
        }
        return _fallback!;
    }
}
