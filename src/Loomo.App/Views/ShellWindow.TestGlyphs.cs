namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: エディタのガターに出す「テスト実行 ▶ ／結果」グリフの配線（ブレークポイント列と同じ流儀）。 テストの正本はテストエクスプローラの VM（dotnet=<see cref="ViewModels.DebugTestsViewModel"/> / TypeScript=<see cref="ViewModels.TsDebugTestsViewModel"/>）が持ち、エディタは表示して押されたことを伝えるだけ。 ファイルの管轄は <see cref="ManagerForPath"/> と同じ拡張子で振り分ける。
/// <para><b>再送の契機は 3 つ。</b>(1) テスト一覧・状態が変わったとき（<c>TestsChanged</c>＝走査の再収集・実行の開始と完了）、 (2) 本文が変わったとき（<c>BufferChanged</c>＝編集。Background 優先度で1フレームぶんにまとめる）、 (3) <b>ファイルを読み込んだとき</b>（<see cref="LoadEditorFile"/>／セッション復元）。
/// (3) が要るのは、<c>VimEditorControl.LoadFile</c> がグリフを捨てるのに <c>BufferChanged</c> を<b>発火しない</b>ため。 これを落とすと、外部変更の読み直し（<c>ReloadExistingTabIfChangedAsync</c>——ブランチ切替・一括置換・ 既存タブを開き直したとき）でガターが空のまま残る。<b><see cref="LoadEditorFile"/> が Loomo 側の唯一の漏斗</b>なので、 <c>control.LoadFile</c> を直に呼ぶ経路を増やさないこと（増やすならそこでも再送する）。 内容が同じなら Editor 側が no-op にするので、送りすぎる分には害がない。</para>
/// <para><b>列の出し入れ。</b>テストソースでないファイルではガター列を無効化する（列幅 0＝本文左端を動かさない）。 いったんテストが見つかったファイルでは 0 件になっても畳まない——編集途中でパーサが拾えなくなるたびに 列が開閉して本文が左右に動くため（<see cref="EditorTestGlyphColumns"/>）。</para>
/// <para><b>切り離しウィンドウ。</b>複製エディタも <c>BuildEditorControl</c> を通るので同じ配線が乗り、 <see cref="LoadEditorFile"/> 経由で初回のグリフも出る。ただしメインのタブ一覧（<c>_editorTabs</c>）には 居ないため、配線したコントロールを弱参照で控えて一斉再送の宛先にする。</para></summary>
public partial class ShellWindow {
    /// <summary>テストグリフを配線済みのエディタ（切り離し窓の複製も含む）。参照は弱く持ち、
    /// 死んだものは再送のたびに掃除する——閉じた窓のコントロールを掴んで生かし続けないため。
    /// <para>ブレークポイント側の <see cref="RealizedEditorControls"/>（<c>_editorTabs</c> 由来）とは
    /// <b>母集団が違う</b>（あちらは切り離し窓を含まない）。1 本に寄せるとブレークポイントの挙動まで
    /// 変わるので、統合は別の変更に分けている。</para></summary>
    private readonly List<WeakReference<VimEditorControl>> _testGlyphEditors = new();

    /// <summary>ガターのテスト列を出すかどうかの記憶（ファイル単位）。</summary>
    private readonly EditorTestGlyphColumns _testGlyphColumns = new();

    /// <summary>再送待ちのエディタ（打鍵ごとの再送を 1 フレームぶんにまとめる）。</summary>
    private readonly HashSet<VimEditorControl> _testGlyphSyncPending = new();
    private void InitializeTestGlyphWiring() {
        _vm.Debug.Tests.TestsChanged += SyncAllEditorTestGlyphs;
        _vm.TsIde.Tests.TestsChanged += SyncAllEditorTestGlyphs;
        // ワークスペースが変われば「どのファイルがテストソースか」の記憶も捨てる。
        _workspace.FoldersChanged += (_, _) => _testGlyphColumns.Reset();
    }
    /// <summary>そのファイルのテストを持つエクスプローラ（.ts/.js 系→TS IDE、それ以外→dotnet IDE）。
    /// 振り分けはデバッグ（<see cref="ManagerForPath"/>）と同じ表を使う——同じファイルが両方に属することはない。</summary>
    private ITestExplorer TestExplorerForPath(string? path)
        => ReferenceEquals(ManagerForPath(path), _vm.TsIde) ? _vm.TsIde.Tests : _vm.Debug.Tests;
    private void WireEditorForTestGlyphs(VimEditorControl control) {
        control.TestGlyphClicked += line0 => OnEditorTestGlyphClicked(control, line0);
        control.BufferChanged += (_, _) => ScheduleEditorTestGlyphSync(control);
        _testGlyphEditors.Add(new WeakReference<VimEditorControl>(control));
        SyncEditorTestGlyphs(control);
    }
    /// <summary>打鍵からの再送。1 文字ごとに全テストを舐め直さないよう、Background 優先度で
    /// 1 フレームぶんにまとめる（同じコントロールへの重複要求は 1 回に畳む）。</summary>
    private void ScheduleEditorTestGlyphSync(VimEditorControl control) {
        if (!_testGlyphSyncPending.Add(control))
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
            _testGlyphSyncPending.Remove(control);
            TrySyncEditorTestGlyphs(control);
        }));
    }
    /// <summary>配線済みの全エディタへ送り直す（テスト一覧・状態が変わったとき）。
    /// 破棄済みのコントロール（閉じた切り離し窓・閉じたタブ）は、GC が弱参照を消すより先に呼ばれ得るので
    /// その場で一覧から外す。<b>それ以外の例外では外さない</b>——一過性の失敗で生きているエディタを
    /// 台帳から永久追放すると、タブを開き直すまでグリフが戻らなくなる。</summary>
    private void SyncAllEditorTestGlyphs() {
        for (var i = _testGlyphEditors.Count - 1; i >= 0; i--) {
            if (_testGlyphEditors[i].TryGetTarget(out var control)) {
                if (!TrySyncEditorTestGlyphs(control))
                    _testGlyphEditors.RemoveAt(i);
            } else {
                _testGlyphEditors.RemoveAt(i);
            }
        }
    }
    /// <summary>1 つのエディタへ送る。破棄済みだったら false（台帳から外してよい）。</summary>
    private bool TrySyncEditorTestGlyphs(VimEditorControl control) {
        try { SyncEditorTestGlyphs(control); }
        catch (ObjectDisposedException) { return false; }
        catch (Exception ex) {
            // 生きているエディタの一過性の失敗。次の契機で回復できるよう台帳には残す。
            Trace.WriteLine($"[TestGlyphs] グリフの再送に失敗しました: {ex}");
        }
        return true;
    }
    /// <summary>1 つのエディタのガターを、そのファイルの現在のテスト状態へ合わせる。</summary>
    private void SyncEditorTestGlyphs(VimEditorControl control) {
        var path = control.FilePath;
        var glyphs = EditorTestGlyphMap.Build(_workspace, TestExplorerForPath(path).TestItems, path);
        control.SetTestGlyphsEnabled(_testGlyphColumns.ShouldEnable(path, glyphs.Count));
        control.SetTestGlyphs(glyphs);
    }
    /// <summary>ガターの ▶ が押された：その行のテストだけを実行する（1 行に複数あれば順に）。
    /// 実行中／完了のグリフはテスト側の状態変化（<c>TestsChanged</c>）が運んでくるので、ここでは触らない。</summary>
    private void OnEditorTestGlyphClicked(VimEditorControl control, int line0) {
        var path = control.FilePath;
        var explorer = TestExplorerForPath(path);
        _ = RunTestsFromEditorAsync(explorer, EditorTestGlyphMap.TestsAt(_workspace, explorer.TestItems, path, line0));
    }
    /// <summary>エディタ発（ガターの ▶ ／コマンドパレット／ショートカット）のテスト実行。
    /// 結果と失敗の文言はテストペイン側（ステータス・出力）が受け持つが、<b>始められなかった</b>ときと
    /// 想定外の例外はここでトーストにする——押したのに無反応、を作らないため。</summary>
    private async Task RunTestsFromEditorAsync(ITestExplorer explorer, IReadOnlyList<TestItemViewModel> tests) {
        if (tests.Count == 0) {
            ToastService.Info("カーソル行にテストがありません。");
            return;
        }
        foreach (var test in tests) {
            try {
                if (!await explorer.RunTestAsync(test))
                    ToastService.Info($"実行中のため、テストを開始できません: {test.DisplayName}");
            }
            catch (Exception ex) { ToastService.Error($"テストを実行できませんでした: {ex.Message}"); }
        }
    }
    /// <summary>キャレット行のテストを実行する（ショートカット／コマンドパレットの実体）。
    /// ▶ はマウス専用なので、キーボード・支援技術からの実行経路はこちらが受け持つ。</summary>
    private void RunTestAtCaret() {
        if (ActiveEditorTestAtCaret() is { } target)
            _ = RunTestsFromEditorAsync(target.Explorer, [target.Test]);
        else
            ToastService.Info("カーソル行にテストがありません。");
    }
    /// <summary>キャレット行のテスト（コマンドパレットの出し分け用。無ければ null＝項目を出さない）。
    /// 対象エディタの決め方は意味的な選択（§24.9）と同じ <see cref="FocusedEditorControl"/> に揃える
    /// ——分割・切り離しでも「いま打っているエディタ」に効かせるため。</summary>
    private (ITestExplorer Explorer, TestItemViewModel Test)? ActiveEditorTestAtCaret() {
        var control = FocusedEditorControl() ?? (_activeEditorTab is { IsRealized: true } tab ? tab.Control : null);
        if (control is null) return null;
        var path = control.FilePath;
        var explorer = TestExplorerForPath(path);
        return EditorTestGlyphMap.TestForCaret(_workspace, explorer.TestItems, path, control.Caret.Line) is { } test
            ? (explorer, test)
            : null;
    }
}
