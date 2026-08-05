using System;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Core.Files;

/// <summary>リンクの宛先の種別。</summary>
public enum LinkOpenTargetKind
{
    /// <summary>開ける宛先が無い（空・未解決・別ウィンドウでは扱えないスキーム）。</summary>
    None,

    /// <summary>ブラウザで開く URL（http/https）。</summary>
    Url,

    /// <summary>エディタで開く既存ファイル。</summary>
    File,

    /// <summary>既存フォルダー（別ウィンドウでは開かず、フォルダーツリーの選択に使う）。</summary>
    Directory,
}

/// <summary>リンクの解決結果。<see cref="Line"/>／<see cref="Column"/> は 1 始まり（0＝指定なし）。</summary>
public readonly record struct LinkOpenTarget(LinkOpenTargetKind Kind, string Value, int Line, int Column)
{
    public static LinkOpenTarget None { get; } = new(LinkOpenTargetKind.None, "", 0, 0);
}

/// <summary>
/// 本文中のリンク（エディタが検知した span、EditorSupport の <c>&lt;a href&gt;</c>）を
/// 「ブラウザで開く URL」か「エディタで開くファイル」へ振り分ける。クリック時の振り分けと
/// 右クリックの「別ウィンドウで開く」が同じ判断を共有するための一段。
/// </summary>
public static class LinkOpenTargetResolver
{
    /// <summary>ワークスペースの中で振り分ける。<b>ホストはこちらを使うこと</b>——
    /// 基準フォルダーは <paramref name="sourceDocumentPath"/> から導くので取り違えようがない
    /// （§32.10.1）。</summary>
    public static LinkOpenTarget Resolve(
        IWorkspaceService workspace, string? href, string? sourceDocumentPath)
        => Resolve(href, sourceDocumentPath, workspace.FolderForOrPrimary(sourceDocumentPath));

    /// <summary>基準フォルダーを明示して振り分ける素の関数。</summary>
    public static LinkOpenTarget Resolve(string? href, string? sourceDocumentPath, string? baseFolder)
    {
        if (string.IsNullOrWhiteSpace(href))
            return LinkOpenTarget.None;

        var target = href.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return new LinkOpenTarget(LinkOpenTargetKind.Url, uri.AbsoluteUri, 0, 0);
            if (uri.IsFile)
                target = uri.LocalPath;   // file:// は実在確認を通したいのでパスへ戻す
            else
                return LinkOpenTarget.None;   // mailto: 等は別ウィンドウの担当外（既定の外部起動に任せる）
        }

        if (!FileLinkResolver.TryResolve(
                target, sourceDocumentPath, baseFolder,
                out var fullPath, out var line, out var column, out var isDirectory))
            return LinkOpenTarget.None;

        return new LinkOpenTarget(
            isDirectory ? LinkOpenTargetKind.Directory : LinkOpenTargetKind.File, fullPath, line, column);
    }
}
