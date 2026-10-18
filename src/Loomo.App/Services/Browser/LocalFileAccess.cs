namespace sk0ya.Loomo.App.Services;

/// <summary>
/// <see cref="LocalFileServer"/> が「この要求に答えてよいか」を決める規則（WebView2 に依らない部分）。
///
/// <para>ローカルの HTML はすべて同じオリジン（<c>https://file.loomo</c>）になるので、オリジンで線を引くと
/// <b>開いた HTML がディスク中のファイルを <c>fetch()</c> で読めてしまう</b>（<c>file://</c> ではオリジンが
/// <c>null</c> で Chromium が止めていた）。ヘッダーにも頼れない——WebView2 の <c>WebResourceRequested</c> には
/// <c>Sec-Fetch-*</c> が来ず、<c>referrerPolicy: 'no-referrer'</c> の <c>fetch</c> は <c>Origin</c> も
/// <c>Referer</c> も無しで届く（2026-10-18 実測）。</para>
///
/// <para>そこで WebView2 ごとに「いま表に出ているページ」を覚え、<b>トップレベルの遷移そのもの以外は、
/// そのページの範囲の中だけ</b>答える。範囲は、ページを担当するワークスペースフォルダー（マルチルートなら
/// そのファイルのフォルダー）、ワークスペースの外ならページのあるフォルダー。ネットのページが表に出ている間は、
/// 部品としての読み出し（<c>&lt;img&gt;</c>・<c>&lt;iframe&gt;</c>・<c>fetch</c>）には一切答えない。</para>
/// </summary>
internal static class LocalFileAccess
{
    /// <summary>ページ <paramref name="pagePath"/> が部品として読んでよい範囲（フォルダー）。</summary>
    public static string ScopeOf(string pagePath, Func<string, string?>? workspaceFolderFor)
    {
        var folder = workspaceFolderFor?.Invoke(pagePath);
        return string.IsNullOrEmpty(folder) ? Path.GetDirectoryName(pagePath) ?? pagePath : folder;
    }

    /// <summary><paramref name="path"/> が <paramref name="scope"/> の中か（区切りまで見る：<c>C:\app2</c> は
    /// <c>C:\app</c> の中ではない）。</summary>
    public static bool IsWithin(string? scope, string path)
    {
        if (string.IsNullOrEmpty(scope))
            return false;
        var root = Path.TrimEndingDirectorySeparator(scope);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>トップレベルの遷移を通してよいか。ネットのページが<b>スクリプトで</b>ローカルファイルへ
    /// 飛ばすのは止める（仕込まれた HTML を開かせて、その範囲を読ませる口になる）。人のクリックと
    /// Loomo 自身の遷移（WebView2 の API 経由はユーザー操作扱い）は通す。</summary>
    public static bool AllowsTopLevelNavigation(bool fromLocalPage, bool isUserInitiated)
        => fromLocalPage || isUserInitiated;
}
