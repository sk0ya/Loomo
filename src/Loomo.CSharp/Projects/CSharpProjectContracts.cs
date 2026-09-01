namespace sk0ya.Loomo.CSharp.Projects;

/// <summary>ワークスペースとMSBuild評価済みC#意味モデルの共有入口。</summary>
public interface ISolutionModelService
{
    SolutionModel Current { get; }
    event EventHandler<SolutionModel>? Changed;
    Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken = default);
    /// <summary>評価済みmulti-targetingプロジェクトの現在のTFMを切り替える。</summary>
    Task<bool> SelectTargetFrameworkAsync(string projectPath, string targetFramework,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
    /// <summary>solution／MSBuildの現在のBuild構成を切り替える。</summary>
    Task<bool> SelectConfigurationAsync(string configuration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
    ProjectModel? ProjectForFile(string filePath);
    /// <summary>csproj／slnなど実行対象のパスから担当プロジェクトを引く。</summary>
    ProjectModel? ProjectForTarget(string targetPath)
        => Current.ProjectForTarget(targetPath);
    ProjectLoadState FileState(string filePath);
}

/// <summary>プロジェクト評価を差し替え可能にする境界。実装はdotnet msbuild、テストは決定的なfakeを使う。</summary>
public interface IProjectEvaluator
{
    Task<ProjectEvaluation> EvaluateAsync(string projectPath, string? targetFramework,
        CancellationToken cancellationToken = default);

    /// <summary>TargetFrameworkとBuild構成を指定した評価。旧評価器は構成を無視して互換動作する。</summary>
    Task<ProjectEvaluation> EvaluateAsync(string projectPath, string? targetFramework,
        string? configuration, CancellationToken cancellationToken = default)
        => EvaluateAsync(projectPath, targetFramework, cancellationToken);
}

public sealed record ProjectEvaluation(
    string? TargetFramework,
    string? TargetFrameworks,
    string? DefineConstants,
    string? LangVersion,
    IReadOnlyList<ProjectItemEvaluation> Compile,
    IReadOnlyList<ProjectItemEvaluation> ProjectReferences,
    IReadOnlyList<ProjectItemEvaluation> Analyzers,
    IReadOnlyList<ProjectItemEvaluation> AdditionalFiles,
    IReadOnlyList<ProjectItemEvaluation> None,
    bool IsTestProject,
    IReadOnlyList<ProjectItemEvaluation>? PackageReferences = null,
    IReadOnlyList<ProjectItemEvaluation>? References = null,
    string? ProjectAssetsFile = null,
    string? Nullable = null);

public sealed record ProjectItemEvaluation(
    string Include,
    string? FullPath = null,
    string? Link = null,
    string? OutputItemType = null,
    bool? ReferenceOutputAssembly = null);
