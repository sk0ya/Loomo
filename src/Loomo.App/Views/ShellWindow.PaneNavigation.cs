
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン操作（Ctrl+W プレフィックス：h/j/k/l フォーカス移動・リサイズモード・ズーム）</summary>
public partial class ShellWindow {

    private void OnPaneNavKey(object sender, KeyEventArgs e) {
        if (e.Key == Key.ImeProcessed)
            return;

        if (IsPaletteOpen) {
            if (e.Key == Key.Escape) {
                CloseCommandPalette(refocus: true);
                e.Handled = true;
            } else if (MatchesPaletteOpenGesture(e)) {
                CyclePaletteMode();
                e.Handled = true;
            }
            return;
        }

        _keyboard?.HandlePreviewKeyDown(e);
    }

    private bool MatchesPaletteOpenGesture(KeyEventArgs e)
        => _keybindings.For("palette.open") is { Count: 1 } seq
           && sk0ya.Loomo.App.Input.KeyChord.FromEvent(e) is { } chord
           && chord.Equals(seq.First);

    private const double ResizeStepRatio = 0.08;

    private void ResizeFocusedPane(DropZone direction) {
        if (_zoomedPane is not null || _focusedRegion is not { } region)
            return;

        BeginTrailLayoutChange();

        var horizontal = direction is DropZone.Left or DropZone.Right;
        var grow = direction is DropZone.Right or DropZone.Below;

        if (region.IsSidebar) {
            if (!horizontal || !_vm.IsSidebarVisible)
                return;
            var width = SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : SidebarColumn.Width.Value;
            SidebarColumn.Width = new GridLength(Math.Max(SidebarColumn.MinWidth, width + (grow ? 24 : -24)));
            return;
        }

        if (region.Pane is not { } kind || FindLeaf(kind) is not { Hidden: false } leaf)
            return;

        var wantOrientation = horizontal ? SplitKind.Columns : SplitKind.Rows;
        CaptureLayoutSizes();
        if (FindAncestorSplit(leaf, wantOrientation) is not { } target)
            return;
        var (split, child) = target;

        var total = split.Children.Sum(c => c.Weight > 0 ? c.Weight : 1);
        var step = total * ResizeStepRatio;
        var min = total * 0.1; // 1ペインを潰し切らないための下限
        var current = child.Weight > 0 ? child.Weight : 1;
        child.Weight = Math.Max(min, current + (grow ? step : -step));

        _suppressResizeExit = true;
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
        FocusPane(kind);
        Dispatcher.BeginInvoke(new Action(() => _suppressResizeExit = false), DispatcherPriority.Input);
    }

    private (PaneSplit Split, PaneNode Child)? FindAncestorSplit(PaneNode leaf, SplitKind orientation) {
        var node = leaf;
        for (var parent = FindParent(node); parent is not null; parent = FindParent(node)) {
            if (parent.Orientation == orientation)
                return (parent, node);
            node = parent;
        }
        return null;
    }

    private void SetResizeMode(bool on) {
        if (_resizeMode == on)
            return;
        _resizeMode = on;
        if (on) {
            EnsureResizeHint();
            PositionResizeHint();
            _resizeHintPopup!.IsOpen = true;
        }
        else if (_resizeHintPopup is not null) {
            _resizeHintPopup.IsOpen = false;
        }
    }

    private void EnsureResizeHint() {
        if (_resizeHintPopup is not null)
            return;

        var banner = new Border {
            Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 6, 12, 6), Child = new TextBlock {
                Text = "リサイズモード　h/j/k/l で伸縮　・　Esc で終了", Foreground = (Brush)FindResource("Fg"), FontSize = UiFontManager.Scaled(12)
            }
        };
        _resizeHintPopup = new Popup {
            PlacementTarget = PaneHost, Placement = PlacementMode.Relative, AllowsTransparency = true, StaysOpen = true, Child = banner
        };
    }

    private void PositionResizeHint() {
        if (_resizeHintPopup is null)
            return;
        const double estimatedWidth = 340;
        _resizeHintPopup.HorizontalOffset = Math.Max(8, (PaneHost.ActualWidth - estimatedWidth) / 2);
        _resizeHintPopup.VerticalOffset = Math.Max(8, PaneHost.ActualHeight - 48);
    }

    private void OnSetInTerminalRequested(object? sender, TerminalSetRequest request) {
        SetPaneVisible(PaneKind.Terminal, true);

        if (request.IsDirectory) {
            _terminal.SetWorkingDirectory(request.FullPath);
        } else {
            var path = request.FullPath;
            var text = path.IndexOf(' ') >= 0 ? $"\"{path}\"" : path;
            _activeTerminalTab?.View.SendTerminalInput(text);
        }

        FocusPane(PaneKind.Terminal);
    }
}
