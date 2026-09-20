using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace XrayClient.Converters;

public class ServerBgConverter : IMultiValueConverter
{
    public static readonly ServerBgConverter Instance = new();
    private static readonly SolidColorBrush HighlightedBrush = new(Color.Parse("#D4EDDA"));
    private static readonly SolidColorBrush ActiveBrush = new(Color.Parse("#E8F5E9"));
    private static readonly SolidColorBrush DefaultBrush = new(Colors.Transparent);

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isHighlighted = values.Count > 1 && values[1] is true;
        if (isHighlighted) return HighlightedBrush;
        bool isActive = values.Count > 0 && values[0] is true;
        if (isActive) return ActiveBrush;
        return DefaultBrush;
    }
}

public class ServerBorderConverter : IMultiValueConverter
{
    public static readonly ServerBorderConverter Instance = new();
    private static readonly SolidColorBrush ActiveBrush = new(Color.Parse("#22C55E"));
    private static readonly SolidColorBrush DefaultBrush = new(Color.Parse("#E1E8E6"));

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isActive = values.Count > 0 && values[0] is true;
        bool isHighlighted = values.Count > 1 && values[1] is true;
        if (isActive || isHighlighted) return ActiveBrush;
        return DefaultBrush;
    }
}
