using System.Net;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Core.Markdown;

namespace sk0ya.Loomo.App.Services;

/// <summary>「この場所へのリンクをコピー」（§24.59）。貼る先がリッチテキストならリンク、プレーンテキストなら Markdown
/// （ブラウザのタブの「リンクをコピー」と同じ作り）。題はリストに出す書き方のパスと行（<c>src/App.cs:42</c>）。</summary>
internal static class LoomoLinkClipboard
{
    public static (string Url, string Markdown, string Html) Format(IWorkspaceService workspace, string file, int line, int column)
    {
        var folder = workspace.Contains(file) ? workspace.FolderFor(file) : null;
        var url = LoomoLink.Build(folder, file, line, column);
        var title = workspace.ToDisplayPath(file).Replace('\\', '/') + (line > 0 ? $":{line}" : "");
        var markdown = $"[{EscapeText(title)}]({MarkdownLinkParser.EncodeDestination(url)})";
        var html = $"<a href=\"{WebUtility.HtmlEncode(url)}\">{WebUtility.HtmlEncode(title)}</a>";
        return (url, markdown, html);
    }

    /// <summary>写して、<c>loomo://</c> の登録を済ませる。登録できなかったら false（リンクは写してある）。</summary>
    public static bool Copy(IWorkspaceService workspace, string file, int line, int column)
    {
        var (_, markdown, html) = Format(workspace, file, line, column);
        ClipboardText.SetLink(markdown, html);
        return LoomoLinkProtocol.EnsureRegistered();
    }

    /// <summary>コミットへのリンク（§24.74）。題は「リポジトリ名@短縮ハッシュ 件名」。</summary>
    public static (string Url, string Markdown, string Html) FormatCommit(
        IWorkspaceService workspace, string repository, string hash, string? subject)
    {
        var folder = workspace.Contains(repository) ? workspace.FolderFor(repository) : null;
        var url = LoomoLink.BuildCommit(folder, repository, hash);
        var name = WorkspacePaths.FolderName(repository);
        var title = $"{name}@{hash[..Math.Min(hash.Length, 7)]}" + (string.IsNullOrWhiteSpace(subject) ? "" : $" {subject.Trim()}");
        var markdown = $"[{EscapeText(title)}]({MarkdownLinkParser.EncodeDestination(url)})";
        var html = $"<a href=\"{WebUtility.HtmlEncode(url)}\">{WebUtility.HtmlEncode(title)}</a>";
        return (url, markdown, html);
    }

    public static bool CopyCommit(IWorkspaceService workspace, string repository, string hash, string? subject)
    {
        var (_, markdown, html) = FormatCommit(workspace, repository, hash, subject);
        ClipboardText.SetLink(markdown, html);
        return LoomoLinkProtocol.EnsureRegistered();
    }

    private static string EscapeText(string title)
        => title.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
}
