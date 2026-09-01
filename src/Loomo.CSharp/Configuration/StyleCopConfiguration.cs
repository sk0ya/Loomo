using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Reflection;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Configuration;

public enum StyleCopConfigurationState
{
    NotInstalled,
    /// <summary>PackageReferenceはあるが、MSBuild評価結果からAnalyzer DLLを解決できない。</summary>
    AnalyzerNotLoaded,
    Installed,
    InvalidConfiguration,
}

public sealed record StyleCopRuleSetting(string RuleId, string Severity, string SourceFile);

/// <summary>StyleCop.Analyzers がプロジェクトへどう接続されているかの設定スナップショット。
/// 実際のAnalyzer実行は <see cref="StyleCopDiagnosticService"/> が担当する。</summary>
public sealed record StyleCopConfiguration(
    StyleCopConfigurationState State,
    IReadOnlyList<string> AnalyzerPaths,
    IReadOnlyList<string> ConfigurationFiles,
    IReadOnlyList<string> RulesetFiles,
    string? Error = null)
{
    public IReadOnlyList<string> EditorConfigFiles { get; init; } = [];
    public IReadOnlyList<StyleCopRuleSetting> RuleSettings { get; init; } = [];
    public bool IsInstalled => State is StyleCopConfigurationState.AnalyzerNotLoaded or
        StyleCopConfigurationState.Installed or StyleCopConfigurationState.InvalidConfiguration;
    /// <summary>評価済みAnalyzerパスのうち、実在する.NETアセンブリとして解決できるものがあるか。</summary>
    public bool HasResolvedAnalyzer => AnalyzerPaths.Any(
        StyleCopConfigurationService.IsLoadableAssemblyPath);
    public string StatusText => State switch
    {
        StyleCopConfigurationState.AnalyzerNotLoaded => RuleSettings.Count == 0
            ? "StyleCop ⚠ Analyzer未読込"
            : $"StyleCop ⚠ Analyzer未読込 · ルール {RuleSettings.Count}",
        StyleCopConfigurationState.Installed => RuleSettings.Count == 0
            ? $"StyleCop ✓ ({AnalyzerPaths.Count})"
            : $"StyleCop ✓ ({AnalyzerPaths.Count}) · ルール {RuleSettings.Count}",
        StyleCopConfigurationState.InvalidConfiguration => "StyleCop ⚠ 設定不正",
        _ => "StyleCop 未導入",
    };
}

/// <summary>MSBuild評価済みProjectModelからStyleCopの導入・設定状態を読む。
/// プロジェクトファイルを書き換えず、設定ファイルも生成しない。</summary>
public sealed class StyleCopConfigurationService
{
    private static readonly Regex EditorConfigSeverity = new(
        @"^dotnet_diagnostic\.(?<id>SA\d+)\.severity\s*=\s*(?<severity>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnalyzerSeverity = new(
        @"^dotnet_analyzer_diagnostic(?:\.category-(?<category>[^\s]+))?\.severity\s*=\s*(?<severity>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public StyleCopConfiguration Resolve(ProjectModel? project)
    {
        if (project is null)
            return new(StyleCopConfigurationState.NotInstalled, [], [], []);

        var selected = project.SelectedTargetFrameworkModel;
        var analyzerPaths = selected?.Analyzers
            .Where(item => IsStyleCop(item.Include) || IsStyleCop(item.FullPath))
            .Select(item => Path.GetFullPath(item.FullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var centralPackageFiles = FindFiles(project.Directory, "Directory.Packages.props");
        var centralPackageInstalled = false;
        foreach (var file in centralPackageFiles)
        {
            try
            {
                centralPackageInstalled |= Regex.IsMatch(
                    File.ReadAllText(file),
                    "<(?:PackageReference|PackageVersion)\\b[^>]*(?:Include|Update)\\s*=\\s*['\\\"]StyleCop\\.Analyzers['\\\"]",
                    RegexOptions.IgnoreCase);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        bool packageInstalled = project.PackageReferences.Any(IsStyleCop) || centralPackageInstalled;
        bool installed = packageInstalled || analyzerPaths.Length > 0;

        var configFiles = FindFiles(project.Directory, "stylecop.json", ".stylecop.json");
        var rulesetFiles = FindFiles(project.Directory, "*.ruleset");
        var editorConfigFiles = FindFiles(project.Directory, ".editorconfig");
        var ruleSettings = new List<StyleCopRuleSetting>();
        var errors = new List<string>();
        foreach (var file in configFiles)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    errors.Add($"{Path.GetFileName(file)} はJSONオブジェクトではありません。");
            }
            catch (JsonException ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
            catch (IOException ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        foreach (var file in editorConfigFiles)
            ReadEditorConfig(file, ruleSettings, errors);
        foreach (var file in rulesetFiles)
            ReadRuleset(file, ruleSettings, errors);

        if (!installed && errors.Count == 0)
            return WithDetails(new(StyleCopConfigurationState.NotInstalled, analyzerPaths, configFiles, rulesetFiles),
                editorConfigFiles, ruleSettings);
        var state = errors.Count > 0
            ? StyleCopConfigurationState.InvalidConfiguration
            : installed && !analyzerPaths.Any(IsLoadableAssemblyPath)
                ? StyleCopConfigurationState.AnalyzerNotLoaded
                : StyleCopConfigurationState.Installed;
        return WithDetails(new(
            state,
            analyzerPaths, configFiles, rulesetFiles,
            errors.Count == 0 ? null : string.Join(Environment.NewLine, errors)),
            editorConfigFiles, ruleSettings);
    }

    private static StyleCopConfiguration WithDetails(
        StyleCopConfiguration result,
        IReadOnlyList<string> editorConfigFiles,
        IReadOnlyList<StyleCopRuleSetting> ruleSettings)
        => result with { EditorConfigFiles = editorConfigFiles, RuleSettings = ruleSettings };

    private static void ReadEditorConfig(
        string file,
        List<StyleCopRuleSetting> result,
        List<string> errors)
    {
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
                var match = EditorConfigSeverity.Match(line);
                if (match.Success)
                {
                    result.Add(new(match.Groups["id"].Value.ToUpperInvariant(),
                        match.Groups["severity"].Value, file));
                    continue;
                }
                match = AnalyzerSeverity.Match(line);
                if (match.Success)
                {
                    var category = match.Groups["category"].Success
                        ? $"category:{match.Groups["category"].Value}"
                        : "all-analyzers";
                    result.Add(new(category, match.Groups["severity"].Value, file));
                }
            }
        }
        catch (IOException ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
    }

    private static void ReadRuleset(
        string file,
        List<StyleCopRuleSetting> result,
        List<string> errors)
    {
        try
        {
            var root = XDocument.Load(file).Root;
            if (root is null) return;
            foreach (var rule in root.Descendants().Where(e => e.Name.LocalName == "Rule"))
            {
                var id = rule.Attribute("Id")?.Value;
                var action = rule.Attribute("Action")?.Value;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(action) &&
                    id.StartsWith("SA", StringComparison.OrdinalIgnoreCase))
                    result.Add(new(id.ToUpperInvariant(), action, file));
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
        }
    }

    private static bool IsStyleCop(string? value)
        => value?.Contains("StyleCop.Analyzers", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsLoadableAssemblyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            _ = AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException) { return false; }
        catch (FileLoadException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IReadOnlyList<string> FindFiles(string directory, params string[] patterns)
    {
        var result = new List<string>();
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (current is not null)
        {
            foreach (var pattern in patterns)
            {
                try
                {
                    result.AddRange(current.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
                        .Select(file => file.FullName));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            current = current.Parent;
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
