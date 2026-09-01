using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.CSharp.Projects;

/// <summary>
/// C# の構文／意味ベースの編集操作へ渡す共有ワークスペース状態。
/// 現在の未保存本文、選択TFMごとの構文設定、ProjectReferenceを辿ったソース、Compilationを
/// 一つのC#専用境界で組み立てる。開いている複数バッファの未保存本文も受け取れる。
/// AppはUIから本文を受け取り、編集結果を適用するだけにする。
/// </summary>
public sealed record CSharpWorkspaceOperationContext(
    CSharpWorkspaceSourceSnapshot Snapshot,
    CSharpCompilation? SemanticCompilation)
{
    /// <summary>上限によるソース欠落を含まないCompilationか。</summary>
    public bool IsSourceSnapshotComplete => Snapshot.IsComplete;

    /// <summary>上限で欠落したソースがある場合に、UIへ返せる説明を返す。</summary>
    public string? SourceSnapshotWarning => Snapshot.IsComplete
        ? null
        : $"C#ソースが上限で切り詰められています（{Snapshot.SkippedFileCount}ファイル）。";

    public static CSharpWorkspaceOperationContext Create(
        SolutionModel? solution,
        string activePath,
        string activeText,
        CSharpWorkspaceSourceScope scope = CSharpWorkspaceSourceScope.ProjectGraph,
        bool includeSemanticCompilation = false,
        CSharpCompilationOptions? compilationOptions = null,
        string? assemblyName = null,
        CSharpEditorConfigService? editorConfigService = null,
        IReadOnlyDictionary<string, string>? openTexts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activePath);
        ArgumentNullException.ThrowIfNull(activeText);

        var snapshot = CSharpWorkspaceSourceLoader.LoadSnapshot(
            solution, activePath, activeText, scope, openTexts);
        if (!includeSemanticCompilation)
            return new(snapshot, null);

        var referencePaths = solution?.ProjectForFile(Path.GetFullPath(activePath))
            ?.SelectedTargetFrameworkModel?.References
            .Select(reference => reference.FullPath)
            .ToArray();
        var analyzerPaths = solution?.ProjectForFile(Path.GetFullPath(activePath))
            ?.SelectedTargetFrameworkModel?.Analyzers
            .Select(analyzer => analyzer.FullPath)
            .ToArray();
        var additionalTexts = solution?.ProjectForFile(Path.GetFullPath(activePath))
            ?.SelectedTargetFrameworkModel?.AdditionalFiles
            .Select(file => file.FullPath)
            .ToArray();
        var compilation = CSharpSemanticCompilation.Create(
            snapshot.Texts, snapshot.ParseOptionsByPath, referencePaths,
            assemblyName: assemblyName,
            compilationOptions: compilationOptions,
            analyzerPaths: analyzerPaths,
            additionalTexts: CSharpSemanticCompilation.CreateAdditionalTexts(additionalTexts),
            analyzerConfigOptionsProvider: new CSharpAnalyzerConfigOptionsProvider(
                editorConfigService ?? new CSharpEditorConfigService(), activePath));
        return new(snapshot, compilation);
    }
}
