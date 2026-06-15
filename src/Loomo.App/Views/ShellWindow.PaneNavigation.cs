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
    /// ウィンドウ全体のキー入力（Preview＝トンネル）を受け、コマンドパレット以外は
    /// <see cref="KeyboardDispatcher"/> へ委ねる。バインドの解釈（Ctrl+W プレフィックス連鎖・
    /// h/j/k/l 方向移動・リサイズモード・z/x/v/s/q 等）はすべてデータ駆動で、設定画面での
    /// 再割り当てが即反映される。パレット表示中は Esc の保険だけここで拾う。
    /// </summary>
    private void OnPaneNavKey(object sender, KeyEventArgs e)
    {
        if (IsPaletteOpen)
        {
            if (e.Key == Key.Escape)
            {
                CloseCommandPalette(refocus: true);
                e.Handled = true;
            }
            return;
        }

        _keyboard?.HandlePreviewKeyDown(e);
    }

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
        // フォーカスが他所へ移ったら、待ち状態のプレフィックス連鎖は破棄する
        // （Ctrl+W → 気が変わってクリック/別ペインへ移動 → 後続の h/j/k/l が誤って奪われるのを防ぐ）。
        // リサイズ自身が起こすフォーカス移動（ガード中）以外でフォーカスが動いたら、
        // ユーザー操作とみなしてリサイズモードも終了する。
        _keyboard?.OnExternalFocusChange(suppressModeExit: _suppressResizeExit);

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

    /// <summary>ウィンドウが非アクティブになったらプレフィックス待ち・リサイズモードを解除する。</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
        => _keyboard?.Reset();

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
        // ステージモード中でも、Editor/Terminal の内部分割がある場合は vim 風に
        // そのペイン内のビューポート移動を優先する。端まで来たら、配置モードの Main/Sub 間を移る。
        // そこにも移動先がなければ、従来通りステージを切り替える。
        if (_stageActive && _focusedRegion?.Pane is { } stageFocused
            && ViewsFor(stageFocused) is { LeafCount: > 1 } stageViews)
        {
            if (stageViews.FocusInDirection(direction, PaneHost)
                && stageViews.FocusedViewportId is { } viewportId)
            {
                _focusedRegion = FocusTarget.Viewport(stageFocused, viewportId);
                SyncActiveFromViewport(stageFocused);
                return;
            }
            else if (ProgramActive && FocusOnStagePaneInDirection(direction))
            {
                return;
            }
        }

        // 配置モード中は、舞台上の Main/Sub スロットを見た目の方向で移動する。
        // 端で移動先がない場合はステージ切り替えへフォールバックする。
        if (_stageActive && ProgramActive)
        {
            if (!FocusOnStagePaneInDirection(direction))
                CycleStage(StageCycleDirection(direction));
            return;
        }

        // 単一ステージ中の h/j/k/l は「舞台の転換」（並び順で前後のペインへ）と読み替える。
        if (_stageActive)
        {
            CycleStage(StageCycleDirection(direction));
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

    private static int StageCycleDirection(DropZone direction)
        => direction is DropZone.Below or DropZone.Right ? 1 : -1;

    /// <summary>ステージ配置モードの Main/Sub スロット間で、指定方向の最寄りペインへフォーカスする。</summary>
    private bool FocusOnStagePaneInDirection(DropZone direction)
    {
        if (!_stageActive || !ProgramActive)
            return false;

        var targets = StageFocusTargets().ToList();
        if (targets.Count == 0)
            return false;

        var originPane = _focusedRegion?.Pane is { } pane && OnStage(pane) ? pane : _stagePane;
        var originIndex = targets.FindIndex(t => t.Kind == originPane);
        if (originIndex < 0)
            originIndex = 0;

        var (originKind, from) = targets[originIndex];
        var fromCenter = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        PaneKind? best = null;
        var bestScore = double.MaxValue;

        foreach (var (kind, rect) in targets)
        {
            if (kind == originKind)
                continue;

            const double tolerance = 1.0;
            var inDirection = direction switch
            {
                DropZone.Left => rect.X + rect.Width <= from.X + tolerance,
                DropZone.Right => rect.X >= from.X + from.Width - tolerance,
                DropZone.Above => rect.Y + rect.Height <= from.Y + tolerance,
                _ => rect.Y >= from.Y + from.Height - tolerance,
            };
            if (!inDirection)
                continue;

            var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var (axis, perpendicular) = direction is DropZone.Left or DropZone.Right
                ? (Math.Abs(center.X - fromCenter.X), Math.Abs(center.Y - fromCenter.Y))
                : (Math.Abs(center.Y - fromCenter.Y), Math.Abs(center.X - fromCenter.X));
            var score = axis + perpendicular * 2;
            if (score < bestScore)
            {
                bestScore = score;
                best = kind;
            }
        }

        if (best is not { } next)
            return false;

        FocusPane(next);
        return true;
    }

    /// <summary>ステージ配置モードでフォーカス移動対象になる Main/Sub スロットを矩形付きで列挙する。</summary>
    private IEnumerable<(PaneKind Kind, Rect Rect)> StageFocusTargets()
    {
        if (_mainSlotElement is { } main && BoundsIn(PaneHost, main) is { } mainRect)
            yield return (_stagePane, mainRect);

        foreach (var (index, element) in _subSlotElements)
            if (index >= 0 && index < _stageSubs.Count
                && BoundsIn(PaneHost, element) is { } rect)
                yield return (_stageSubs[index].Kind, rect);
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
        // 差分を出した等の既存フローがそのまま「舞台の自動転換」になる）。配置モード中は
        // 主役を崩さず、舞台外のペインだけサブとして迎える（既に在台なら何もしない）。
        if (_stageActive)
        {
            if (ProgramActive)
            {
                if (!OnStage(kind))
                    AddSub(kind, StageDock.Right);
            }
            else if (kind != _stagePane)
            {
                SetStagePane(kind);
            }
        }
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
