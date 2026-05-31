using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace sk0ya.Loomo.App.Converters;

/// <summary>文字列が空のとき Visible（ウォーターマーク表示用）。</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
