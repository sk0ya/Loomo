using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 接続中の LSP マネージャーを横断してワークスペースシンボルを検索する。開いているコードタブぶんの
/// 言語サーバーへ問い合わせ、同じシンボル（名前・URI・行が一致）は重複排除して1件にまとめる。
/// 検索ペインのクラス／シンボルスコープが使う。
/// </summary>
public static class WorkspaceSymbolSearch
{
    /// <summary>アクティブ＋開いている各エディタタブのうち、コード編集用言語サーバーへ接続済みのものを返す
    /// （タブごとに重複しないよう、同一マネージャーは1つにまとめる）。</summary>
    internal static IReadOnlyList<IEditorLspManager> ConnectedManagers(
        Views.EditorTab? active, IReadOnlyList<Views.EditorTab> tabs,
        CodeEditorSupport support, Func<Views.EditorTab, IEditorLspManager?> getManager)
    {
        var seen = new HashSet<IEditorLspManager>();
        var result = new List<IEditorLspManager>();
        void Add(Views.EditorTab? tab)
        {
            if (tab is null || !support.CanHandle(tab.PeekFilePath)) return;
            var manager = getManager(tab);
            if (manager is { IsConnected: true } && seen.Add(manager)) result.Add(manager);
        }
        Add(active);
        foreach (var tab in tabs) Add(tab);
        return result;
    }

    public static async Task<IReadOnlyList<LspSymbolInformation>> MergeAsync(
        IReadOnlyList<IEditorLspManager> managers, string query, bool isClass, CancellationToken ct)
    {
        var merged = new List<LspSymbolInformation>();
        var seen = new HashSet<(string Name, string Uri, int Line)>();
        foreach (var manager in managers)
        {
            IReadOnlyList<LspSymbolInformation> symbols;
            try { symbols = await manager.GetWorkspaceSymbolsAsync(query, isClass, ct); }
            catch { continue; }
            if (ct.IsCancellationRequested)
                break;
            foreach (var symbol in symbols)
            {
                var key = (symbol.Name ?? "", symbol.Location?.Uri ?? "", symbol.Location?.Range?.Start?.Line ?? 0);
                if (seen.Add(key))
                    merged.Add(symbol);
            }
        }
        return merged;
    }
}
