using System;
using System.IO;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// workspace edit（rename／複数ファイルに及ぶコードアクション）の対象 URI を、実際に書き換えて
/// よいワークスペース内の絶対パスへ解決する。<see cref="Views.ShellWindow"/> のイベントハンドラから
/// 切り出してあるのは、ここが**壊れてもエラー文言しか出ない**静かな失敗点だから（テスト可能にする）。
///
/// <para>URI→パス変換は必ず <see cref="LspUri"/> 経由。<c>new Uri(uri).LocalPath</c> 直読みだと
/// tsserver 系の <c>file:///c%3A/…</c> が <c>/c:/…</c> になり、<c>Path.GetFullPath</c> が
/// <c>C:\c:\…</c> という実在しないパスに変えてしまう（TypeScript の rename が全滅していた原因）。</para>
/// </summary>
internal static class LspWorkspaceEditPaths
{
    /// <summary>
    /// 対象 URI をワークスペース内の絶対パスへ解決する。file URI でない／ワークスペース外なら
    /// <see cref="InvalidOperationException"/>（そのままユーザーに見せる文言）。
    /// </summary>
    internal static string ResolveInWorkspace(string uri, string workspaceRoot)
    {
        var local = LspUri.TryToLocalPath(uri)
            ?? throw new InvalidOperationException($"ファイルとして扱えない文書は編集できません: {uri}");
        var path = Path.GetFullPath(local);
        if (!path.StartsWith(RootPrefix(workspaceRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ワークスペース外の文書は編集できません: {path}");
        return path;
    }

    /// <summary>ルート直下判定用に、末尾を区切り文字1個へ揃えたルート。</summary>
    internal static string RootPrefix(string workspaceRoot) =>
        Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
