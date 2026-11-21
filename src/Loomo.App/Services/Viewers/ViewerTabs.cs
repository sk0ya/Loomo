using System.Windows.Automation;
using sk0ya.Loomo.Core.Viewers;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Viewer ペインのタブ 1 つ（§44.2）。置き場の Viewer（<see cref="ViewerTabHost"/>）と、パスから開いたもの
/// （HTML の頁・EditorSupport の表示・プログラムの窓・作っている途中）が同じ形で並ぶ。中身はペインの同じ場所
/// （<c>host</c> の Panel）に重ねて置き、前に出ているタブだけが見える。ページを出せないときは <see cref="BuildCard"/>。
/// </summary>
internal interface IViewerTab : IDisposable
{
    /// <summary>TABS の行と結ぶ鍵（起動ごとに振り直す）。</summary>
    Guid TabId { get; }

    /// <summary>開いているタブの並びに保存する鍵：置き場の Viewer は id、パスから開いたものは <see cref="ViewerPath.KeyOf"/>。</summary>
    string Key { get; }

    string Title { get; }
    string ToolTip { get; }

    /// <summary>パスから開いたタブなら、そのパス。</summary>
    string? TargetPath { get; }

    ViewerTabStatus Status { get; }

    /// <summary>見え方（題・状態・カード）が変わった。</summary>
    event EventHandler? Changed;

    /// <summary>タブの中から「閉じる」を押した（許しのカードの「開かない」など）。</summary>
    event EventHandler? CloseRequested;

    /// <summary>出せるなら出す。<paramref name="reload"/> は中身が変わったとき。</summary>
    Task RefreshAsync(bool reload = false);

    /// <summary>題だけ先に読む（復元したタブ。前に出るまで中身を作らない）。</summary>
    Task PeekAsync();

    void SetVisible(bool visible);
    void Focus();

    /// <summary>別の入れ物（ペイン ⇄ 切り離した窓）へ移す。中身は捨てて、移った先で見えたときに作り直す。</summary>
    void Rehost(Panel host);

    /// <summary>中身の代わりに出すカード。中身が出ているなら null。</summary>
    UIElement? BuildCard();
}

/// <summary>Viewer ペインのカード（許し・壊れた・空・作っている途中…）を組む部品。字の大きさと色は部屋のトークンを引く。</summary>
internal static class ViewerCards
{
    public static StackPanel Panel() => new()
    {
        Margin = new Thickness(16, 14, 16, 14),
        MaxWidth = 640,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public static TextBlock Line(string text, string size, string color, bool bold = false, double top = 2)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 0),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        block.SetResourceReference(TextBlock.FontSizeProperty, size);
        block.SetResourceReference(TextBlock.ForegroundProperty, color);
        return block;
    }

    public static TextBlock Mono(string text, string size = "Fs12", string color = "Fg", double top = 2)
    {
        var block = Line(text, size, color, top: top);
        block.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        return block;
    }

    public static Button Button(string content, string style, Action onClick, string? automationId = null)
    {
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 8, 0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        button.Click += (_, _) => onClick();
        if (automationId is not null)
            AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    public static StackPanel Buttons(params UIElement[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }
}
