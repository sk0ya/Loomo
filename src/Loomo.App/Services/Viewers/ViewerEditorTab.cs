using System.Windows.Automation;
using Editor.Core.Buffer;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// パスから開いた、見せ方の決まっていないテキストのタブ（§44.2「パスから開く」）。エディタ（Diff と同じ、LSP なしの部品）に
/// <b>読むだけ</b>で出す。上の帯から、エディタペインで開いて直す・Claude Code に見せ方を作ってもらう、へ進める。
/// ファイルが書き換わったら読み直す（読んでいた位置は残す）。
/// </summary>
internal sealed class ViewerEditorTab : ViewerTabBase
{
    private readonly Func<VimEditorControl> _createEditor;
    private readonly Action<string> _openInEditor;
    private readonly SingleFileWatcher _fileWatcher;
    private DockPanel? _root;
    private VimEditorControl? _editor;
    private bool _stale = true;
    private int _loadSeq;

    public ViewerEditorTab(string targetPath, Func<VimEditorControl> createEditor, Action<string> openInEditor,
        Panel host, Dispatcher dispatcher)
        : base(targetPath, host, dispatcher)
    {
        _createEditor = createEditor;
        _openInEditor = openInEditor;
        _fileWatcher = new SingleFileWatcher(changedPath =>
        {
            _stale = true;
            if (CanShow)
                _ = LoadAsync(keepView: true);
        }, post: action => dispatcher.BeginInvoke(action));
        _fileWatcher.Watch(targetPath);
        SetStatus(ViewerTabStatus.Running);
    }

    public override bool CanGenerate => true;
    public override string ToolTip => TargetPath + "\nテキストとして表示（読むだけ）";
    public override UIElement? BuildCard() => null;

    public override Task RefreshAsync(bool reload = false)
    {
        if (reload)
            _stale = true;
        OnShownChanged();
        return Task.CompletedTask;
    }

    public override void Focus() => _editor?.Focus();

    protected override void OnShownChanged()
    {
        if (!CanShow)
        {
            if (_root is not null)
                _root.Visibility = Visibility.Collapsed;
            return;
        }
        if (_root is null)
            Build();
        _root!.Visibility = Visibility.Visible;
        if (_stale)
            _ = LoadAsync(keepView: false);
    }

    private void Build()
    {
        var label = ViewerCards.Line("テキストとして表示（読むだけ）", "Fs11", "FgDim", top: 0);
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;   // 狭いペインではボタンを優先して省く（全文はツールチップ）
        label.ToolTip = "見せ方の決まっていないファイルを、テキストとして読むだけで表示しています";
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(8, 0, 8, 0);
        var edit = ViewerCards.Button("エディタで開く", "SecondaryButton", () => _openInEditor(TargetPath!), "ViewerTextOpenInEditor");
        var generate = ViewerCards.Button("Claude Code に見せ方を作ってもらう", "SecondaryButton",
            RequestGenerate, "ViewerTextGenerate");
        foreach (var button in new[] { edit, generate })
        {
            button.Margin = new Thickness(0, 2, 6, 2);
            button.SetResourceReference(FrameworkElement.StyleProperty, "PaneHeaderGhostButton");
        }
        var bar = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(generate, Dock.Right);
        DockPanel.SetDock(edit, Dock.Right);
        bar.Children.Add(generate);
        bar.Children.Add(edit);
        bar.Children.Add(label);
        var band = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        band.SetResourceReference(Border.BorderBrushProperty, "Border");
        band.SetResourceReference(Border.BackgroundProperty, "Panel");
        DockPanel.SetDock(band, Dock.Top);

        _editor = _createEditor();
        _editor.IsReadOnly = true;
        AutomationProperties.SetName(_editor, ViewerTargets.NameOf(TargetPath!));
        _root = new DockPanel { LastChildFill = true };
        _root.Children.Add(band);
        _root.Children.Add(_editor);
        Host.Children.Add(_root);
    }

    private async Task LoadAsync(bool keepView)
    {
        if (_editor is not { } editor)
            return;
        var seq = ++_loadSeq;
        _stale = false;
        var path = TargetPath!;
        PreparedFileLoad? prepared;
        try { prepared = await Task.Run(() => PreparedFileLoad.Prepare(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { prepared = null; }
        if (seq != _loadSeq || Disposed || !ReferenceEquals(_editor, editor) || prepared is null)
            return;
        var caret = editor.Caret;
        var (vertical, horizontal) = (editor.VerticalOffset, editor.HorizontalOffset);
        editor.IsReadOnly = false;
        editor.LoadFile(path, prepared);
        editor.IsReadOnly = true;
        if (keepView)
        {
            editor.NavigateTo(caret.Line, caret.Column);
            editor.ScrollToOffset(vertical, horizontal);
        }
    }

    protected override void Unmount()
    {
        ++_loadSeq;
        _stale = true;
        if (_root is null)
            return;
        Host.Children.Remove(_root);
        _editor?.Dispose();
        _editor = null;
        _root = null;
    }

    public override void Dispose()
    {
        _fileWatcher.Dispose();
        base.Dispose();
    }
}
