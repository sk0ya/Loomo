using System.Collections.Generic;

namespace sk0ya.Loomo.CSharp.Projects;

/// <summary>C# プロジェクトの読み込み状態。候補なしと、まだ解析中／失敗を区別する。</summary>
public enum ProjectLoadState
{
    NotConfigured,
    Loading,
    Ready,
    Failed,
    NotInProject,
    NotInSelectedTargetFramework,
}

/// <summary>MSBuild item の解決済みパスと、プロジェクト内での相対表記。</summary>
public sealed record ProjectItem(string Include, string FullPath, string? Link = null);

/// <summary>1つの TargetFramework に対するMSBuild評価結果。</summary>
public sealed record TargetFrameworkModel(
    string Name,
    IReadOnlyList<string> DefineConstants,
    string LangVersion,
    IReadOnlyList<ProjectItem> CompileFiles,
    IReadOnlyList<ProjectItem> Analyzers,
    IReadOnlyList<ProjectItem> AdditionalFiles,
    IReadOnlyList<ProjectItem> NoneFiles)
{
    /// <summary>このTFMのMSBuild評価で有効になったProjectReference。
    /// nullは旧来の簡易モデルを表し、プロジェクト全体の参照へフォールバックする。</summary>
    public IReadOnlyList<string>? ProjectReferences { get; init; }

    /// <summary>MSBuildが解決したアセンブリ参照。構文中心の機能でも、意味解析を同じ評価結果へ寄せる。</summary>
    public IReadOnlyList<ProjectItem> References { get; init; } = [];

    /// <summary>MSBuildが解決したNullable設定（enable／disable／warnings）。生成器がnullable注釈を
    /// 無条件に出してCS8632を作らないために利用する。</summary>
    public string? Nullable { get; init; }

    public bool NullableEnabled
        => string.Equals(Nullable, "enable", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Nullable, "warnings", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Nullable, "annotations", StringComparison.OrdinalIgnoreCase);
}

/// <summary>MSBuild評価後のプロジェクト意味モデル。プロジェクトファイルのXMLを直接読む層ではない。</summary>
public sealed record ProjectModel(
    string Name,
    string FullPath,
    string Directory,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<TargetFrameworkModel> TargetFrameworks,
    string? SelectedTargetFramework,
    bool IsTestProject,
    ProjectLoadState State,
    string? Error = null)
{
    /// <summary>評価されたPackageReference。中央管理されたAnalyzerの存在確認にも使う。</summary>
    public IReadOnlyList<string> PackageReferences { get; init; } = Array.Empty<string>();

    /// <summary>solution構成からこのプロジェクトへ割り当てられた実構成名。</summary>
    public string Configuration { get; init; } = "Debug";

    public TargetFrameworkModel? SelectedTargetFrameworkModel
        => TargetFrameworks.FirstOrDefault(t => string.Equals(t.Name, SelectedTargetFramework, StringComparison.OrdinalIgnoreCase))
           ?? TargetFrameworks.FirstOrDefault();

    /// <summary>このプロジェクトに属するファイルか。Link item は実体パスも受け付ける。</summary>
    public bool ContainsFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        return SelectedTargetFrameworkModel?.CompileFiles.Any(f =>
                   string.Equals(Path.GetFullPath(f.FullPath), full, StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>選択中TFM以外のCompile項目も含めた所属判定。表示状態の説明にだけ使い、
    /// 診断・補完・編集対象の解決には <see cref="ContainsFile"/> を使う。</summary>
    public bool ContainsFileInAnyTargetFramework(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        return TargetFrameworks.SelectMany(target => target.CompileFiles).Any(file =>
            string.Equals(Path.GetFullPath(file.FullPath), full, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>solution／workspace の現在状態と、解決済みプロジェクト一覧。</summary>
public sealed record SolutionModel(
    string? FullPath,
    string Name,
    string RootDirectory,
    IReadOnlyList<ProjectModel> Projects,
    ProjectLoadState State,
    string? Error = null,
    IReadOnlyList<string>? Configurations = null,
    string? SelectedConfiguration = null)
{
    /// <summary>solution／MSBuildが提供する構成名。構成を持たないcsproj単独ワークスペースにも
    /// dotnet標準のDebug／Releaseを表示し、実行経路間で同じ値を使う。</summary>
    public IReadOnlyList<string> ConfigurationOptions
        => Configurations is { Count: > 0 } ? Configurations : ["Debug", "Release"];

    /// <summary>未選択・不正な保存値を実行前に安全な既定値へ正規化した構成名。</summary>
    public string EffectiveConfiguration
        => ConfigurationOptions.FirstOrDefault(c =>
               string.Equals(c, SelectedConfiguration, StringComparison.OrdinalIgnoreCase))
           ?? ConfigurationOptions.FirstOrDefault(c =>
               string.Equals(c, "Debug", StringComparison.OrdinalIgnoreCase))
           ?? ConfigurationOptions[0];

    public static SolutionModel NotConfigured(string rootDirectory)
        => new(null, Path.GetFileName(Path.TrimEndingDirectorySeparator(rootDirectory)), rootDirectory,
            Array.Empty<ProjectModel>(), ProjectLoadState.NotConfigured);

    public ProjectModel? ProjectForFile(string filePath)
        => Projects.FirstOrDefault(p => p.State == ProjectLoadState.Ready && p.ContainsFile(filePath));

    /// <summary>選択中TFM以外も含めて、表示用の所属プロジェクトを解決する。
    /// 編集・診断の対象解決には <see cref="ProjectForFile"/> を使い、別TFMのファイルを
    /// 現在の意味モデルへ混ぜない。</summary>
    public ProjectModel? ProjectForFileInAnyTargetFramework(string filePath)
        => Projects.FirstOrDefault(p => p.State == ProjectLoadState.Ready &&
            p.ContainsFileInAnyTargetFramework(filePath));

    /// <summary>Build／Testの対象（csproj／sln）から担当プロジェクトを引く。
    /// <see cref="ProjectForFile"/>はCompile項目だけを対象にするため、実行対象のcsproj自体は
    /// こちらで解決する。</summary>
    public ProjectModel? ProjectForTarget(string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        return Projects.FirstOrDefault(project =>
                   string.Equals(Path.GetFullPath(project.FullPath), full,
                       StringComparison.OrdinalIgnoreCase))
               ?? ProjectForFile(full);
    }

    /// <summary>Build／Test／Run／Debugの対象へ渡す実効構成を解決する。
    /// solution自身はsolution構成を使い、csprojまたはそのCompileファイルは
    /// <c>ProjectConfigurationPlatforms</c>で割り当てられたプロジェクト構成を使う。</summary>
    public string ConfigurationForTarget(string? targetPath)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            try
            {
                var project = ProjectForTarget(targetPath);
                if (project is { Configuration.Length: > 0 })
                    return project.Configuration;
            }
            catch (ArgumentException)
            {
                // 呼び出し元がまだ解決していないパスを渡した場合はsolution構成へ戻す。
            }
        }
        return EffectiveConfiguration;
    }

    /// <summary>ファイルが解析対象外か、解析中／失敗かを「候補なし」と分けて返す。</summary>
    public ProjectLoadState ResolveFileState(string filePath)
    {
        if (ProjectForFile(filePath) is not null) return ProjectLoadState.Ready;
        if (State is ProjectLoadState.Loading or ProjectLoadState.Failed) return State;
        if (ProjectForFileInAnyTargetFramework(filePath) is not null)
            return ProjectLoadState.NotInSelectedTargetFramework;
        return ProjectLoadState.NotInProject;
    }
}
