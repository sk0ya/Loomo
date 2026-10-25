using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ターミナルのタブに付ける色（§24.63・Fork 2.68 のタブの色）。「本番につないでいるタブ」「Claude Code を回しているタブ」を、
/// 見出しの文字を読まずに見分けるための印。保存するのは鍵（<see cref="Choice.Key"/>）だけで、色そのものは持たない——
/// 選べる色を後から調整しても、保存済みのタブがそれに付いてくるように。明暗どちらのテーマでも読める中間の彩度に揃える。
/// </summary>
internal static class TerminalTabColors
{
    internal sealed record Choice(string Key, string Label, Color Color);

    public static IReadOnlyList<Choice> All { get; } =
    [
        new("red", "赤", Color.FromRgb(0xE5, 0x53, 0x4B)),
        new("orange", "橙", Color.FromRgb(0xE0, 0x82, 0x3D)),
        new("yellow", "黄", Color.FromRgb(0xD4, 0xA7, 0x2C)),
        new("green", "緑", Color.FromRgb(0x57, 0xAB, 0x5A)),
        new("blue", "青", Color.FromRgb(0x53, 0x9B, 0xF5)),
        new("purple", "紫", Color.FromRgb(0xB0, 0x83, 0xF0)),
    ];

    private static readonly Dictionary<string, Brush> Brushes = All.ToDictionary(
        c => c.Key, c => (Brush)Freeze(new SolidColorBrush(c.Color)), StringComparer.Ordinal);

    /// <summary>知っている鍵ならそのまま、知らない鍵（手で書き換えた設定・消した色）と空は null（色なし）。</summary>
    public static string? Normalize(string? key)
        => key is not null && Brushes.ContainsKey(key) ? key : null;

    public static Brush? BrushFor(string? key)
        => key is not null && Brushes.TryGetValue(key, out var brush) ? brush : null;

    /// <summary>「色」の子メニューを作り直す（TABS とペインの見出しの右クリックで同じものを出す）。</summary>
    public static void FillMenu(MenuItem menu, string? current, Action<string?> apply)
    {
        menu.Items.Clear();
        foreach (var choice in All)
        {
            var key = choice.Key;
            var item = new MenuItem
            {
                Header = choice.Label,
                IsCheckable = true,
                IsChecked = key == current,
                Icon = new Ellipse { Width = 10, Height = 10, Fill = BrushFor(key) },
            };
            item.Click += (_, _) => apply(key);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var none = new MenuItem { Header = "色なし", IsEnabled = current is not null };
        none.Click += (_, _) => apply(null);
        menu.Items.Add(none);
    }

    private static T Freeze<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
