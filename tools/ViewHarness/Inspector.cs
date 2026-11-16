using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ViewHarness;

/// <summary>描いた画面から、これまで何度も人に指摘された種類の欠けを機械的に拾う。
/// 拾えるのは「見た目の事故」だけで、手順の分かりにくさや用語の悪さは拾えない（そこはスキルの手順で見る）。</summary>
public static class Inspector
{
    public sealed record Finding(string Kind, string Message, Rect Bounds);

    public static List<Finding> Inspect(FrameworkElement root)
    {
        var findings = new List<Finding>();
        foreach (var el in Descendants(root))
        {
            if (el is not FrameworkElement fe || !fe.IsVisible || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                continue;
            Rect bounds;
            try { bounds = fe.TransformToAncestor(root).TransformBounds(new Rect(fe.RenderSize)); }
            catch (InvalidOperationException) { continue; }

            switch (fe)
            {
                case TextBlock tb: CheckTextBlock(tb, bounds, root, findings); break;
                case TextBox box: CheckTextBox(box, bounds, findings); break;
                case ButtonBase btn: CheckButton(btn, bounds, findings); break;
            }

            if (fe is Control or TextBlock)
                CheckRawFontSize(fe, bounds, findings);

            // 窓（描画範囲）からのはみ出し。スクロールの中は除く。
            if (fe is TextBlock or Control && FindAncestor<ScrollViewer>(fe, root) is null
                && (bounds.Right > root.ActualWidth + 1 || bounds.Bottom > root.ActualHeight + 1))
                findings.Add(new("はみ出し", $"描画範囲の外へ出ている: {Describe(fe)}", bounds));
        }
        return findings
            .GroupBy(f => (f.Kind, f.Message))
            .Select(g => g.First())
            .ToList();
    }

    private static void CheckTextBlock(TextBlock tb, Rect bounds, FrameworkElement root, List<Finding> findings)
    {
        var text = tb.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (tb.TextWrapping == TextWrapping.NoWrap)
        {
            var width = MeasureText(text, tb) + tb.Padding.Left + tb.Padding.Right;
            if (width > tb.ActualWidth + 1)
            {
                var how = tb.TextTrimming == TextTrimming.None ? "切れている" : "「…」で省略されている";
                findings.Add(new("文字の欠け", $"{how}（要 {width:0} / 実 {tb.ActualWidth:0}）: 「{Short(text)}」", bounds));
            }
        }
        else if (tb.ActualHeight + 1 < tb.DesiredSize.Height - tb.Margin.Top - tb.Margin.Bottom)
        {
            findings.Add(new("文字の欠け", $"折り返した行の下が切れている: 「{Short(text)}」", bounds));
        }

        // 親（どの階層でも）に切られている：固定の高さのボタン・狭い列・ClipToBounds の枠など。
        // 自分の矩形のうち実際に見えている部分を、祖先の切り取りと重ねて求める。
        var visible = VisibleRect(tb, root);
        if (visible.IsEmpty)
            findings.Add(new("文字の欠け", $"枠の外にあって見えない: 「{Short(text)}」", bounds));
        // 上下は行の余白（行間）の分だけ削れても字は欠けないので、2px かつ 1 割を超えたときだけ言う。
        else if (visible.Width + 1 < bounds.Width || bounds.Height - visible.Height > Math.Max(2, bounds.Height * 0.1))
        {
            var side = visible.Width + 1 < bounds.Width ? "左右" : "上下";
            findings.Add(new("文字の欠け", $"置かれた枠に{side}を切られている（見えるのは {visible.Width:0}x{visible.Height:0} / {bounds.Width:0}x{bounds.Height:0}）: 「{Short(text)}」", bounds));
        }

        var fg = (tb.Foreground as SolidColorBrush)?.Color;
        var bg = EffectiveBackground(tb);
        if (fg is { } f && bg is { } b)
        {
            // 本文は 4.5・控えめな文字は 3.0 が目安だが、どちらの役の文字かはここでは分からない。
            // 控えめな文字にも足りない 3.0 未満だけを言う（それ以上は「手で見る」側）。
            var ratio = Contrast(Blend(f, b), b);
            if (ratio < 3.0)
                findings.Add(new("読みにくい", $"文字と背景のコントラスト {ratio:0.0}（3.0 未満）: 「{Short(text)}」", bounds));
        }
    }

    private static void CheckTextBox(TextBox box, Rect bounds, List<Finding> findings)
    {
        if (string.IsNullOrEmpty(box.Text) || box.TextWrapping != TextWrapping.NoWrap) return;
        var width = MeasureText(box.Text, box);
        if (box.ViewportWidth > 0 && width > box.ViewportWidth + 1)
            findings.Add(new("文字の欠け", $"入力欄の値が欄に収まらない（要 {width:0} / 欄 {box.ViewportWidth:0}）: 「{Short(box.Text)}」", bounds));
    }

    private static void CheckButton(ButtonBase btn, Rect bounds, List<Finding> findings)
    {
        // テンプレートが内部で作るボタン（コンボの開閉・スクロールバー等）は対象外。
        if (btn.TemplatedParent is not null) return;

        if (btn.Style is null && btn is not RadioButton && btn is not CheckBox)
            findings.Add(new("スタイル漏れ", $"既定のスタイルのまま（FlatButton / SecondaryButton 等を当てる）: {Describe(btn)}", bounds));

        var hasText = btn.Content is string s ? !string.IsNullOrWhiteSpace(s) : Descendants(btn).OfType<TextBlock>().Any(t => t.Text.Any(char.IsLetterOrDigit));
        if (!hasText && btn.ToolTip is null && string.IsNullOrEmpty(AutomationProperties.GetName(btn)))
            findings.Add(new("名前なし", $"文字もツールチップも無いボタン（何をするか分からない）: {Describe(btn)}", bounds));
    }

    private static void CheckRawFontSize(FrameworkElement fe, Rect bounds, List<Finding> findings)
    {
        var src = DependencyPropertyHelper.GetValueSource(fe, TextElement.FontSizeProperty);
        if (src.BaseValueSource == BaseValueSource.Local && !src.IsExpression)
            findings.Add(new("生の文字サイズ", $"FontSize が数値の直書き（DynamicResource Fs* にする。文字サイズ設定に追随しない）: {Describe(fe)}", bounds));
    }

    /// <summary>要素の矩形のうち、祖先の切り取り（レイアウトの切り取り・ClipToBounds・Clip）を通って見えている部分（root 座標）。</summary>
    private static Rect VisibleRect(FrameworkElement fe, FrameworkElement root)
    {
        var rect = fe.TransformToAncestor(root).TransformBounds(new Rect(fe.RenderSize));
        DependencyObject? cur = fe;
        while (cur is not null && cur != root)
        {
            if (cur is UIElement ui)
            {
                var clips = new List<Geometry?> { ui.Clip, ui is FrameworkElement f ? LayoutInformation.GetLayoutClip(f) : null };
                if (ui.ClipToBounds) clips.Add(new RectangleGeometry(new Rect(ui.RenderSize)));
                foreach (var g in clips)
                {
                    if (g is null || g.Bounds.IsEmpty) continue;
                    var b = ((Visual)ui).TransformToAncestor(root).TransformBounds(g.Bounds);
                    rect.Intersect(b);
                    if (rect.IsEmpty) return rect;
                }
            }
            cur = VisualTreeHelper.GetParent(cur);
        }
        return rect;
    }

    private static double MeasureText(string text, FrameworkElement fe)
    {
        var tf = new Typeface(TextElement.GetFontFamily(fe), TextElement.GetFontStyle(fe), TextElement.GetFontWeight(fe), TextElement.GetFontStretch(fe));
        var line = text.Split('\n').MaxBy(l => l.Length) ?? text;
        return new FormattedText(line, CultureInfo.CurrentUICulture, fe.FlowDirection, tf,
            TextElement.GetFontSize(fe), Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
    }

    private static Color? EffectiveBackground(DependencyObject d)
    {
        for (var cur = VisualTreeHelper.GetParent(d); cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            var brush = cur switch
            {
                Panel p => p.Background,
                Border b => b.Background,
                // Control.Background はテンプレートが使わないことがある（中の Border で塗る）ので見ない。
                _ => null,
            };
            if (brush is SolidColorBrush sc && sc.Color.A >= 230) return sc.Color;
        }
        return null;
    }

    private static Color Blend(Color fg, Color bg)
    {
        var a = fg.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(Mix(fg.R, bg.R), Mix(fg.G, bg.G), Mix(fg.B, bg.B));
    }

    public static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (l1, l2) = (L(a), L(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static string Describe(FrameworkElement fe)
    {
        var name = string.IsNullOrEmpty(fe.Name) ? "" : $"#{fe.Name} ";
        var text = fe switch
        {
            ContentControl { Content: string s } => $"「{Short(s)}」",
            ContentControl cc => Descendants(cc).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) is { } t ? $"「{Short(t)}」" : "",
            TextBlock tb => $"「{Short(tb.Text)}」",
            _ => "",
        };
        return $"{fe.GetType().Name} {name}{text}".Trim();
    }

    private static string Short(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= 40 ? s : s[..40] + "…";
    }

    private static T? FindAncestor<T>(DependencyObject d, DependencyObject stop) where T : DependencyObject
    {
        for (var cur = VisualTreeHelper.GetParent(d); cur is not null && cur != stop; cur = VisualTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            yield return cur;
            var n = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(cur) : 0;
            for (var i = n - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(cur, i));
        }
    }
}
