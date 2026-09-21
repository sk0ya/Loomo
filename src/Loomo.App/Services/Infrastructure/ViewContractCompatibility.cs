using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.Views;

/// <summary>移動した処理の旧View契約をサービス実装へ委譲する。</summary>
public sealed record NavigationLocationDisplay(string DisplayPath, string Scope, bool IsExternalSource)
{
    public string Format(int line, int column) => $"{DisplayPath}:{line + 1}:{column + 1} [{Scope}]";
}

public static class NavigationLocationFormatter
{
    public static NavigationLocationDisplay Resolve(
        string filePath, IReadOnlyList<string> workspaceFolders, SolutionModel? solution)
    {
        var location = NavigationLocationResolver.Resolve(filePath, workspaceFolders, solution);
        return new NavigationLocationDisplay(location.DisplayPath, location.Scope, location.IsExternalSource);
    }
}

public static class NavigationSourceContext
{
    public static string Read(string filePath, int line, int radius = 2, int maxLineCharacters = 240)
        => NavigationSourceReader.Read(filePath, line, radius, maxLineCharacters);
}

internal static class ExtensionPageBridge
{
    public const string OpenPageKind = ExtensionPageBridgeService.OpenPageKind;
    public const string Script = ExtensionPageBridgeService.Script;
    public static bool TryReadOpenRequest(string? message, string? source, out string url)
        => ExtensionPageBridgeService.TryReadOpenRequest(message, source, out url);
    public static bool IsExtensionPage(string? url) => ExtensionPageBridgeService.IsExtensionPage(url);
}
