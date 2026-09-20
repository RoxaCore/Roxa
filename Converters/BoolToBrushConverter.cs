using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace XrayClient.Converters;

public class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    private static readonly Dictionary<string, (SolidColorBrush True, SolidColorBrush False)> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string colors)
        {
            if (!Cache.TryGetValue(colors, out var cached))
            {
                var parts = colors.Split(',');
                if (parts.Length == 2)
                {
                    cached = (new SolidColorBrush(Color.Parse(parts[0].Trim())), new SolidColorBrush(Color.Parse(parts[1].Trim())));
                    Cache[colors] = cached;
                }
            }
            if (Cache.TryGetValue(colors, out cached))
                return b ? cached.True : cached.False;
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
