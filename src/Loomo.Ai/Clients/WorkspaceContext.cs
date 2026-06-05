namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// システムプロンプトへ含める「現在のフォルダ」情報を組み立てる。
/// 載せるのは<b>ワークスペースルートのみ</b>。ルートはフォルダを開き直したときだけ変わる
/// 準安定値なので、システムプロンプト（安定プレフィックス）に含めてよい。設定値そのものは変更しない。
/// </summary>
internal static class WorkspaceContext
{
    /// <summary>システムプロンプト末尾へ連結する文字列を返す。フォルダ未オープン時は空。</summary>
    public static string Describe(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return string.Empty;

        return $"\n\n# 現在のフォルダ\nワークスペースルート: {root}";
    }
}
