using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: エディタの「使用箇所表示（Find References / gr）」の結果を受けて一覧表示する。 エディタコントロールは LSP に問い合わせて参照を計算するが、結果は自身では描画せず <see cref="VimEditorControl.FindReferencesResult"/> イベントを発火するだけなので、ホスト側で ポップアップに一覧を出し、クリックで該当ファイル・行へジャンプさせる。 同じイベントは grep / 診断一覧 / コール・型ヒエラルキー / ワークスペースシンボルの結果にも使われる。 ※「まず配線だけ最小実装」段階：機能は通っているが見た目は最小。後でドッキングパネル等へ移す。</summary>
public partial class ShellWindow {
    private void OnEditorFindReferencesResult(object? sender, FindReferencesResultEventArgs e) {
        BuildReferencesPopup(e.Items, $"{e.TitlePrefix} ({e.Items.Count}) — {e.SymbolName}");
        // 出どころ（イベントを発火したエディタビュー）に紐づけて置く。sender は分割・切り離しを問わず
        // 「いま操作しているビュー」そのものなので、_activeEditorTab より正確。
        PlaceReferencesPopup(sender as FrameworkElement);
        ReferencesPopup.IsOpen = true;
    }
    /// <summary>
    /// ポップアップを <paramref name="source"/>（呼び出し元のエディタビュー）に紐づけて配置する。
    /// 基準ビューが取れない場合（ワークスペース診断でエディタを1枚も開いていない等）は
    /// ペイン領域そのものを基準にする＝同じ計算で「領域の下端」に出る（従来の中央固定ではない）。
    /// </summary>
    private void PlaceReferencesPopup(FrameworkElement? source) {
        var target = source is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } ? source : PaneHost;
        var host = Window.GetWindow(target);
        if (host is null || target.ActualWidth <= 0 || target.ActualHeight <= 0) {
            // 参照できる矩形が無い：従来どおりペイン領域の中央へ（配置の失敗で一覧を出さないのは論外）。
            ReferencesPopup.PlacementTarget = PaneHost;
            ReferencesPopup.Placement = PlacementMode.Center;
            ReferencesPopup.HorizontalOffset = 0;
            ReferencesPopup.VerticalOffset = 0;
            return;
        }
        var origin = target.TransformToAncestor(host).Transform(new Point(0, 0));
        var targetRect = new Rect(origin, new Size(target.ActualWidth, target.ActualHeight));
        var windowRect = new Rect(0, 0, host.ActualWidth, host.ActualHeight);
        var offset = ReferencesPopupPlacement.OffsetFrom(
            MeasureReferencesPopup(targetRect), targetRect, windowRect);

        ReferencesPopup.PlacementTarget = target;
        ReferencesPopup.Placement = PlacementMode.Relative;
        ReferencesPopup.HorizontalOffset = offset.X;
        ReferencesPopup.VerticalOffset = offset.Y;
    }
    /// <summary>
    /// 開く前のポップアップ実寸。中身（件数）で高さが変わるので、その都度測る。
    /// 測る前に**幅の上限を基準ビューに合わせて詰める**（詰めないと省略記号付きの長い行で必ず
    /// 上限 760 に張り付き、狭い分割では反対側を覆う）。
    /// </summary>
    private Size MeasureReferencesPopup(Rect targetRect) {
        if (ReferencesPopup.Child is not FrameworkElement child)
            return ReferencesPopupFallbackSize;
        child.MaxWidth = ReferencesPopupPlacement.MaxWidthIn(
            targetRect, ReferencesPopupPreferredMaxWidth, ReferencesPopupFallbackSize.Width);
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = child.DesiredSize;
        return size.Width > 0 && size.Height > 0 ? size : ReferencesPopupFallbackSize;
    }
    /// <summary>XAML の <c>MaxWidth</c> と同じ「広げてよい上限」。ビューが狭ければここから詰める。</summary>
    private const double ReferencesPopupPreferredMaxWidth = 760;
    /// <summary>測定できなかったときの見積り。幅は XAML の <c>MinWidth</c>＝一覧として読める下限。</summary>
    private static readonly Size ReferencesPopupFallbackSize = new(460, 380);
    private void BuildReferencesPopup(IReadOnlyList<FindReferenceItem> items, string title) {
        ReferencesPopupTitle.Text = title;
        ReferencesPopupList.Children.Clear();
        ReferencesPopupPeek.Visibility = Visibility.Collapsed;
        if (items.Count == 0) {
            ReferencesPopupList.Children.Add(new TextBlock {
                Text = "使用箇所が見つかりませんでした", FontSize = UiFontManager.Scaled(12), Margin = new Thickness(10, 6, 10, 6), Foreground = (Brush)FindResource("FgDim"), });
            return;
        }
        foreach (var item in items) {
            var captured = item;
            var display = NavigationLocationFormatter.Resolve(
                captured.FilePath, _workspace.Folders, _solutionModel?.Current);
            var location = display.Format(captured.Line, captured.Col);
            var preview = captured.Preview ?? ReadSourceLine(captured.FilePath, captured.Line);
            var content = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            content.Inlines.Add(new System.Windows.Documents.Run(location) {
                Foreground = (Brush)FindResource("Accent"), });
            if (!string.IsNullOrWhiteSpace(preview))
                content.Inlines.Add(new System.Windows.Documents.Run("   " + preview) {
                    Foreground = (Brush)FindResource("FgDim"), });
            var row = new Button {
                Style = (Style)FindResource("BranchMenuItem"), FontSize = UiFontManager.Scaled(12), ToolTip = $"{captured.FilePath}:{captured.Line + 1}:{captured.Col + 1}", Content = content, };
            row.Click += (_, _) => {
                ReferencesPopup.IsOpen = false;
                if (Uri.TryCreate(captured.FilePath, UriKind.Absolute, out var uri) && !uri.IsFile)
                    _ = OpenUrlInBrowserAsync(uri.AbsoluteUri, "外部ソース");
                else
                    _ = OpenPathInEditorAsync(captured.FilePath, captured.Line + 1, captured.Col + 1);
            };
            row.MouseEnter += (_, _) => ShowReferencePeek(captured);
            ReferencesPopupList.Children.Add(row);
        }
        ShowReferencePeek(items[0]);
    }

    private void ShowReferencePeek(FindReferenceItem item)
    {
        var display = NavigationLocationFormatter.Resolve(
            item.FilePath, _workspace.Folders, _solutionModel?.Current);
        // 定義Peekでは、同一バッファの未保存行をディスク上の内容より優先する。
        var context = string.IsNullOrWhiteSpace(item.Preview)
            ? NavigationSourceContext.Read(item.FilePath, item.Line)
            : item.Preview;
        ReferencesPopupPeek.Text = string.IsNullOrWhiteSpace(context)
            ? $"プレビュー: {display.Format(item.Line, item.Col)}\n（ソースを読み取れません）"
            : $"プレビュー: {display.Format(item.Line, item.Col)}\n{context}";
        ReferencesPopupPeek.Visibility = Visibility.Visible;
    }
    private static string ReadSourceLine(string filePath, int line) {
        try {
            using var reader = new StreamReader(filePath);
            for (var i = 0; i < line; i++)
                if (reader.ReadLine() == null) return "";
            return (reader.ReadLine() ?? "").Trim();
        } catch { return ""; }
    }
}
