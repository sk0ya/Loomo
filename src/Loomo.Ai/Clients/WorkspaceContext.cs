using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// 最新ユーザーメッセージ末尾へ添える「現在のフォルダ」情報を組み立てる。
/// 載せるのは<b>ワークスペースルートのみ</b>。毎ターン変わる揮発情報なので最小限にし、
/// システムプロンプト（安定プレフィックス）とは分離する。設定値そのものは変更しない。
/// </summary>
internal static class WorkspaceContext
{
    /// <summary>ユーザーメッセージ末尾へ連結する文字列を返す。フォルダ未オープン時は空。</summary>
    public static string Describe(IWorkspaceService workspace)
    {
        var root = workspace.RootPath;
        if (string.IsNullOrWhiteSpace(root)) return string.Empty;

        return $"\n\n# 現在のフォルダ\nワークスペースルート: {root}";
    }
}
