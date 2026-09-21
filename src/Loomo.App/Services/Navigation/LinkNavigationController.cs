using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>ターミナルのハイパーリンクをブラウザー／エディタの宛先へ解決する。</summary>
internal static class TerminalLinkTargetResolver
{
    public static LinkOpenTarget Resolve(
        IWorkspaceService workspace, string? target, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(target))
            return LinkOpenTarget.None;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return new(LinkOpenTargetKind.Url, uri.AbsoluteUri, 0, 0);

            // Windows パスに行番号が付いている場合は URI の LocalPath へ渡さず、
            // SourceLocationResolver に行・列の解釈を任せる。
            if (uri.IsFile && !IsWindowsPathTarget(target))
                return new(LinkOpenTargetKind.File, uri.LocalPath, 0, 0);

            if (!IsWindowsPathTarget(target))
                return LinkOpenTarget.None;
        }

        return SourceLocationResolver.TryResolve(
                workspace, target, workingDirectory, currentDocumentPath: null, out var location)
            ? new(LinkOpenTargetKind.File, location.Path, location.Line, location.Column)
            : LinkOpenTarget.None;
    }

    public static bool IsWindowsPathTarget(string target)
        => target.Length >= 3 && char.IsLetter(target[0]) && target[1] == ':' &&
           (target[2] == '\\' || target[2] == '/');
}

/// <summary>ブラウザーで開くローカルファイルの表示先を解決する。</summary>
internal readonly record struct FileBrowserLinkTarget(string Url, string Title);

internal static class FileBrowserLinkTargetResolver
{
    public static bool TryResolve(string? path, out FileBrowserLinkTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var fullPath = Path.GetFullPath(path);
        target = new FileBrowserLinkTarget(new Uri(fullPath).AbsoluteUri, Path.GetFileName(fullPath));
        return true;
    }
}

/// <summary>EditorSupport リンクを既存のリンク解決規則へ通し、表示先の副作用だけ呼び出し側へ渡す。</summary>
internal static class EditorSupportLinkController
{
    public static async Task OpenAsync(
        IWorkspaceService workspace,
        string? href,
        string? sourcePath,
        Func<string, Task> openUrl,
        Func<string, int, int, Task> openFile,
        Action<string> selectDirectory,
        Action<string> openExternal)
    {
        if (string.IsNullOrWhiteSpace(href))
            return;

        if (Uri.TryCreate(href.Trim(), UriKind.Absolute, out var fileUri) && fileUri.IsFile)
        {
            await openFile(fileUri.LocalPath, 0, 0);
            return;
        }

        var target = LinkOpenTargetResolver.Resolve(workspace, href, sourcePath);
        switch (target.Kind)
        {
            case LinkOpenTargetKind.Url:
                await openUrl(target.Value);
                return;
            case LinkOpenTargetKind.File:
                await openFile(target.Value, target.Line, target.Column);
                return;
            case LinkOpenTargetKind.Directory:
                selectDirectory(target.Value);
                return;
        }

        // file: URI は上で扱った。その他の絶対 URI は外部ハンドラーへ送る。
        if (!Uri.TryCreate(href.Trim(), UriKind.Absolute, out var uri))
            return;
        openExternal(uri.AbsoluteUri);
    }
}
