using sk0ya.Loomo.Core.Files;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインの表示/非表示トグルと、開いたファイル・結果表示のためのペイン確保 （SetPaneVisible・トグル状態同期・左上入れ替え・最下段追加）。レイアウト構築は ShellWindow.PaneLayout.cs。</summary>
public partial class ShellWindow {
    private void OnHidePane(object sender, RoutedEventArgs e) {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;
        if (TryCloseDockPane(kind))   // ドックの領域に出ている面は「畳む」（タイルからしまうのではない）
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
    private static string PaneIconKey(PaneKind kind) => $"PaneIcon.{kind}";
    private void UpdateMainPaneHeader() {
        var main = PaneVisibilityPresentation.ResolveMainPane(
            _stageActive, _stagePane, _dockActive, _dockMode.CenterPane, TopLeftPane());
        var layoutLabel = CurrentLayoutLabel();
        var modeLabel = ShellLayoutPresentation.ModeName(CurrentDisplayMode);
        MainPaneIcon.Data = main is { } kind && TryFindResource(PaneIconKey(kind)) is Geometry geo ? geo : null;
        // 集中表示＝舞台のペイン名、分割表示＝配置名。名前の無い配置に「未保存の配置」と出すのは
        // 情報が無いのに幅だけ取るので、その場合はモード名だけにする。
        // 集中もドックも「いま中央に立っている面」が現在地。分割だけが配置名を出す。
        // 中央を畳んだドックには「いま立っている面」が無い。名前の代わりに「選択」のような
        // 置き字を出すのは、名前の無い配置に「未保存の配置」と出すのと同じ空振りなので、
        // 言うことが無いときはモード名だけにする。
        var header = PaneVisibilityPresentation.MainHeader(
            main, layoutLabel, UnsavedLayoutLabel, modeLabel, _stageActive || _dockActive);
        MainPaneLabel.Text = header.Label;
        var hasDetail = header.HasDetail;
        MainPaneLabel.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        MainPaneLabelSeparator.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        MainPaneButton.ToolTip = header.ToolTip;
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
        // 配置（タイルの組み方）は分割表示にしか無い概念。集中・ドックでは見出しごと畳む。
        LayoutSection.Visibility = _stageActive || _dockActive ? Visibility.Collapsed : Visibility.Visible;
        LayoutSaveRow.Visibility = Visibility.Collapsed;
        LayoutNameInput.Clear();
        BuildLayoutPopup();
        foreach (var kind in PaneOrderForMode().Where(IsPaneApplicable)) {
            var row = BuildPopupRow(PaneLabel(kind), TryFindResource(PaneIconKey(kind)) as Geometry);
            row.Button.CommandParameter = kind.ToString();
            row.Button.Click += OnSelectMainPane;
            if (_dockActive)
                row.Button.ContextMenu = BuildDockPlacementMenu(kind);

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
        var main = PaneVisibilityPresentation.ResolveMainPane(
            _stageActive, _stagePane, _dockActive, _dockMode.CenterPane, TopLeftPane());
        var accent = (Brush)FindResource("Accent");
        var fg = (Brush)FindResource("Fg");
        var fgDim = (Brush)FindResource("FgDim");
        foreach (var row in _paneMenuRows) {
            var docked = _dockActive;
            var state = PaneVisibilityPresentation.MenuState(
                row.Kind, main, docked, _dockMode.IsOpen(row.Kind), IsSessionEnabled(row.Kind),
                docked ? _dockMode.RegionOf(row.Kind) : DockRegion.Center);
            row.Row.SetState(state.Active, state.Enabled, accent, fg, fgDim);
            row.Row.Button.ToolTip = state.ToolTips.MainAction;
            row.EyeIcon.Data = state.Enabled ? EyeOnIcon : EyeOffIcon;
            row.EyeIcon.Stroke = state.Enabled ? fg : fgDim;
            row.Eye.ToolTip = state.ToolTips.VisibilityAction;
        }
    }
    private bool IsPaneApplicable(PaneKind kind)
        => (kind != PaneKind.Debug || _idePaneApplicable)
            && (kind != PaneKind.TsIde || _tsIdePaneApplicable);
    private void OnSelectMainPane(object sender, RoutedEventArgs e) {
        if (sender is not Button { CommandParameter: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;
        if (_dockActive) {
            ToggleDockPane(kind);   // ドックはどの面も「その領域の1枚にする」
            if (_dockMode.IsOpen(kind))
                FocusPane(kind);
            PaneTogglePopup.IsOpen = false;
            UpdatePaneToggleStates();
            return;
        }
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
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;
        if (_dockActive) {
            ToggleDockPane(kind);   // ドックに「部屋に出す／しまう」は無い。あるのは開閉だけ
            UpdatePaneToggleStates();
            return;
        }
        BeginTrailLayoutChange();
        ToggleSessionEnabled(kind);
        UpdatePaneToggleStates();
    }
    private void UpdatePaneToggleStates() {
        RefreshPaneMenuStates();
        UpdateMainPaneHeader();
        if (_dockActive)
            RebuildDockBar();   // 中央の面の印はタイルの表示状態なので、ここでも帯を合わせ直す
    }
    private static string PaneLabel(PaneKind kind) => PaneVisibilityPresentation.Label(kind);
    private bool IsPaneVisible(PaneKind kind) => FindLeaf(kind) is { Hidden: false };
    private int VisibleLeafCount() => AllLeaves().Count(l => !l.Hidden);
    private void SetPaneVisible(PaneKind kind, bool visible) {
        var leaf = FindLeaf(kind);
        var currentlyVisible = leaf is { Hidden: false };
        if (visible)
            _enabledSessions.Add(kind);
        if (currentlyVisible == visible)
            return;
        if (!visible && VisibleLeafCount() <= 1)
            return;
        CaptureLayoutSizes();
        _paneLayout.SetVisible(kind, visible, appendToLastColumn: _isSpanMaximized);
        if (!visible) {
            if (_focusedRegion?.Pane == kind)
                _focusedRegion = null; // 起点が消えたので次回ナビゲーションは可視ペインから選び直す
        }
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot) {
            _spanSavedRoot = PaneLayoutCoordinator.SetVisibleOnSavedTree(savedRoot, kind, visible);
        }
        if (kind == PaneKind.EditorSupport && visible)
            InvalidateEditorSupport();
        _zoomedPane = null; // 表示構成が変わるのでズームは解除する
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }
    /// <summary>舞台（タイル）に出ているペインを袖へしまう。閉じる（＝無効化）のではなく
    /// 「有効なまま非表示」＝袖のカードとして残すので、袖から掴み直せば元どおり戻せる。
    /// ドラッグ元が最後の1枚のときは受けない（舞台が空になる）。</summary>
    private void MovePaneToWing(PaneKind kind) {
        if (_stageActive || _dockActive || !IsPaneVisible(kind) || VisibleLeafCount() <= 1)
            return;
        BeginTrailLayoutChange();
        _enabledSessions.Add(kind);   // 袖に並ぶのは「有効な」ペインだけ
        var wasFocused = _focusedRegion?.Pane == kind;
        SetPaneVisible(kind, false);
        if (!InActiveWingTab(kind))
            SelectWingTab(WingTab.All);   // しまった先が今のタブに無いと、行方が見えない
        if (wasFocused && TopLeftPane() is { } next)
            FocusPane(next);
    }
    /// <summary>ファイルを開いた・エディタのタブを選んだときの見せ方。Editor か EditorSupport の
    /// どちらかが見えていればそのまま（EditorSupport を前に出しているところへ Editor を割り込ませない）。
    /// ドックは Editor の領域に別の面（Terminal 等）が出ているときだけ、EditorSupport が出ていても Editor を出す。
    /// <paramref name="path"/> は無題タブなら null。</summary>
    private void EnsureEditorPaneForOpenedFile(string? path) {
        var plan = PaneRevealPolicy.ForOpenedFile(
            path is not null && BinaryFileDetector.IsBinary(path),
            _dockActive, _stageActive,
            IsPaneVisible(PaneKind.Editor), IsPaneVisible(PaneKind.EditorSupport),
            OnStage(PaneKind.Editor), OnStage(PaneKind.EditorSupport),
            IsDockPaneShown(PaneKind.Editor), IsDockPaneShown(PaneKind.EditorSupport),
            _dockMode.OpenPaneIn(_dockMode.RegionOf(PaneKind.Editor))
                is { } inEditorRegion && inEditorRegion != PaneKind.Editor && inEditorRegion != PaneKind.EditorSupport);
        switch (plan.Action)
        {
            case PaneRevealAction.OpenDock: EnsureDockPaneShown(plan.Target); break;
            case PaneRevealAction.SelectStage: SetStagePane(plan.Target); break;
            case PaneRevealAction.PlaceInLayout: PlacePaneByBehavior(plan.Target); break;
        }
    }
    private void EnsurePaneVisibleOrSwapTopLeft(PaneKind target) {
        switch (PaneRevealPolicy.ForPane(
            _stageActive, _dockActive, IsPaneVisible(target), OnStage(target)))
        {
            case PaneRevealAction.OpenDock: EnsureDockPaneShown(target); break;
            case PaneRevealAction.SelectStage: SetStagePane(target); break;
            case PaneRevealAction.PlaceInLayout: PlacePaneByBehavior(target); break;
        }
    }
    private void PlacePaneByBehavior(PaneKind target) {
        var behavior = _settings.PaneOpenBehavior;
        var targetVisible = IsPaneVisible(target);
        var topLeft = (behavior is PaneOpenBehavior.Sub or PaneOpenBehavior.Loop) ? null : TopLeftPane();
        PaneKind? main = null;
        PaneKind? sub = null;
        if ((behavior is PaneOpenBehavior.Sub or PaneOpenBehavior.Loop) && !targetVisible)
            (main, sub) = MainAndSubPanes();
        var plan = PanePlacementPolicy.Resolve(
            behavior, target, targetVisible, topLeft, main, sub,
            _focusedRegion?.Pane, SubAxis());
        ApplyPanePlacementPlan(plan);
    }
    private void SwapIntoTopLeft(PaneKind target)
    {
        var plan = PanePlacementPolicy.Resolve(
            PaneOpenBehavior.Main, target, IsPaneVisible(target), TopLeftPane(),
            null, null, _focusedRegion?.Pane, SubAxis());
        ApplyPanePlacementPlan(plan);
    }
    private void ApplyPanePlacementPlan(IReadOnlyList<PanePlacementStep> plan) {
        foreach (var step in plan) {
            if (step.MakesVisible)
                SetPaneVisible(step.Pane, true);
            else if (step.RelativeTo is { } relativeTo)
                PlaceWingPane(step.Pane, relativeTo, step.Center, step.Zone);
        }
    }
}
