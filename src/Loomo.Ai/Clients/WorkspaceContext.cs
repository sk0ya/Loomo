using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// システムプロンプトへ含める「現在のフォルダ」情報を組み立てる。
/// ワークスペースフォルダーの集合（複数フォルダーワークスペースなら全件）を載せる。フォルダー集合は
/// フォルダーを開き直す／追加・削除したときだけ変わる準安定値なので、システムプロンプト
/// （安定プレフィックス）に含めてよい。設定値そのものは変更しない。
/// </summary>
internal static class WorkspaceContext
{
    /// <summary>システムプロンプト末尾へ連結する文字列を返す。フォルダ未オープン時は空。</summary>
    public static string Describe(IReadOnlyList<string> folders)
    {
        if (folders.Count == 0)
            return string.Empty;

        if (folders.Count == 1)
            return $"\n\n# 現在のフォルダ\nワークスペースルート: {folders[0]}";

        var sb = new StringBuilder();
        sb.Append("\n\n# 現在のフォルダ（複数フォルダーワークスペース）\n");
        sb.Append("ワークスペースルート（相対パスの基準）: ").Append(folders[0]).Append('\n');
        sb.Append("追加フォルダー（相対パス解決の基準はワークスペースルートのみなので、");
        sb.Append("これらの配下を指すときは絶対パスを使うこと）:\n");
        foreach (var folder in folders.Skip(1))
            sb.Append("- ").Append(folder).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }
}
