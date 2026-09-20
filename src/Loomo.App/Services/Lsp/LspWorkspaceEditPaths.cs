using System;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Lsp;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// workspace edit（rename／複数ファイルに及ぶコードアクション／シグネチャ変更）の対象 URI を、
/// 実際に書き換えてよいワークスペース内の絶対パスへ解決する。<see cref="Views.ShellWindow"/> の
/// イベントハンドラから切り出してあるのは、ここが**壊れてもエラー文言しか出ない**静かな失敗点だから
/// （テスト可能にする）。
///
/// <para>ワークスペース内かどうかの判定そのものは <see cref="WorkspacePaths"/> が正本。
/// ここは URI→パス変換と、拒否したときの文言だけを持つ。</para>
///
/// <para>URI→パス変換は必ず <see cref="LspUri"/> 経由。<c>new Uri(uri).LocalPath</c> 直読みだと
/// tsserver 系の <c>file:///c%3A/…</c> が <c>/c:/…</c> になり、<c>Path.GetFullPath</c> が
/// <c>C:\c:\…</c> という実在しないパスに変えてしまう（TypeScript の rename が全滅していた原因）。</para>
/// </summary>
internal static class LspWorkspaceEditPaths
{
    /// <summary>
    /// 対象 URI を、いずれかのワークスペースフォルダー配下の絶対パスへ解決する。
    /// file URI でない／どのフォルダーにも属さないなら <see cref="InvalidOperationException"/>
    /// （そのままユーザーに見せる文言）。
    /// </summary>
    internal static string ResolveInWorkspace(string uri, IReadOnlyList<string> workspaceFolders)
    {
        var local = LspUri.TryToLocalPath(uri)
            ?? throw new InvalidOperationException($"ファイルとして扱えない文書は編集できません: {uri}");
        var path = Path.GetFullPath(local);
        if (!WorkspacePaths.Contains(workspaceFolders, path))
            throw new InvalidOperationException($"ワークスペース外の文書は編集できません: {path}");
        return path;
    }
}
