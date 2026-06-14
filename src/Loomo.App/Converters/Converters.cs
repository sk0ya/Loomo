using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Converters;

/// <summary>Git の状態文字（M/A/D/R/?/U…）を一般的な配色のブラシに変換する。
/// VS Code のソース管理に倣ったセマンティック色（テーマ非依存）。</summary>
public sealed class GitStatusToBrushConverter : IValueConverter
{
    private static readonly Brush Modified = Freeze("#E2C08D"); // 変更：ゴールド
    private static readonly Brush Added = Freeze("#73C991");    // 追加・未追跡：緑
    private static readonly Brush Deleted = Freeze("#E57373");  // 削除・コンフリクト：赤
    private static readonly Brush Renamed = Freeze("#6CB6FF");  // リネーム：青
    private static readonly Brush Other = Freeze("#9DA5B4");    // その他：灰

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) switch
        {
            "M" => Modified,
            "A" or "?" or "C" => Added,
            "D" or "U" => Deleted,
            "R" => Renamed,
            _ => Other,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>文字列が空のとき Visible（ウォーターマーク表示用）。</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>列挙値が ConverterParameter（名前）と一致するとき Visible。</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>列挙値が ConverterParameter（名前）と一致するとき true。RadioButton 双方向バインド用。</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is string name
            ? Enum.Parse(targetType, name)
            : Binding.DoNothing;
}

/// <summary>コレクション件数が 0 より大きいとき Visible（空セクションを畳む用）。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>真偽を反転する（IsEnabled の「〜中は無効化」用）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>true のとき Collapsed（false で Visible）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
