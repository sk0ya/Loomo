
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン操作（Ctrl+W プレフィックス：h/j/k/l フォーカス移動・リサイズモード・ズーム）</summary>
public partial class ShellWindow
{
    // ===== ペイン操作（Ctrl+W → h/j/k/l 移動 / Shift+h/j/k/l リサイズ / z ズーム） =====

    // ウィンドウ全体のキー入力（Preview＝トンネル）を受け、コマンドパレット以外は KeyboardDispatcher へ委ねる。バインドの解釈（Ctrl+W プレフィックス連鎖・ h/j/k/l 方向移動・リサイズモード・z/x/v/s/q 等）はすべてデータ駆動で、設定画面での 再割り当てが即反映される。パレット表示中は Esc の保険だけここで拾う。
    private void OnPaneNavKey(object sender, KeyEventArgs e)
    {
        // IME 合成中（変換確定の Enter・候補選択の矢印・Esc 等）は WPF が Key.ImeProcessed で届ける。
        // この間はシェルのキー処理を一切行わず、フォーカス中のエディタ／IME に委ねる。さもないと
        // 合成キーまで毎打 KeyboardResolver にかける無駄処理になり、さらにプレフィックス待ち・
        // リサイズモード中は確定 Enter を奪って「確定文字が入らない」「キャレット位置がずれる」原因になる。
        if (e.Key == Key.ImeProcessed)
            return;

        if (IsPaletteOpen)
        {
            if (e.Key == Key.Escape)
            {
                CloseCommandPalette(refocus: true);
                e.Handled = true;
            }
            // 開いたまま「パレットを開く」ジェスチャ（既定 Ctrl+Shift+P）をもう一度押すとモード巡回。
            else if (MatchesPaletteOpenGesture(e))
            {
                CyclePaletteMode();
                e.Handled = true;
            }
            return;
        }

        _keyboard?.HandlePreviewKeyDown(e);
    }

    // このキーイベントが「コマンドパレットを開く」単一ジェスチャ（既定 Ctrl+Shift+P）か。 再割り当てに追従する。プレフィックス連鎖（Ctrl+W P）は単発イベントでは判定しない。
    private bool MatchesPaletteOpenGesture(KeyEventArgs e)
        => _keybindings.For("palette.open") is { Count: 1 } seq
           && sk0ya.Loomo.App.Input.KeyChord.FromEvent(e) is { } chord
           && chord.Equals(seq.First);

    // 1回のキーリサイズで動かす量（その分割の合計比率に対する割合）。
    private const double ResizeStepRatio = 0.08;

    // フォーカス中ペインを指定方向へリサイズする（L=広く / H=狭く / J=高く / K=低く）。 方向の軸に一致する最も近い祖先スプリットを探し、フォーカスペイン側の子の比率を増減する。 軸に合うスプリットが無い（その方向に分割が無い）場合は何もしない。
    private void ResizeFocusedPane(DropZone direction)
    {
        if (_zoomedPane is not null || _focusedRegion is not { } region)
            return;

        BeginTrailLayoutChange();

        var horizontal = direction is DropZone.Left or DropZone.Right;
        var grow = direction is DropZone.Right or DropZone.Below;

        // サイドバーは Grid 列なので幅を直接増減する（縦方向のリサイズ対象は持たない）。
        if (region.IsSidebar)
        {
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

        // 再構築＋再フォーカスが起こすフォーカス移動でリサイズモードを抜けないようガードする。
        // 端末等の非同期フォーカスが流れ切るまで保持したいので、解除は Input 優先度で遅延させる。
        _suppressResizeExit = true;
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
        FocusPane(kind);
        Dispatcher.BeginInvoke(new Action(() => _suppressResizeExit = false), DispatcherPriority.Input);
    }

    // leaf から根へ向かい、向き orientation に一致する最も近い 祖先スプリットと、その分割直下にある（リーフへ至る経路上の）子ノードを返す。無ければ null。
    private (PaneSplit Split, PaneNode Child)? FindAncestorSplit(PaneNode leaf, SplitKind orientation)
    {
        var node = leaf;
        for (var parent = FindParent(node); parent is not null; parent = FindParent(node))
        {
            if (parent.Orientation == orientation)
                return (parent, node);
            node = parent;
        }
        return null;
    }

    // リサイズモードのオン/オフを切り替え、操作ヒントの表示も連動させる。
    private void SetResizeMode(bool on)
    {
        if (_resizeMode == on)
            return;
        _resizeMode = on;
        if (on)
        {
            EnsureResizeHint();
            PositionResizeHint();
            _resizeHintPopup!.IsOpen = true;
        }
        else if (_resizeHintPopup is not null)
        {
            _resizeHintPopup.IsOpen = false;
        }
    }

    private void EnsureResizeHint()
    {
        if (_resizeHintPopup is not null)
            return;

        var banner = new Border
        {
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 6, 12, 6),
            Child = new TextBlock
            {
                Text = "リサイズモード　h/j/k/l で伸縮　・　Esc で終了",
                Foreground = (Brush)FindResource("Fg"),
                FontSize = UiFontManager.Scaled(12)
            }
        };
        _resizeHintPopup = new Popup
        {
            PlacementTarget = PaneHost,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            Child = banner
        };
    }

    // ヒントを PaneHost の下部中央へ置く。
    private void PositionResizeHint()
    {
        if (_resizeHintPopup is null)
            return;
        const double estimatedWidth = 340;
        _resizeHintPopup.HorizontalOffset = Math.Max(8, (PaneHost.ActualWidth - estimatedWidth) / 2);
        _resizeHintPopup.VerticalOffset = Math.Max(8, PaneHost.ActualHeight - 48);
    }

    // FolderTree の「ターミナルにセット」要求を処理する。フォルダは可視ターミナルでそのフォルダへ cd し、ファイルはパスをプロンプトへ入力する（実行はしない＝ユーザーがコマンドを組み立てられる）。 いずれもターミナルペインを表示してフォーカスする。
    private void OnSetInTerminalRequested(object? sender, TerminalSetRequest request)
    {
        SetPaneVisible(PaneKind.Terminal, true);

        if (request.IsDirectory)
        {
            // 可視ターミナル＋エージェント cwd の両方を追従させる（既存のフォルダ追従と同じ経路）。
            _terminal.SetWorkingDirectory(request.FullPath);
        }
        else
        {
            // 空白を含むパスは引用してそのまま使えるようにする。改行は付けない（未実行）。
            var path = request.FullPath;
            var text = path.IndexOf(' ') >= 0 ? $"\"{path}\"" : path;
            _activeTerminalTab?.View.SendTerminalInput(text);
        }

        FocusPane(PaneKind.Terminal);
    }
}
