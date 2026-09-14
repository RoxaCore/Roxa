using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace XrayClient.Converters;

public class ActiveToBrushConverter : IValueConverter
{
    public static readonly ActiveToBrushConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.Parse("#E8F5F1")) : new SolidColorBrush(Colors.White);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class ActiveToBorderConverter : IValueConverter
{
    public static readonly ActiveToBorderConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.Parse("#0E7C6B")) : new SolidColorBrush(Color.Parse("#ECECEC"));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}