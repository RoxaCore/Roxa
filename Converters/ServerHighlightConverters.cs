// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Roxa.Converters;

public class ServerBgConverter : IMultiValueConverter
{
    public static readonly ServerBgConverter Instance = new();
    private static readonly SolidColorBrush HighlightedBrush = new(Color.Parse("#D4EDDA"));
    private static readonly SolidColorBrush ActiveBrush = new(Color.Parse("#E8F5E9"));
    private static readonly SolidColorBrush SelectedBrush = new(Color.Parse("#E0F2FE"));
    private static readonly SolidColorBrush DefaultBrush = new(Colors.Transparent);

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isHighlighted = values.Count > 1 && values[1] is true;
        if (isHighlighted) return HighlightedBrush;
        bool isActive = values.Count > 0 && values[0] is true;
        if (isActive) return ActiveBrush;
        bool isSelected = values.Count > 2 && values[2] is true;
        if (isSelected) return SelectedBrush;
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
