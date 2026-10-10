namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: エディタの場所のナビゲーション（Rider の Navigate Back/Forward・Last Edit Location・
/// Recent Files・Recent Locations・File Structure 相当）。
/// <para>履歴はキャレットの動きだけから作る（<see cref="EditorLocationHistory"/>）。定義ジャンプ・問題一覧・
/// パレット・検索・タブ切替と「飛ぶ」経路は数十あり、経路ごとに記録を書くと必ず漏れるため。
/// 履歴はワークスペースごとに分ける——戻るで別ワークスペースのファイルが開くと、部屋を取り違える。</para>
/// <para>一覧系はすべてコマンドパレットに載せる（新しい窓を作らない）。項目は場所（<see cref="PaletteTarget"/>）を
/// 持つので、選んでいる間は右の欄に中身が出て、Enter で同じ場所へ飛ぶ。</para></summary>
public partial class ShellWindow {
    private sealed class LocationNavigationState {
        public EditorLocationHistory History { get; } = new();
        public EditLocationTrail Edits { get; } = new();
    }
    private readonly Dictionary<Guid, LocationNavigationState> _locationNavigation = new();
    private sk0ya.Loomo.Core.Abstractions.IWorkspaceSearchService? _workspaceSearch;
    /// <summary>いまパレットに出しているナビゲーション一覧（同じキーをもう一度押したときの切替先を決める）。</summary>
    private IReadOnlyList<PaletteCommand>? _recentFilesPaletteItems;

    private LocationNavigationState CurrentLocationNavigation {
        get {
            var key = _activeWorkspace?.Id ?? Guid.Empty;
            if (!_locationNavigation.TryGetValue(key, out var state))
                _locationNavigation[key] = state = new LocationNavigationState();
            return state;
        }
    }

    private void WireEditorForLocationHistory(VimEditorControl control) {
        control.CaretMoved += (_, caret) => {
            if (TryEditorLocation(control, caret, out var location))
                CurrentLocationNavigation.History.Record(location, DateTime.UtcNow);
        };
        control.BufferChanged += (_, _) => {
            // 人が打った編集だけを数える：読み込み・外部変更の再読込・裏のタブへの WorkspaceEdit は
            // フォーカスが無いか未変更状態なので落ちる。
            if (!control.IsKeyboardFocusWithin || !control.IsModified)
                return;
            if (TryEditorLocation(control, control.Caret, out var location))
                CurrentLocationNavigation.Edits.Record(location);
        };
    }

    /// <summary>アクティブなファイルが変わった：その時点のキャレットを履歴へ入れる。タブの切替や起動時の
    /// 復元はキャレットを動かさない（イベントが出ない）ので、これが無いと「切り替える前にいた場所」も
    /// 「起動直後にいた場所」も履歴に残らず、戻る先が無くなる。読み込みとキャレット復元が済んでから
    /// 読むため、ディスパッチャが落ち着いた時点まで遅らせる。</summary>
    private void SeedEditorLocationHistory(EditorTab tab) {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => {
            if (ReferenceEquals(_activeEditorTab, tab) && CurrentEditorLocation() is { } location)
                CurrentLocationNavigation.History.Record(location, DateTime.UtcNow);
        });
    }

    /// <summary>このコントロールのキャレットを「場所」として数えてよいか。仮想文書（差分・抜粋タブ等）と
    /// 裏のタブの動き（復元・外部編集の追従）は数えない。</summary>
    private bool TryEditorLocation(VimEditorControl control, CaretInfo caret, out EditorLocation location) {
        location = default;
        if (control.IsVirtualDocument || control.FilePath is not { Length: > 0 } path)
            return false;
        var isActive = _activeEditorTab is { IsRealized: true } tab && ReferenceEquals(tab.Control, control);
        if (!isActive && !control.IsKeyboardFocusWithin)
            return false;
        location = new EditorLocation(path, caret.Line, caret.Column);
        return true;
    }

    private EditorLocation? CurrentEditorLocation()
        => _activeEditorTab is { IsRealized: true } tab && TryEditorLocation(tab.Control, tab.Control.Caret, out var location)
            ? location
            : null;

    private static bool IsLocationAvailable(EditorLocation location) => File.Exists(location.Path);

    /// <summary>戻る（<c>nav.back</c>）／進む（<c>nav.forward</c>）。エディタの上のマウスの戻る／進むボタンもここ。</summary>
    private async Task NavigateEditorLocationAsync(bool back) {
        var history = CurrentLocationNavigation.History;
        if (CurrentEditorLocation() is { } here)
            history.Record(here, DateTime.UtcNow);
        var target = back ? history.GoBack(IsLocationAvailable) : history.GoForward(IsLocationAvailable);
        if (target is not { } location) {
            ShowEditorStatus(back ? "これ以上戻れる場所はありません" : "これ以上進める場所はありません");
            return;
        }
        history.BeginNavigation();
        try {
            await GoToEditorLocationAsync(location);
        }
        finally {
            // ファイルを開く経路はキャレットを二段で動かす（前回の位置→目的の行）うえ、後段が
            // ディスパッチャに積まれることもある。その分を流し切ってから記録を再開する。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, history.EndNavigation);
        }
    }

    /// <summary>最後に編集した場所へ（<c>nav.lastEditLocation</c>）。続けて押すと古い編集箇所へ遡る。</summary>
    private void GoToLastEditLocation() {
        var target = CurrentLocationNavigation.Edits.Previous(CurrentEditorLocation(), IsLocationAvailable);
        if (target is not { } location) {
            ShowEditorStatus("編集した場所の記録がありません");
            return;
        }
        _ = GoToEditorLocationAsync(location);
    }

    private async Task GoToEditorLocationAsync(EditorLocation location) {
        await OpenPathInEditorAsync(location.Path, location.Line + 1, location.Column + 1);
        FocusPane(PaneKind.Editor);
    }

    private void ShowEditorStatus(string message) {
        if (_activeEditorTab is { IsRealized: true } tab)
            tab.Control.ShowStatusMessage(message);
    }

    /// <summary>最近のファイル（<c>nav.recentFiles</c>）。開いている最中にもう一度押すと最近の場所へ切り替わる
    /// （パレットを開くキーと同じ流儀で、覚えるキーを1本に保つ）。</summary>
    private void OpenRecentFilesOrToggle() {
        if (IsPaletteOpen && _recentFilesPaletteItems is { } shown && ReferenceEquals(_paletteCommands, shown)) {
            _ = OpenRecentLocationsAsync();
            return;
        }
        var items = EditorNavigationPaletteItems.ForRecentFiles(
            _vm.Recent.RecentFiles.Select(item => item.FullPath).Where(File.Exists),
            ActiveEditorFilePath(), _workspace.ToDisplayPath, JumpToPaletteTarget);
        _recentFilesPaletteItems = items;
        OpenCommandPalette(PaletteMode.Command, items);
    }

    /// <summary>最近の場所（<c>nav.recentLocations</c>）。行の文面はディスクから読む（開いているタブの未保存の
    /// 変更は映らないが、どの場所かを見分けるには十分で、選べば右の欄が今の中身を出す）。</summary>
    private async Task OpenRecentLocationsAsync() {
        var history = CurrentLocationNavigation.History;
        if (CurrentEditorLocation() is { } here)
            history.Record(here, DateTime.UtcNow);
        var locations = history.RecentDistinct();
        var lines = await Task.Run(() => ReadLocationLines(locations));
        var items = EditorNavigationPaletteItems.ForLocations(
            locations, l => lines.TryGetValue(l, out var text) ? text : null,
            _workspace.ToDisplayPath, JumpToPaletteTarget);
        _recentFilesPaletteItems = null;
        OpenCommandPalette(PaletteMode.Command, items);
    }

    private static Dictionary<EditorLocation, string> ReadLocationLines(IReadOnlyList<EditorLocation> locations) {
        var result = new Dictionary<EditorLocation, string>();
        foreach (var group in locations.GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase)) {
            try {
                var wanted = group.ToList();
                var maxLine = wanted.Max(l => l.Line);
                var index = 0;
                foreach (var line in File.ReadLines(group.Key)) {
                    foreach (var location in wanted.Where(l => l.Line == index))
                        result[location] = line;
                    if (++index > maxLine)
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // 読めないファイルは「ファイル名:行」の表示に落ちる。
            }
        }
        return result;
    }

    /// <summary>ファイル構造（<c>nav.fileStructure</c>）。言語サーバーの documentSymbol を宣言順に並べ、
    /// キャレットを含む宣言を選んだ状態で開く。</summary>
    private async Task OpenFileStructureAsync() {
        if (_activeEditorTab is not { IsRealized: true } tab || tab.Control.FilePath is not { Length: > 0 } path)
            return;
        var control = tab.Control;
        if (control.LspDocument is not { IsReady: true } document) {
            control.ShowStatusMessage("ファイル構造：このファイルの言語サーバーが準備できていません");
            return;
        }
        var symbols = await CodeEditorSupportAnalysis.RequestDocumentSymbolsSafeAsync(document, CancellationToken.None);
        if (!ReferenceEquals(_activeEditorTab, tab))
            return;   // 待っている間に別のタブへ移った
        if (symbols.Symbols.Count == 0) {
            control.ShowStatusMessage(symbols.TimedOut
                ? "ファイル構造：言語サーバーが応答しませんでした"
                : "ファイル構造：宣言が見つかりませんでした");
            return;
        }
        var roots = CodeEditorSupport.ToOutline(symbols.Symbols, CodeEditorSupportAnalysis.SplitLines(control.Text));
        var items = EditorNavigationPaletteItems.ForStructure(roots, path, JumpToPaletteTarget);
        _recentFilesPaletteItems = null;
        OpenCommandPalette(PaletteMode.Command, items);
        var caret = control.Caret;
        var selected = EditorNavigationPaletteItems.IndexOfEnclosing(items, roots, caret.Line, caret.Column);
        if (selected >= 0) {
            PaletteList.SelectedIndex = selected;
            PaletteList.ScrollIntoView(PaletteList.SelectedItem);
        }
    }

    /// <summary>テスト⇔実装（<c>nav.relatedTest</c>）。命名規約（<see cref="RelatedTestFiles"/>）で相手の名前を出し、
    /// ワークスペース全フォルダーから探す。1件ならそのまま開き、複数ならフォルダーの近い順にパレットへ並べる。</summary>
    private async Task GoToRelatedTestAsync() {
        if (ActiveEditorFilePath() is not { Length: > 0 } path || _workspaceSearch is not { } search)
            return;
        var names = RelatedTestFiles.CounterpartNames(path);
        var isTest = RelatedTestFiles.IsTestFile(path);
        if (names.Count == 0) {
            ShowEditorStatus("テスト⇔実装：このファイルの種類には対応規則がありません（.cs／.ts・.js 系／.py）");
            return;
        }
        var results = await Task.WhenAll(names.Select(name =>
            search.FindFilesAsync(Path.GetFileNameWithoutExtension(name), 500, CancellationToken.None)));
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var ranked = RelatedTestFiles.RankByProximity(path, results.SelectMany(hits => hits)
            .Where(hit => wanted.Contains(Path.GetFileName(hit.FullPath)))
            .Select(hit => hit.FullPath));
        if (ranked.Count == 0) {
            ShowEditorStatus($"{(isTest ? "実装" : "テスト")}が見つかりません（{string.Join("／", names.Take(3))}）");
            return;
        }
        if (ranked.Count == 1) {
            await OpenPaletteTargetAsync(new PaletteTarget(ranked[0]));
            return;
        }
        var items = EditorNavigationPaletteItems.ForRecentFiles(ranked, path, _workspace.ToDisplayPath, JumpToPaletteTarget);
        _recentFilesPaletteItems = null;
        OpenCommandPalette(PaletteMode.Command, items);
    }
}
