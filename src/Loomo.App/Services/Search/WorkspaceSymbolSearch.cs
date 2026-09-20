using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 検索ペインのクラス／シンボルスコープの入口。ワークスペースの LSP セッションへそのまま問い合わせる。
///
/// <para>以前は「開いているエディタタブを走査して接続済みの LSP マネージャーを集める」実装だったため、
/// 検索結果が**そのとき何のタブを開いているかで変わり**、<c>.cs</c> タブが1枚も無ければ 0 件だった。
/// <see cref="ILspWorkspace"/> はタブに依存しないので、その意味の壊れ方が消えている
/// （マージ・重複排除もセッション側の責務になった）。</para>
/// </summary>
public static class WorkspaceSymbolSearch
{
    public static async Task<IReadOnlyList<LspSymbolInformation>> SearchAsync(
        ILspWorkspace workspace, string query, bool isClass, CancellationToken ct)
    {
        try { return await workspace.GetWorkspaceSymbolsAsync(query, isClass, ct); }
        catch { return []; }
    }
}
