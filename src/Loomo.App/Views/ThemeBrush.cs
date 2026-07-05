using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 添付プロパティ：バインドした<b>リソースキー</b>から <see cref="TextBlock.Foreground"/> を
/// テーマ追従で設定する。DataTemplate 内で要素ごとに色キーが変わる箇所（コード構造アウトラインの
/// 種別グリフなど、<c>DynamicResource</c> のリテラルキーが書けないケース）に使う。内部で
/// <c>SetResourceReference</c> を張るので、テーマ切替（パレット差し替え）で色が即時追従する
/// （静的ブラシをバインドする従来方式は切替時に更新されなかった）。
/// </summary>
public static class ThemeBrush
{
    /// <summary>Foreground に張るパレットのリソースキー（例: "SymType"）。空なら既定へ戻す。</summary>
    public static readonly DependencyProperty ForegroundKeyProperty =
        DependencyProperty.RegisterAttached(
            "ForegroundKey", typeof(string), typeof(ThemeBrush),
            new PropertyMetadata(null, OnForegroundKeyChanged));

    public static string? GetForegroundKey(DependencyObject d) => (string?)d.GetValue(ForegroundKeyProperty);
    public static void SetForegroundKey(DependencyObject d, string? value) => d.SetValue(ForegroundKeyProperty, value);

    private static void OnForegroundKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;

        var key = e.NewValue as string;
        if (string.IsNullOrEmpty(key))
            fe.ClearValue(TextBlock.ForegroundProperty);
        else
            fe.SetResourceReference(TextBlock.ForegroundProperty, key);
    }
}
