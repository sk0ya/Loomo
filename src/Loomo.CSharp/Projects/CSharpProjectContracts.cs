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
    string? Nullable = null,
    // MSBuildが評価した実アセンブリ名。プロジェクトファイル名とは一致しないことがある。
    string? AssemblyName = null)
{
    /// <summary>
    /// design-time build（<c>/t:Compile</c>）まで走り切った評価か。<b>走れなかったときの
    /// <c>@(Compile)</c> には生成ソースが1つも入らない</b>——<c>*.g.cs</c>・<c>AssemblyInfo.cs</c>・
    /// global usings はターゲットが足すものなので、評価だけでは「元から無い」のと区別が付かない。
    ///
    /// <para>区別を評価結果そのものへ持たせるのは、欠落を<b>ファイルの読み取り失敗として数えられない</b>
    /// から。一覧に載らなかったファイルは読みに行かれず、
    /// <see cref="CSharpWorkspaceSourceSnapshot.MissingFileCount"/> は 0 のまま＝
    /// 「全部読めた」に見えてしまい、意味解析の結果を信用してよいかの判定
    /// （<see cref="CSharpWorkspaceOperationContext.CanTrustSemanticResults"/>）が
    /// <b>いちばん当てにならない経路でだけ</b>素通りする。</para>
    /// </summary>
    public bool IsDesignTimeBuildComplete { get; init; } = true;
}

public sealed record ProjectItemEvaluation(
    string Include,
    string? FullPath = null,
    string? Link = null,
    string? OutputItemType = null,
    bool? ReferenceOutputAssembly = null,
    // design-time buildが中間出力へ生成した項目（XAMLの*.g.cs、AssemblyInfo、global usings）。
    // コンパイラには要るが、ユーザーが開く・編集する対象ではない。
    bool IsGenerated = false);
