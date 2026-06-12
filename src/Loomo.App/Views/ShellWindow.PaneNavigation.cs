using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Settings;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン操作（Ctrl+W プレフィックス：h/j/k/l フォーカス移動・リサイズモード・ズーム）</summary>
public partial class ShellWindow
{
    // ===== ペイン操作（Ctrl+W → h/j/k/l 移動 / Shift+h/j/k/l リサイズ / z ズーム） =====

    /// <summary>
    /// Ctrl+W を押すと方向キー（h/j/k/l）待ちに入り、続けて押されたキーの向きの
    /// 隣接ペインへフォーカスを移す。Preview（トンネル）で本体より先に拾い、消費したキーは
    /// <see cref="RoutedEventArgs.Handled"/> で止める（同一イベントの KeyDown も併せて抑止される）。
    /// </summary>
    private void OnPaneNavKey(object sender, KeyEventArgs e)
    {
        var key = e.Key;

        // リサイズモード中は h/j/k/l（修飾不要）で伸縮し続ける。Ctrl+W で移動プレフィックスへ復帰、
        // Esc/Enter で確定終了、その他のキーはモードを抜けて通常入力としてそのまま流す。
        if (_resizeMode)
        {
            if (IsModifierKey(key))
                return; // Shift 等の単独押下はモード維持
            if (key == Key.W && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                SetResizeMode(false);
                _awaitingPaneDirection = true;
                e.Handled = true;
                return;
            }
            if (MapNavDirection(key) is { } resizeDir)
            {
                ResizeFocusedPane(resizeDir);
                e.Handled = true;
                return;
            }
            SetResizeMode(false);
            if (key is Key.Escape or Key.Return)
                e.Handled = true;
            return;
        }

        if (_awaitingPaneDirection)
        {
            if (IsModifierKey(key))
                return; // Ctrl 等の単独押下は方向キー待ちを維持する

            // Ctrl+W の押しっぱなし・再入力はプレフィックスのまま待ち続ける
            if (key == Key.W && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                return;
            }

            _awaitingPaneDirection = false;
            if (key == Key.Z)
            {
                ToggleZoom(); // Ctrl+W z でフォーカス中ペインをズーム／復元
                e.Handled = true;
                return;
            }
            if (key == Key.X)
            {
                // Ctrl+W x：分割中ならまずその分割ビューポートを消す。分割が無ければペイン（またはサイドバー）を隠す。
                if (!CloseFocusedViewport())
                    HideFocusedRegion();
                e.Handled = true;
                return;
            }
            if (key is Key.V or Key.S or Key.Q)
            {
                // Ctrl+W v/s でペイン内を分割（v=左右 / s=上下）、q で分割を畳む。Editor/Terminal のみ作用。
                HandleViewportSplitKey(key);
                e.Handled = true;
                return;
            }
            if (MapNavDirection(key) is { } direction)
            {
                // Shift 併用はフォーカス中ペインのリサイズ（以降はモードに入り連打で伸縮可）、
                // 単独は隣接ペインへフォーカス移動。
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    ResizeFocusedPane(direction);
                    SetResizeMode(true);
                }
                else
                    FocusPaneInDirection(direction);
                e.Handled = true;
            }
            // 方向キー以外はプレフィックスを解除し、そのまま素通しさせる
            return;
        }

        if (key == Key.W && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _awaitingPaneDirection = true;
            e.Handled = true;
        }
    }

    private static DropZone? MapNavDirection(Key key) => key switch
    {
        Key.H => DropZone.Left,
        Key.J => DropZone.Below,
        Key.K => DropZone.Above,
        Key.L => DropZone.Right,
        _ => null
    };

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.System or Key.LWin or Key.RWin;

    /// <summary>1回のキーリサイズで動かす量（その分割の合計比率に対する割合）。</summary>
    private const double ResizeStepRatio = 0.08;

    /// <summary>
    /// フォーカス中ペインを指定方向へリサイズする（L=広く / H=狭く / J=高く / K=低く）。
    /// 方向の軸に一致する最も近い祖先スプリットを探し、フォーカスペイン側の子の比率を増減する。
    /// 軸に合うスプリットが無い（その方向に分割が無い）場合は何もしない。
    /// </summary>
    private void ResizeFocusedPane(DropZone direction)
    {
        if (_zoomedPane is not null || _focusedRegion is not { } region)
            return;

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

    /// <summary>
    /// <paramref name="leaf"/> から根へ向かい、向き <paramref name="orientation"/> に一致する最も近い
    /// 祖先スプリットと、その分割直下にある（リーフへ至る経路上の）子ノードを返す。無ければ null。
    /// </summary>
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

    /// <summary>リサイズモードのオン/オフを切り替え、操作ヒントの表示も連動させる。</summary>
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
                FontSize = 12
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

    /// <summary>ヒントを PaneHost の下部中央へ置く。</summary>
    private void PositionResizeHint()
    {
        if (_resizeHintPopup is null)
            return;
        const double estimatedWidth = 340;
        _resizeHintPopup.HorizontalOffset = Math.Max(8, (PaneHost.ActualWidth - estimatedWidth) / 2);
        _resizeHintPopup.VerticalOffset = Math.Max(8, PaneHost.ActualHeight - 48);
    }

    /// <summary>キーボードフォーカスが入ったペインを記録する（移動の起点に使う）。</summary>
    private void OnWindowPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // フォーカスが他所へ移ったら、待ち状態の Ctrl+W プレフィックスは破棄する。
        // （Ctrl+W → 気が変わってクリック/別ペインへ移動 → 後続の h/j/k/l が誤って奪われるのを防ぐ）
        _awaitingPaneDirection = false;

        // リサイズ自身が起こすフォーカス移動（ガード中）以外でフォーカスが動いたら、
        // ユーザー操作とみなしてリサイズモードを終了する（次のキー入力が誤って奪われない）。
        if (_resizeMode && !_suppressResizeExit)
            SetResizeMode(false);

        if (e.NewFocus is not DependencyObject d)
            return;
        if (FindPaneOf(d) is { } kind)
        {
            // 分割中ならどのビューポートが取得したかまで記録する（hjkl 移動の起点に使う）。
            if (ViewsFor(kind) is { } views && views.SetFocusedFromElement(d) is { } viewId)
                _focusedRegion = FocusTarget.Viewport(kind, viewId);
            else
                _focusedRegion = FocusTarget.Of(kind);
        }
        else if (IsWithin(d, SidebarContainer))
            _focusedRegion = FocusTarget.Sidebar;
    }

    /// <summary>要素が指定の祖先（論理・視覚いずれか）の内側にあるか。</summary>
    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = GetAnyParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    /// <summary>ウィンドウが非アクティブになったら Ctrl+W の待ち状態とリサイズモードを解除する。</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _awaitingPaneDirection = false;
        SetResizeMode(false);
    }

    /// <summary>要素を内包するペイン種別を視覚ツリーを遡って特定する（ペイン外なら null）。</summary>
    private PaneKind? FindPaneOf(DependencyObject element)
    {
        for (var current = element; current is not null; current = GetAnyParent(current))
        {
            foreach (var (kind, paneElement) in _paneElements)
                if (ReferenceEquals(paneElement, current))
                    return kind;
        }
        return null;
    }

    private static DependencyObject? GetAnyParent(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    /// <summary>
    /// 起点領域から指定方向で最も近い隣接領域へフォーカスを移す。候補にはペイン本体に加え、
    /// 表示中ならサイドバー（Explorer 等）も含めるので、最左ペインから Ctrl+W h でサイドバーへ移れる。
    /// </summary>
    private void FocusPaneInDirection(DropZone direction)
    {
        // ステージモード中の h/j/k/l は「舞台の転換」（並び順で前後のペインへ）と読み替える。
        if (_stageActive)
        {
            CycleStage(direction is DropZone.Below or DropZone.Right ? 1 : -1);
            return;
        }
        var targets = FocusTargets().ToList();
        if (targets.Count == 0)
            return;

        // 起点：直近フォーカスの領域。見つからなければ最初の候補（=可視ペイン）を起点扱いにする。
        var originIndex = _focusedRegion is { } region
            ? targets.FindIndex(t => t.Target == region)
            : -1;
        if (originIndex < 0)
            originIndex = 0;
        var (originTarget, from) = targets[originIndex];

        var fromCenter = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        FocusTarget? best = null;
        var bestScore = double.MaxValue;

        foreach (var (target, r) in targets)
        {
            if (target == originTarget)
                continue;

            // 指定方向の側にある領域だけを候補にする（タイル配置なので辺で判定）。
            const double tolerance = 1.0;
            var inDirection = direction switch
            {
                DropZone.Left => r.X + r.Width <= from.X + tolerance,
                DropZone.Right => r.X >= from.X + from.Width - tolerance,
                DropZone.Above => r.Y + r.Height <= from.Y + tolerance,
                _ => r.Y >= from.Y + from.Height - tolerance,
            };
            if (!inDirection)
                continue;

            var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            // 移動軸方向の距離を主に、直交方向のずれを従にして最も近い領域を選ぶ。
            var (axis, perpendicular) = direction is DropZone.Left or DropZone.Right
                ? (Math.Abs(center.X - fromCenter.X), Math.Abs(center.Y - fromCenter.Y))
                : (Math.Abs(center.Y - fromCenter.Y), Math.Abs(center.X - fromCenter.X));
            var score = axis + perpendicular * 2;
            if (score < bestScore)
            {
                bestScore = score;
                best = target;
            }
        }

        if (best is { } target2)
            ApplyFocusTarget(target2);
    }

    /// <summary>ナビゲーション候補（表示中ペイン＋サイドバー）を矩形付きで列挙する。ペインを先頭に並べる。</summary>
    private IEnumerable<(FocusTarget Target, Rect Rect)> FocusTargets()
    {
        foreach (var leaf in AllLeaves())
        {
            if (leaf.Hidden)
                continue;
            // 内部分割しているペインはビューポート単位、それ以外はペイン全体を1候補にする。
            if (ViewsFor(leaf.Kind) is { LeafCount: > 1 } views)
            {
                foreach (var (id, rect) in views.ViewportRects(PaneHost))
                    yield return (FocusTarget.Viewport(leaf.Kind, id), rect);
            }
            else if (TryGetPaneRect(leaf.Kind, out var rect))
            {
                yield return (FocusTarget.Of(leaf.Kind), rect);
            }
        }

        if (TryGetSidebarRect(out var sidebarRect))
            yield return (FocusTarget.Sidebar, sidebarRect);
    }

    /// <summary>そのペインの内部分割マネージャ（Editor/Terminal のみ。それ以外は null）。</summary>
    private PaneSplitView? ViewsFor(PaneKind kind) => kind switch
    {
        PaneKind.Editor => _editorViews,
        PaneKind.Terminal => _terminalViews,
        _ => null
    };

    /// <summary>サイドバーの矩形（PaneHost 座標系）を取得する。非表示・未配置なら false。</summary>
    private bool TryGetSidebarRect(out Rect rect)
    {
        rect = default;
        if (!_vm.IsSidebarVisible || !SidebarContainer.IsVisible
            || SidebarContainer.ActualWidth <= 0 || SidebarContainer.ActualHeight <= 0)
            return false;

        // サイドバーは PaneHost の左隣にあるため X は負になるが、辺判定・距離計算はそのまま成立する。
        var topLeft = SidebarContainer.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        rect = new Rect(topLeft, new Size(SidebarContainer.ActualWidth, SidebarContainer.ActualHeight));
        return true;
    }

    private void ApplyFocusTarget(FocusTarget target)
    {
        if (target.IsSidebar)
        {
            FocusSidebar();
            return;
        }

        var kind = target.Pane!.Value;
        if (target.ViewportId != default && ViewsFor(kind) is { } views)
        {
            views.FocusViewport(target.ViewportId);
            _focusedRegion = target;
            SyncActiveFromViewport(kind);
        }
        else
        {
            FocusPane(kind);
        }
    }

    /// <summary>フォーカス中ビューポートのタブに合わせて strip 強調・サービスアタッチを追従させる。</summary>
    private void SyncActiveFromViewport(PaneKind kind)
    {
        if (kind == PaneKind.Editor && _editorViews?.FocusedTabId is { } eid
            && _editorTabs.FirstOrDefault(t => t.Id == eid) is { } et)
            SetActiveEditorTab(et);
        else if (kind == PaneKind.Terminal && _terminalViews?.FocusedTabId is { } tid
            && _terminalTabs.FirstOrDefault(t => t.Id == tid) is { } tt)
            SetActiveTerminalTab(tt);
    }

    /// <summary>表示中のサイドバー（Explorer 等）へキーボードフォーカスを移す。</summary>
    private void FocusSidebar()
    {
        if (!_vm.IsSidebarVisible)
            return;

        var view = SidebarContainer.Children.OfType<UIElement>()
            .FirstOrDefault(c => c.Visibility == Visibility.Visible);
        if (view is null)
            return;

        _focusedRegion = FocusTarget.Sidebar;
        if (view is FolderTreeView tree)
            tree.FocusTree();           // Explorer は中身のツリーへ直接フォーカス（先頭未選択なら選ぶ）
        else
            FocusFirstFocusable(view);  // 他パネルは最初のフォーカス可能要素へ
    }

    /// <summary>要素ツリーを深さ優先でたどり、最初のフォーカス可能要素へフォーカスを移す。</summary>
    private static bool FocusFirstFocusable(DependencyObject root)
    {
        if (root is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element)
        {
            element.Focus();
            return true;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FocusFirstFocusable(VisualTreeHelper.GetChild(root, i)))
                return true;
        return false;
    }

    /// <summary>ペイン本体の矩形（PaneHost 座標系）を取得する。非表示・未配置なら false。</summary>
    private bool TryGetPaneRect(PaneKind kind, out Rect rect)
    {
        rect = default;
        if (!_paneElements.TryGetValue(kind, out var element)
            || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        var topLeft = element.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        rect = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
        return true;
    }

    /// <summary>指定ペインのアクティブな中身へキーボードフォーカスを移す。</summary>
    private void FocusPane(PaneKind kind)
    {
        // ステージモード中は、フォーカス対象を舞台へ立てる（AI がファイルを開いた・
        // 差分を出した等の既存フローがそのまま「舞台の自動転換」になる）。
        if (_stageActive && kind != _stagePane)
            SetStagePane(kind);
        _focusedRegion = FocusTarget.Of(kind);
        switch (kind)
        {
            case PaneKind.Terminal:
                if (_terminalViews is { } tv) tv.FocusFocused();
                else _activeTerminalTab?.View.FocusTerminal();
                break;
            case PaneKind.Editor:
                if (_editorViews is { } ev) ev.FocusFocused();
                else _activeEditorTab?.Control.Focus();
                break;
            case PaneKind.EditorSupport:
                _editorSupportView?.Focus();
                break;
            case PaneKind.Browser:
                _activeBrowserTab?.View.Focus();
                break;
            case PaneKind.Ai:
                AiBarHost.FocusInput();
                break;
            case PaneKind.Git:
                GitSessionHost.Focus();
                break;
            case PaneKind.Diff:
                DiffSessionHost.Focus();
                break;
            case PaneKind.Trace:
                TraceSessionHost.Focus();
                break;
        }
    }

    /// <summary>
    /// FolderTree の「ターミナルにセット」要求を処理する。フォルダは可視ターミナルでそのフォルダへ
    /// cd し、ファイルはパスをプロンプトへ入力する（実行はしない＝ユーザーがコマンドを組み立てられる）。
    /// いずれもターミナルペインを表示してフォーカスする。
    /// </summary>
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
