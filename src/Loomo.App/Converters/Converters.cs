using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Converters;

/// <summary>true（または 0 より大きい件数）のとき指定の星倍率（既定 2*）、それ以外は高さ 0 の
/// <see cref="GridLength"/> を返す。実行ログ領域のように「あるときだけ行を確保し、無いときは畳んで
/// 他の行へ高さを譲る」用途に使う。</summary>
public sealed class BoolToStarLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isActive = value switch
        {
            bool b => b,
            int n => n > 0,
            _ => false,
        };
        if (!isActive) return new GridLength(0);
        var factor = 2.0;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            factor = f;
        return new GridLength(factor, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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

/// <summary>文字列が空でないとき Visible。</summary>
public sealed class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

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

/// <summary>真偽を、選択状態を表す Tag 値（true→"active"／false→null）へ変換する。
/// セグメントボタン等で「選択中なら Tag="active" のトリガで強調表示」に使う。</summary>
public sealed class ActiveTagConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "active" : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>2つの値が一致するとき "active" を返す。リスト内の現在項目表示に使う。</summary>
public sealed class EqualityActiveTagConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length >= 2 && Equals(values[0], values[1]) ? "active" : null;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>すべて true のとき Visible。</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length > 0 && values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
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

/// <summary>'/' 区切りパスの末尾セグメントだけを返す（無ければそのまま）。フォルダー補完候補のように
/// 「入力済みのディレクトリ部分＋名前」を蓄積した文字列から、直近の名前だけを表示したい用途向け。</summary>
public sealed class LastPathSegmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0)
            return value ?? string.Empty;
        var trimmed = s.Replace('\\', '/').TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>日時を「今日→時刻のみ／今年→月日／それ以外→年月日」の簡潔な表記へ変換する
/// （AIセッション一覧など、カード内で日時を目立たせすぎない用途向け）。</summary>
public sealed class RelativeDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return string.Empty;
        var now = DateTime.Now;
        if (dt.Date == now.Date) return dt.ToString("HH:mm", culture);
        if (dt.Date == now.Date.AddDays(-1)) return "昨日 " + dt.ToString("HH:mm", culture);
        return dt.Year == now.Year ? dt.ToString("M/d", culture) : dt.ToString("yyyy/M/d", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
