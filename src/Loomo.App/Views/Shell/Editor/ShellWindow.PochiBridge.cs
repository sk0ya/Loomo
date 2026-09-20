namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: EditorSupport ペインに載せた Pochi（<c>.pochi.json</c> のキャンバス）との postMessage
/// ブリッジ。Pochi は <c>{id, op, …}</c> を送って <c>{id, result}</c> の応答を待つ（Pochi 本体の desktop
/// シェル <c>desktop/MainWindow.xaml.cs</c> と同じ規約）。
///
/// 図面データはファイルへ直接ではなく<b>エディタタブの本文</b>を経由させる——そうすることで、Loomo 側の
/// 未保存表示・保存フロー（タブの dirty・:w）にそのまま乗り、Pochi 側の変更を他のペイン（差分・Git）も
/// 通常の編集と同じに見る。<see cref="PochiEditorSupport"/> も参照。
/// </summary>
public partial class ShellWindow {
    /// <summary>
    /// Pochi からの <c>op</c> メッセージを処理して <c>{id, result}</c> を返す。応答するのは Pochi を
    /// 載せているとき（対象ファイルが .pochi.json）だけで、他のプレビューページからの名乗り出には
    /// <c>result: null</c> を返す＝Pochi 側は「ホスト無し」と判断して web ビルドのまま動く。
    /// </summary>
    private void HandlePochiBridgeMessage(CoreWebView2 core, JsonElement root, string? op) {
        var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var i) ? i : 0;
        object? result = null;
        var tab = _editorSupport.Source;
        var filePath = tab?.Control.FilePath;
        if (tab is not null && filePath is not null
            && _editorSupports.Resolve(filePath) is PochiEditorSupport) {
            switch (op) {
                case "hello":
                    result = new { app = "loomo", version = 1, ops = PochiEditorSupport.Ops };
                    break;
                case "hostDoc":
                    result = new { name = filePath, content = tab.Control.Text };
                    break;
                case "hostDocChanged":
                    result = TryApplyPochiDoc(tab, root);
                    break;
                case "hostSave":
                    if (TryApplyPochiDoc(tab, root)) {
                        tab.Control.Save();
                        result = true;
                    }
                    break;
            }
        }
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result })); }
        catch { }
    }

    /// <summary>
    /// Pochi が送ってきた図面 JSON をエディタ本文へ書き戻す。内容が同じなら何もしない——Pochi は編集の
    /// たびに送ってくるので、同値の <c>SetText</c> でカーソルやアンドゥ履歴を無駄に揺らさないため。
    /// </summary>
    private static bool TryApplyPochiDoc(EditorTab tab, JsonElement root) {
        if (!root.TryGetProperty("content", out var contentElement)
            || contentElement.ValueKind != JsonValueKind.String
            || contentElement.GetString() is not { } content)
            return false;
        if (tab.Control.Text == content)
            return true;
        tab.Control.SetText(content);
        return true;
    }
}
