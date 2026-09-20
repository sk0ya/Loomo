using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.Views;

/// <summary>ナビゲーション結果を、ワークスペース／プロジェクト／外部ソース付きで表示する情報。</summary>
public sealed record NavigationLocationDisplay(
    string DisplayPath,
    string Scope,
    bool IsExternalSource)
{
    public string Format(int line, int column)
        => $"{DisplayPath}:{line + 1}:{column + 1} [{Scope}]";
}

/// <summary>LSPのファイル位置をLoomoのワークスペース／C#プロジェクト文脈へ写像する。</summary>
public static class NavigationLocationFormatter
{
    public static NavigationLocationDisplay Resolve(
        string filePath,
        IReadOnlyList<string> workspaceFolders,
        SolutionModel? solution)
    {
        // file: 以外のURIはローカルパスへ変換せず、外部ソースとしてそのまま表示する。
        if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && !uri.IsFile)
            return new NavigationLocationDisplay(filePath, "外部ソース", true);

        var project = solution?.ProjectForFile(filePath);
        var isExternal = !WorkspacePaths.Contains(workspaceFolders, filePath);
        var scope = project?.Name ?? (isExternal ? "外部ソース" : "ワークスペース");
        return new NavigationLocationDisplay(
            WorkspacePaths.ToDisplayPath(workspaceFolders, filePath), scope, isExternal);
    }
}
