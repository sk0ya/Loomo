namespace sk0ya.Loomo.App.Views;

internal sealed class CommandPaletteViewController
{
    private readonly ListBox _list;
    private readonly Border _previewHost;
    private readonly ColumnDefinition _previewColumn;
    private readonly FrameworkElement _box;
    private readonly TextBox _input;
    private readonly ShellAppearanceCoordinator _appearance;
    private readonly IReadOnlyDictionary<PaletteMode, Button> _modeButtons;
    private CancellationTokenSource? _previewCts;
    private VimEditorControl? _previewEditor;

    public CommandPaletteViewController(ListBox list, Border previewHost, ColumnDefinition previewColumn,
        FrameworkElement box, TextBox input, ShellAppearanceCoordinator appearance,
        IReadOnlyDictionary<PaletteMode, Button> modeButtons)
    {
        _list = list; _previewHost = previewHost; _previewColumn = previewColumn;
        _box = box; _input = input; _appearance = appearance; _modeButtons = modeButtons;
    }

    public void Cancel() => _previewCts?.Cancel();
    public void RefreshAppearance() { if (_previewEditor is not null) _appearance.ApplyEditorAppearance(_previewEditor); }
    public void SetMode(PaletteMode mode)
    {
        var (_, query) = CommandPaletteService.Parse(_input.Text);
        _input.Text = CommandPaletteService.Prefix(mode) + query;
        _input.CaretIndex = _input.Text.Length;
        _input.Focus();
    }
    public void CycleMode() { var (mode, _) = CommandPaletteService.Parse(_input.Text); SetMode(CommandPaletteService.Next(mode)); }

    public void UpdateMode(PaletteMode mode)
    {
        foreach (var (itemMode, button) in _modeButtons)
        {
            if (itemMode == mode)
            {
                button.SetResourceReference(Control.BorderBrushProperty, "Accent");
                button.SetResourceReference(Control.ForegroundProperty, "Fg");
            }
            else
            {
                button.BorderBrush = Brushes.Transparent;
                button.SetResourceReference(Control.ForegroundProperty, "FgDim");
            }
        }
    }

    public void UpdateSize(double width, double height, bool previewShown)
    {
        if (width <= 0 || height <= 0) return;
        _box.Width = Math.Clamp(width * 0.72, 760, 1600);
        var tall = Math.Max(440, height * 0.82);
        _box.MaxHeight = tall;
        _box.Height = previewShown ? tall : double.NaN;
    }
    public void SetPreviewVisible(bool visible)
        => _previewColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    public void ShowItems(IReadOnlyList<PaletteCommand> items, string query)
    {
        foreach (var item in items) item.TitleMatch = query;
        _list.ItemsSource = items;
        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
            _list.ScrollIntoView(_list.SelectedItem);
        }
        else UpdatePreview(null);
    }
    public void MoveSelection(int delta)
    {
        var count = _list.Items.Count;
        if (count == 0) return;
        _list.SelectedIndex = ((_list.SelectedIndex < 0 ? 0 : _list.SelectedIndex) + delta + count) % count;
        _list.ScrollIntoView(_list.SelectedItem);
    }

    public void UpdatePreview(PaletteCommand? command)
    {
        _previewCts?.Cancel();
        if (command?.PreviewPath is not { } path || !File.Exists(path))
        {
            if (_previewEditor is not null) _previewEditor.Visibility = Visibility.Collapsed;
            return;
        }
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = ShowPreviewAsync(command, path, cts.Token);
    }

    private VimEditorControl EnsurePreviewEditor()
    {
        if (_previewEditor is { } existing) return existing;
        var editor = new VimEditorControl(new VimEditorControlOptions()) { VimEnabled = false, Focusable = false };
        _appearance.ApplyEditorOptions(editor); _appearance.ApplyEditorAppearance(editor);
        editor.ExecuteCommand("set number"); editor.ExecuteCommand("set cursorline"); editor.ExecuteCommand("set nominimap");
        editor.SetSharedStatusBar(new VimStatusBar()); _previewHost.Child = editor;
        return _previewEditor = editor;
    }
    private async Task ShowPreviewAsync(PaletteCommand command, string path, CancellationToken ct)
    {
        try { await Task.Delay(60, ct); } catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
        var editor = EnsurePreviewEditor(); editor.Visibility = Visibility.Visible;
        try
        {
            editor.LoadFile(path); editor.HighlightSearch(command.PreviewHighlight ?? ""); Navigate(editor, command);
            _ = editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!ct.IsCancellationRequested && editor.Visibility == Visibility.Visible) Navigate(editor, command);
            });
        }
        catch { editor.Visibility = Visibility.Collapsed; }
    }
    private static void Navigate(VimEditorControl editor, PaletteCommand command)
    {
        if (command.PreviewLine > 0) editor.JumpToLine(command.PreviewLine - 1, 0);
        else editor.NavigateTo(0, 0);
    }
}
