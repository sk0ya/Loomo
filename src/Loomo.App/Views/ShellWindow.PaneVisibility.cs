using sk0ya.Loomo.Core.Files;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインの表示/非表示トグルと、開いたファイル・結果表示のためのペイン確保 （SetPaneVisible・トグル状態同期・左上入れ替え・最下段追加）。レイアウト構築は ShellWindow.PaneLayout.cs。</summary>
public partial class ShellWindow {
    private void OnHidePane(object sender, RoutedEventArgs e) {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;
        BeginTrailLayoutChange();
        SetPaneVisible(kind, false);
    }
    /// <summary>ビュー・スイッチャーの1ペイン分。状態で色や印が変わる要素だけ持ち、
    /// <see cref="RefreshPaneMenuStates"/> がその場で書き換える（開いたままの作り直しを避ける）。</summary>
    private sealed record PaneMenuRow(
        PaneKind Kind, PopupRow Row, Button Eye, System.Windows.Shapes.Path EyeIcon);
    private readonly List<PaneMenuRow> _paneMenuRows = new();
    private void HookPaneMenu() {
        TrackPopupClose(PaneTogglePopup);
        // 別ウィンドウへ移ったら畳む（ポップアップは別 HWND だが WPF では自ウィンドウの Deactivated を
        // 起こさないので、中のボタン操作では閉じない）。TrailDateTimePopup と同じ扱い。
        Deactivated += (_, _) => PaneTogglePopup.IsOpen = false;
    }
    /// <summary>開いたままなら中身を作り直す。モード切替のように、開いている前提で内容が変わる操作から呼ぶ。</summary>
    private void RefreshOpenPaneMenu() {
        if (PaneTogglePopup.IsOpen)
            BuildPaneMenu();
    }
    private void OnMainPaneClick(object sender, RoutedEventArgs e) => TogglePopup(PaneTogglePopup, BuildPaneMenu);
    private PaneKind? CurrentMainPane() => _stageActive ? _stagePane : TopLeftPane();
    private static string PaneIconKey(PaneKind kind) => $"PaneIcon.{kind}";
    private void UpdateMainPaneHeader() {
        var main = CurrentMainPane();
        var layoutLabel = CurrentLayoutLabel();
        var modeLabel = DisplayModeName(_stageActive);
        MainPaneIcon.Data = main is { } kind && TryFindResource(PaneIconKey(kind)) is Geometry geo ? geo : null;
        // 集中表示＝舞台のペイン名、分割表示＝配置名。名前の無い配置に「未保存の配置」と出すのは
        // 情報が無いのに幅だけ取るので、その場合はモード名だけにする。
        MainPaneLabel.Text = _stageActive
            ? main is { } labelKind ? PaneLabel(labelKind) : "選択"
            : layoutLabel == UnsavedLayoutLabel ? "" : layoutLabel;
        var hasDetail = MainPaneLabel.Text.Length > 0;
        MainPaneLabel.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        MainPaneLabelSeparator.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        MainPaneButton.ToolTip = main is { } k
            ? _stageActive
                ? $"{modeLabel}／メイン: {PaneLabel(k)}"
                : $"{modeLabel}／配置: {(hasDetail ? layoutLabel : UnsavedLayoutLabel)}／メイン: {PaneLabel(k)}"
            : "並べ方、配置、メイン画面を変更";
    }
    /// <summary>ポップアップの1行。<c>[現在印 6px][8][アイコン 16px][7][ラベル]</c> という同じ文法を、
    /// 「メイン画面」も「配置」もこの1関数から作る（作り分けると左端が3種類に割れる）。</summary>
    private PopupRow BuildPopupRow(string text, Geometry? iconGeometry) {
        var content = new DockPanel();
        var dot = new Ellipse {
            Width = 6, Height = 6, Fill = (Brush)FindResource("Accent"),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(dot);
        System.Windows.Shapes.Path? icon = null;
        if (iconGeometry is not null) {
            icon = new System.Windows.Shapes.Path {
                Data = iconGeometry, Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.3,
                Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(icon);
        }
        var label = new TextBlock {
            Text = text, FontSize = UiFontManager.Scaled(12),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(label);
        var button = new Button {
            Content = content, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(9, 5, 6, 5), Style = (Style)FindResource("BranchMenuItem"),
        };
        return new PopupRow(button, dot, icon, label);
    }
    private sealed record PopupRow(Button Button, Ellipse Dot, System.Windows.Shapes.Path? Icon, TextBlock Label) {
        /// <summary>現在選択中か（印・アクセント）と、部屋に出ているか（淡色）を同時に表す。</summary>
        public void SetState(bool active, bool enabled, Brush accent, Brush fg, Brush fgDim) {
            Dot.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            if (Icon is { } icon) {
                icon.Stroke = active ? accent : fgDim;
                icon.Opacity = enabled ? 1 : 0.45;
            }
            Label.Foreground = active ? accent : enabled ? fg : fgDim;
            Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
    /// <summary>行末の「部屋に出す／しまう」トグル。隠しセクションに追いやると、一覧に出ている画面が
    /// なぜ画面に無いのか説明が付かないので、名前と同じ行に置く。</summary>
    private static readonly Geometry EyeOnIcon = FreezeGeometry(
        "M0.5,7 C3.5,3 10.5,3 13.5,7 C10.5,11 3.5,11 0.5,7 Z M5.4,7 A1.6,1.6 0 1,0 8.6,7 A1.6,1.6 0 1,0 5.4,7 Z");
    private static readonly Geometry EyeOffIcon = FreezeGeometry(
        "M0.5,7 C3.5,3 10.5,3 13.5,7 C10.5,11 3.5,11 0.5,7 Z M1.5,11.5 L12.5,2.5");
    private static Geometry FreezeGeometry(string path) {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }
    private void BuildPaneMenu() {
        // ヘッダーはタイトルバー右端に近いので、枠の左端に合わせると画面外へはみ出して切れる。右端をそろえる。
        PaneTogglePopup.HorizontalOffset = Math.Min(0, MainPaneButton.ActualWidth - PaneTogglePopupRoot.Width);
        MainPaneChoices.Children.Clear();
        _paneMenuRows.Clear();
        // 配置は分割表示にしか無い概念。集中表示では見出しごと畳む（見出しだけ残すと空セクションに見える）。
        LayoutSection.Visibility = _stageActive ? Visibility.Collapsed : Visibility.Visible;
        LayoutSaveRow.Visibility = Visibility.Collapsed;
        LayoutNameInput.Clear();
        BuildLayoutPopup();
        foreach (var kind in StageOrder.Where(IsPaneApplicable)) {
            var row = BuildPopupRow(PaneLabel(kind), TryFindResource(PaneIconKey(kind)) as Geometry);
            row.Button.CommandParameter = kind.ToString();
            row.Button.Click += OnSelectMainPane;

            var eyeIcon = new System.Windows.Shapes.Path {
                Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.1,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            var eye = new Button {
                Tag = kind.ToString(), Content = eyeIcon, Width = 26,
                Style = (Style)FindResource("BranchMenuItem"), Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            eye.Click += OnTogglePaneVisibility;
            DockPanel.SetDock(eye, Dock.Right);

            var host = new DockPanel { LastChildFill = true };
            host.Children.Add(eye);
            host.Children.Add(row.Button);
            MainPaneChoices.Children.Add(host);
            _paneMenuRows.Add(new PaneMenuRow(kind, row, eye, eyeIcon));
        }
        RefreshPaneMenuStates();
    }
    /// <summary>作り直さずに現在状態（メイン印・表示チェック）だけ反映する。</summary>
    private void RefreshPaneMenuStates() {
        if (_paneMenuRows.Count == 0)
            return;
        var main = CurrentMainPane();
        var accent = (Brush)FindResource("Accent");
        var fg = (Brush)FindResource("Fg");
        var fgDim = (Brush)FindResource("FgDim");
        foreach (var row in _paneMenuRows) {
            var enabled = IsSessionEnabled(row.Kind);
            row.Row.SetState(main == row.Kind, enabled, accent, fg, fgDim);
            row.Row.Button.ToolTip = $"{PaneLabel(row.Kind)} をメインにする";
            row.EyeIcon.Data = enabled ? EyeOnIcon : EyeOffIcon;
            row.EyeIcon.Stroke = enabled ? fg : fgDim;
            row.Eye.ToolTip = enabled
                ? $"{PaneLabel(row.Kind)} を部屋からしまう"
                : $"{PaneLabel(row.Kind)} を部屋に出す";
        }
    }
    private bool IsPaneApplicable(PaneKind kind)
        => (kind != PaneKind.Debug || _idePaneApplicable)
            && (kind != PaneKind.TsIde || _tsIdePaneApplicable);
    private void OnSelectMainPane(object sender, RoutedEventArgs e) {
        if (sender is not Button { CommandParameter: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;
        BeginTrailLayoutChange();
        _enabledSessions.Add(kind);
        if (_stageActive) {
            SetStagePane(kind);
            FocusPane(kind);
        } else {
            SwapIntoTopLeft(kind);
            FocusPane(kind);
        }
        PaneTogglePopup.IsOpen = false;
        UpdatePaneToggleStates();
    }
    private void OnTogglePaneVisibility(object sender, RoutedEventArgs e) {
        BeginTrailLayoutChange();
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneKind>(tag, out var kind))
            ToggleSessionEnabled(kind);
        UpdatePaneToggleStates();
    }
    private void UpdatePaneToggleStates() {
        RefreshPaneMenuStates();
        UpdateMainPaneHeader();
    }
    private static string PaneLabel(PaneKind kind) => kind switch {
        PaneKind.Terminal => "ターミナル", PaneKind.Editor => "エディタ", PaneKind.EditorSupport => "エディタサポート", PaneKind.Browser => "ブラウザ", PaneKind.Ai => "AI", PaneKind.Git => "Git", PaneKind.Diff => "Diff", PaneKind.Trace => "トレース", PaneKind.Debug => "IDE", PaneKind.Search => "検索", PaneKind.TsIde => "TS IDE", PaneKind.Files => "ファイル一覧", _ => kind.ToString(),
    };
    private bool IsPaneVisible(PaneKind kind) => FindLeaf(kind) is { Hidden: false };
    private int VisibleLeafCount() => AllLeaves().Count(l => !l.Hidden);
    private void SetPaneVisible(PaneKind kind, bool visible) {
        var leaf = FindLeaf(kind);
        var currentlyVisible = leaf is { Hidden: false };
        if (visible)
            _enabledSessions.Add(kind);
        if (currentlyVisible == visible)
            return;
        CaptureLayoutSizes();
        if (visible) {
            if (leaf is null) {
                var newLeaf = NewLeaf(kind);
                if (_isSpanMaximized && _root is PaneSplit { Orientation: SplitKind.Columns } columns
                    && columns.Children.Count > 0)
                    columns.Children[^1] = AddLeafAtBottom(columns.Children[^1], newLeaf);
                else
                    AddLeafAtBottom(newLeaf);
            } else
                leaf.Hidden = false;
        } else {
            if (VisibleLeafCount() <= 1)
                return;
            leaf!.Hidden = true;
            if (_focusedRegion?.Pane == kind)
                _focusedRegion = null; // 起点が消えたので次回ナビゲーションは可視ペインから選び直す
        }
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot) {
            if (AllLeaves(savedRoot).FirstOrDefault(l => l.Kind == kind) is { } savedLeaf)
                savedLeaf.Hidden = !visible;
            else if (visible)
                _spanSavedRoot = AddLeafAtBottom(savedRoot, NewLeaf(kind));
        }
        if (kind == PaneKind.EditorSupport && visible)
            InvalidateEditorSupport();
        _zoomedPane = null; // 表示構成が変わるのでズームは解除する
        _root = Normalize(_root);
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }
    private void EnsureEditorPaneForOpenedFile(string path) {
        var target = BinaryFileDetector.IsBinary(path) ? PaneKind.EditorSupport : PaneKind.Editor;
        if (_stageActive) {
            if (!OnStage(PaneKind.Editor) && !OnStage(PaneKind.EditorSupport))
                SetStagePane(target);
            return;
        }
        if (IsPaneVisible(PaneKind.Editor) || IsPaneVisible(PaneKind.EditorSupport))
            return;
        PlacePaneByBehavior(target);
    }
    private void EnsurePaneVisibleOrSwapTopLeft(PaneKind target) {
        if (_stageActive) {
            if (!OnStage(target))
                SetStagePane(target);
            return;
        }
        if (IsPaneVisible(target))
            return;
        PlacePaneByBehavior(target);
    }
    private void PlacePaneByBehavior(PaneKind target) {
        switch (_settings.PaneOpenBehavior) {
            case PaneOpenBehavior.Sub:
                PlaceIntoSubPane(target);
                break;
            case PaneOpenBehavior.Loop:
                PlaceIntoLoopPane(target);
                break;
            default:
                SwapIntoTopLeft(target);
                break;
        }
    }
    private void SwapIntoTopLeft(PaneKind target) {
        if (TopLeftPane() is { } topLeft && topLeft != target)
            PlaceWingPane(target, topLeft, center: true, zone: null);
        else
            SetPaneVisible(target, true);
    }
    private void PlaceIntoSubPane(PaneKind target) {
        if (IsPaneVisible(target))
            return;
        var main = TopRowLeftPane();
        var sub = TopRightPane();
        if (sub is { } s && s != main)
            PlaceWingPane(target, s, center: true, zone: null);                // 右上と入れ替え
        else if (main is { } m && m != target)
            PlaceWingPane(target, m, center: false, zone: DropZone.Right);     // 横1枚 → 右に追加
        else
            SetPaneVisible(target, true);
    }
    private void PlaceIntoLoopPane(PaneKind target) {
        if (IsPaneVisible(target))
            return;
        var main = TopRowLeftPane();
        var sub = TopRightPane();
        var originFromSub = _focusedRegion?.Pane is { } origin
            && sub is { } s && s != main && origin == s;
        if (originFromSub && main is { } m && sub is { } current && current != target) {
            PlaceWingPane(current, m, center: true, zone: null);
            PlaceWingPane(target, current, center: false, zone: DropZone.Right);
        } else {
            PlaceIntoSubPane(target);
        }
    }
    private void AddLeafAtBottom(PaneLeaf leaf) => _root = AddLeafAtBottom(_root, leaf);
    private static PaneNode AddLeafAtBottom(PaneNode? root, PaneLeaf leaf) => PaneLayoutTree.AddLeafAtBottom(root, leaf);
}
