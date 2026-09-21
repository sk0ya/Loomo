namespace sk0ya.Loomo.App.Views;
/// <summary>タイトルバーのデバッグメニュー（C# の部屋にだけ出るボタン）の開閉と、中身（<see cref="DebugSwitcherView"/>）から上がってくる「ウィンドウ側でしかできない操作」の受け口＝IDE ペインを部屋に出すこと。ブランチ切替（<see cref="ShellWindow.OnTitleBarBranchClick"/>）と同じ作りで、押す対象が違うだけ。ボタンの出し入れは <see cref="ShellWindow.ApplyIdePaneApplicability"/>（IDE ペインの適用性＝§28.6 と同じ判定）が行う。</summary>
public partial class ShellWindow {
    private void OnTitleBarDebugClick(object sender, RoutedEventArgs e)
        => TogglePopup(DebugPopup, DebugSwitcher.PrepareForOpen);
    private void HookDebugSwitcher() {
        DebugSwitcher.CloseRequested += (_, _) => DebugPopup.IsOpen = false;
        DebugSwitcher.OpenIdePaneRequested += (_, _) => ShowIdePane();
        DebugSwitcher.RunRequested += OnDebugSwitcherRunRequested;
        TrackPopupClose(DebugPopup);
    }
    /// <summary>メニューの ▶ ＝デバッグなしの実行。Solution Explorer の「実行」と同じ
    /// <see cref="ExecuteCSharpTargetAsync"/> を通す（dotnet run／IIS Express・TFM・launchSettings の
    /// 扱いを1か所に保つ）。</summary>
    private async void OnDebugSwitcherRunRequested(object? sender, string projectPath) {
        if (_terminal.IsExecuting || _vm.Debug.IsTaskRunning) {
            ShowRefactorStatus("別のビルド／テスト／実行が動いています。");
            return;
        }
        if (!File.Exists(projectPath)) {
            ShowRefactorStatus($"対象が見つかりません: {projectPath}");
            return;
        }
        await ExecuteCSharpTargetAsync(projectPath, CSharpSolutionAction.Run,
            CSharpSolutionExplorerPolicy.SelectedTargetFrameworkFor(_solutionModel?.Current, projectPath));
    }
    /// <summary>IDE ペインを部屋に出して前に出す。タイトルバーから開始したデバッグの出力・変数を見に行く
    /// ための導線で、勝手に部屋を組み替えないよう「開く」と明示したときだけ呼ぶ（開始そのものでは動かさない
    /// ——実行中であることは袖の活動バッジで分かる。§24.1）。</summary>
    private void ShowIdePane() {
        if (!_idePaneApplicable)
            return;
        BeginTrailLayoutChange();
        _enabledSessions.Add(PaneKind.Debug);
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Debug);
        UpdatePaneToggleStates();
        FocusPane(PaneKind.Debug);
    }
    private void UpdateDebugButtonVisibility()
        => DebugButton.Visibility = _idePaneApplicable ? Visibility.Visible : Visibility.Collapsed;
}
