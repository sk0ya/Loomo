using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>起動プロジェクト選択用のUIアダプター。C#プロジェクトの検出・判定本体は
/// <see cref="CSharpProjectDiscovery"/>にあり、ここはTypeScript側とも共有する表示型へ写像する。</summary>
public static class DebugProjectDiscovery
{
    public sealed record ProjectEntry(string Name, string FullPath, string RelativePath, bool IsTest)
    {
        /// <summary>モノレポで同名プロジェクトが並んでも場所を見分けられる表示名。</summary>
        public string DisplayName => string.IsNullOrEmpty(RelativePath) || RelativePath == Name
            ? Name : $"{Name}  —  {RelativePath.Replace('\\', '/')}";
    }

    /// <summary>ComboBoxで「自動検出」を確実に選択できる実体項目。</summary>
    public static readonly ProjectEntry AutoDetect = new("(自動検出)", "", "", false);

    public static IReadOnlyList<ProjectEntry> Discover(string root)
        => CSharpProjectDiscovery.Discover(root)
            .Select(candidate => new ProjectEntry(candidate.Name, candidate.FullPath,
                candidate.RelativePath, candidate.IsTest))
            .ToList();
}
