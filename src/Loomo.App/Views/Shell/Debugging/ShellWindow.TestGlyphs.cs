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
    private EditorTestGlyphController _testGlyphController = null!;
    private void InitializeTestGlyphWiring() {
        _testGlyphController = new EditorTestGlyphController(
            _workspace, TestExplorerForPath,
            path => EditorCoverageMarkerMapper.ForPath(path, _vm.Debug.Tests.CoverageFiles),
            Dispatcher, ToastService.Info, ToastService.Error);
        _vm.Debug.Tests.TestsChanged += _testGlyphController.SyncAll;
        _vm.Debug.Tests.CoverageChanged += _testGlyphController.SyncAll;
        _vm.TsIde.Tests.TestsChanged += _testGlyphController.SyncAll;
        // ワークスペースが変われば「どのファイルがテストソースか」の記憶も捨てる。
        _workspace.FoldersChanged += (_, _) => _testGlyphController.ResetColumns();
    }
    /// <summary>そのファイルのテストを持つエクスプローラ（.ts/.js 系→TS IDE、それ以外→dotnet IDE）。
    /// 振り分けはデバッグ（<see cref="ManagerForPath"/>）と同じ表を使う——同じファイルが両方に属することはない。</summary>
    private ITestExplorer TestExplorerForPath(string? path)
        => ReferenceEquals(ManagerForPath(path), _vm.TsIde) ? _vm.TsIde.Tests : _vm.Debug.Tests;
    private void WireEditorForTestGlyphs(VimEditorControl control) {
        _testGlyphController.WireEditor(control);
    }
    private void SyncEditorTestGlyphs(VimEditorControl control) => _testGlyphController.SyncEditor(control);
    /// <summary>キャレット行のテストを実行する（ショートカット／コマンドパレットの実体）。
    /// ▶ はマウス専用なので、キーボード・支援技術からの実行経路はこちらが受け持つ。</summary>
    private void RunTestAtCaret() {
        _testGlyphController.RunTestAtCaret(ActiveTestEditor());
    }
    /// <summary>キャレット行のテスト（コマンドパレットの出し分け用。無ければ null＝項目を出さない）。
    /// 対象エディタの決め方は意味的な選択（§24.9）と同じ <see cref="FocusedEditorControl"/> に揃える
    /// ——分割・切り離しでも「いま打っているエディタ」に効かせるため。</summary>
    private VimEditorControl? ActiveTestEditor()
        => FocusedEditorControl() ?? (_activeEditorTab is { IsRealized: true } tab ? tab.Control : null);

    private (ITestExplorer Explorer, TestItemViewModel Test)? ActiveEditorTestAtCaret()
        => _testGlyphController.TestAtCaret(ActiveTestEditor());
}
