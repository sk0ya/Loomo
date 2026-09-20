using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタ右クリックの「この位置の説明を表示」（hover）。
///
/// <para>ライブラリのネイティブ項目と置き換わる項目（<c>ShellWindow.EditorNativeMenu</c>）。
/// ネイティブ側は hover の本文を<b>ステータスバーへ先頭 1 行だけ</b>流していたため、
/// Markdown で返すサーバー（Roslyn LSP など）では実測で <c>```csharp</c> という
/// コードフェンスだけが出ていた——説明が 1 文字も読めない。ここでは
/// <see cref="HoverDisplayText"/> で素のテキストに直し、右クリックした位置に
/// ポップアップで出す。本文は読み取り専用の <see cref="TextBox"/> なので、
/// シグネチャをそのままコピーできる。</para>
///
/// <para>本文の出どころはネイティブと同じ二段構え——言語サーバーの
/// <c>textDocument/hover</c> が空なら、C# は Roslyn の意味モデル
/// （<see cref="CSharpHoverService"/>）へ落ちる。</para></summary>
public partial class ShellWindow
{
    /// <summary>キャレット行と重ならないよう、右クリック位置から少し下へずらす量。</summary>
    private const double HoverPopupCaretGap = 18;

    private Popup? _hoverPopup;
    private TextBox? _hoverPopupText;
    /// <summary>取得中に別の位置で開き直されたとき、古い応答を出さないための番兵。</summary>
    private object? _hoverToken;

    private MenuItem BuildHoverInfoMenuItem(VimEditorControl control, Point anchor)
    {
        var item = new MenuItem
        {
            Header = EditorMenuLabels.HoverInfo,
            // ネイティブ項目と同じキー（Vim の K）。綴りは Vim のものなので訳さない。
            InputGestureText = "K",
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(item, "EditorHoverInfo");
        System.Windows.Automation.AutomationProperties.SetName(item, EditorMenuLabels.HoverInfo);
        item.Click += (_, _) => _ = ShowHoverInfoAsync(control, anchor);
        return item;
    }

    private async Task ShowHoverInfoAsync(VimEditorControl control, Point anchor)
    {
        var token = new object();
        _hoverToken = token;

        // 右クリックでキャレットはその位置へ移っている（選択の内側なら選択の位置のまま）。
        var caret = control.Caret;
        var text = await RequestHoverTextAsync(control, caret.Line, caret.Column);
        if (!ReferenceEquals(_hoverToken, token)) return;

        // 「説明が無い」ことも同じ場所に出す。ここを黙って終わらせると、
        // 押しても何も起きない項目に戻ってしまう。
        ShowHoverPopup(control, anchor, HoverDisplayText.Plain(text) ?? "この位置に説明はありません。");
    }

    private async Task<string?> RequestHoverTextAsync(VimEditorControl control, int line, int character)
    {
        if (control.LspDocument is { IsConnected: true } document)
        {
            try
            {
                if (await document.RequestHoverAsync(line, character) is { Value: { } value } &&
                    !string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch (OperationCanceledException) { return null; }
            catch { /* サーバーが応えないだけ。下の Roslyn へ落とす。 */ }
        }

        if (control.FilePath is not { Length: > 0 } path) return null;
        try
        {
            return await RequestCSharpHoverFallbackAsync(
                path, control.Text, line, character, CancellationToken.None);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            ShowRefactorStatus($"説明を取得できませんでした: {ex.Message}");
            return null;
        }
    }

    /// <summary>LSP が hover を返さないときの C# フォールバック。
    /// <c>BuildEditorControl</c> の <c>HostHoverProvider</c> もここを通る（同じ答えにする）。</summary>
    private Task<string?> RequestCSharpHoverFallbackAsync(
        string path, string source, int line, int character, CancellationToken cancellationToken)
    {
        if (!IsCSharpFallbackTarget(path)) return Task.FromResult<string?>(null);

        var openTexts = FindOpenCSharpEditorTexts();
        return Task.Run(() => CSharpHoverService.Get(
            _solutionModel?.Current, path, source, line, character, openTexts), cancellationToken);
    }

    private void ShowHoverPopup(VimEditorControl control, Point anchor, string text)
    {
        _hoverPopup ??= CreateHoverPopup();
        _hoverPopupText!.Text = text;
        _hoverPopup.PlacementTarget = control;
        _hoverPopup.Placement = PlacementMode.Relative;
        _hoverPopup.HorizontalOffset = Math.Max(0, anchor.X);
        _hoverPopup.VerticalOffset = Math.Max(0, anchor.Y) + HoverPopupCaretGap;
        // 開いたまま位置だけ変えても追従しないので、開き直す。
        _hoverPopup.IsOpen = false;
        _hoverPopup.IsOpen = true;
        _hoverPopupText.Focus();
        _hoverPopupText.CaretIndex = 0;
    }

    private Popup CreateHoverPopup()
    {
        _hoverPopupText = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
        };
        _hoverPopupText.SetResourceReference(ForegroundProperty, "Fg");
        _hoverPopupText.SetResourceReference(FontSizeProperty, "Fs12");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_hoverPopupText, "EditorHoverText");

        var scroll = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _hoverPopupText,
        };
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 7, 10, 7),
            Child = scroll,
        };
        border.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");

        var popup = new Popup
        {
            StaysOpen = false,
            AllowsTransparency = true,
            Focusable = true,
            Child = border,
        };
        // Escape は<b>トンネル段</b>で取る。WPF の TextBox は Escape を Undo として扱い、
        // そのコマンドはポップアップの外——最後はエディタ——まで上がってしまう
        // （実測で本文を出しただけなのに「1 change undone」が出た）。
        popup.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            popup.IsOpen = false;
            e.Handled = true;
        };
        return popup;
    }
}
