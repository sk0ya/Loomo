using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>.editorconfig の値を、対象ファイルへ適用した後の C# 向けスナップショット。</summary>
public sealed class CSharpEditorConfig
{
    private readonly IReadOnlyDictionary<string, string> _properties;

    internal CSharpEditorConfig(
        string filePath,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyDictionary<string, string> properties)
    {
        FilePath = Path.GetFullPath(filePath);
        SourceFiles = sourceFiles;
        _properties = properties;
    }

    /// <summary>解決対象のソースファイル。</summary>
    public string FilePath { get; }

    /// <summary>値を提供した .editorconfig（祖先→子孫の順）。</summary>
    public IReadOnlyList<string> SourceFiles { get; }

    /// <summary>大文字小文字を無視する正規化済みプロパティ。</summary>
    public IReadOnlyDictionary<string, string> Properties => _properties;

    public string? Get(string key)
        => _properties.TryGetValue(key.Trim().ToLowerInvariant(), out var value) ? value : null;

    public string IndentStyle => Get("indent_style") ?? "space";

    public int? IndentSize => ParsePositiveInt(Get("indent_size"));

    public int? TabWidth => ParsePositiveInt(Get("tab_width"));

    public bool? InsertFinalNewline => ParseBool(Get("insert_final_newline"));

    public string? EndOfLine => Get("end_of_line");

    /// <summary>指定したsymbol kind／accessibilityに一致する.NET naming ruleを返す。
    /// ルールが無い場合はnullとし、生成器側のC#標準名へフォールバックさせる。</summary>
    public CSharpNamingStyle? ResolveNamingStyle(string symbolKind, string accessibility)
    {
        foreach (var rule in _properties.Keys
                     .Where(key => key.StartsWith("dotnet_naming_rule.", StringComparison.OrdinalIgnoreCase) &&
                                   key.EndsWith(".symbols", StringComparison.OrdinalIgnoreCase))
                     .Select(key => key["dotnet_naming_rule.".Length..^".symbols".Length]))
        {
            var symbols = Get($"dotnet_naming_rule.{rule}.symbols");
            var style = Get($"dotnet_naming_rule.{rule}.style");
            if (string.IsNullOrWhiteSpace(symbols) || string.IsNullOrWhiteSpace(style)) continue;
            foreach (var symbol in symbols.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var kinds = Get($"dotnet_naming_symbols.{symbol}.applicable_kinds");
                var accessibilities = Get($"dotnet_naming_symbols.{symbol}.applicable_accessibilities");
                if (kinds is null || accessibilities is null) continue;
                if (!kinds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Any(kind => NamingValueMatch(kind, symbolKind))) continue;
                if (!accessibilities.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Any(value => NamingValueMatch(value, accessibility))) continue;

                return new CSharpNamingStyle(
                    Get($"dotnet_naming_style.{style}.required_prefix") ?? "",
                    Get($"dotnet_naming_style.{style}.capitalization") ?? "pascal_case");
            }
        }
        return null;

        static bool NamingValueMatch(string value, string expected)
            => value is "*" or "all" || string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>style option の値と severity を分離する。例えば
    /// <c>csharp_style_var_for_built_in_types = true:suggestion</c>。</summary>
    public (string Value, CSharpDiagnosticSeverity Severity)? GetStyle(string key)
    {
        var raw = Get(key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
        var value = parts[0].Trim();
        if (value.Length == 0) return null;
        var severity = parts.Length == 1
            ? CSharpDiagnosticSeverity.Default
            : CSharpDiagnosticSeverityParser.Parse(parts[1]);
        return (value, severity);
    }

    /// <summary>診断ID、カテゴリ、全Analyzerの順で .editorconfig severity を解決する。</summary>
    public CSharpDiagnosticSeverity GetDiagnosticSeverity(string diagnosticId, string? category = null)
    {
        var direct = Get($"dotnet_diagnostic.{diagnosticId}.severity");
        if (direct is not null) return CSharpDiagnosticSeverityParser.Parse(direct);

        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryValue = Get($"dotnet_analyzer_diagnostic.category-{category}.severity");
            if (categoryValue is not null) return CSharpDiagnosticSeverityParser.Parse(categoryValue);
        }

        var all = Get("dotnet_analyzer_diagnostic.severity");
        return all is null ? CSharpDiagnosticSeverity.Default : CSharpDiagnosticSeverityParser.Parse(all);
    }

    private static int? ParsePositiveInt(string? value)
        => int.TryParse(value, out var result) && result > 0 ? result : null;

    private static bool? ParseBool(string? value)
        => bool.TryParse(value, out var result) ? result : null;
}

public sealed record CSharpNamingStyle(string RequiredPrefix, string Capitalization);

public enum CSharpDiagnosticSeverity
{
    Default,
    None,
    Silent,
    Suggestion,
    Warning,
    Error,
}

internal static class CSharpDiagnosticSeverityParser
{
    public static CSharpDiagnosticSeverity Parse(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "none" => CSharpDiagnosticSeverity.None,
            "silent" => CSharpDiagnosticSeverity.Silent,
            "suggestion" => CSharpDiagnosticSeverity.Suggestion,
            "warning" => CSharpDiagnosticSeverity.Warning,
            "error" => CSharpDiagnosticSeverity.Error,
            _ => CSharpDiagnosticSeverity.Default,
        };
}

/// <summary>対象ファイルへ適用する .editorconfig を祖先から順に読み込むサービス。
/// Roslyn／LSPの設定を置き換えるものではなく、Loomoがプロジェクト状態とUIへ表示するための
/// 同じ設定スナップショットを提供する。</summary>
public sealed class CSharpEditorConfigService
{
    public CSharpEditorConfig Resolve(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var configs = FindConfigFiles(Path.GetDirectoryName(fullPath)!);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var applied = new List<string>();

        foreach (var config in configs)
        {
            try
            {
                ApplyConfig(config, fullPath, properties);
                applied.Add(config);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new CSharpEditorConfig(fullPath,
            new ReadOnlyCollection<string>(applied),
            new ReadOnlyDictionary<string, string>(properties));
    }

    internal static IReadOnlyList<string> FindConfigFiles(string directory)
    {
        var result = new List<string>();
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, ".editorconfig");
            if (File.Exists(path))
            {
                result.Add(path);
                // root=true stops the search toward ancestors, not the processing of
                // nearer configurations. Reverse below so values still apply from
                // the outermost file to the innermost file.
                if (HasRootMarker(path)) break;
            }
            current = current.Parent;
        }
        result.Reverse();
        return result;
    }

    private static bool HasRootMarker(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parsed = ParseProperty(line);
                if (parsed is not { } pair || !pair.Key.Equals("root", StringComparison.OrdinalIgnoreCase)) continue;
                return pair.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static void ApplyConfig(string path, string filePath, Dictionary<string, string> properties)
    {
        var configDirectory = Path.GetDirectoryName(path)!;
        string? section = null;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            var pair = ParseProperty(line);
            if (pair is null || pair.Value.Key.Equals("root", StringComparison.OrdinalIgnoreCase)) continue;
            if (section is null || Matches(section, configDirectory, filePath))
                properties[pair.Value.Key.ToLowerInvariant()] = pair.Value.Value;
        }
    }

    private static (string Key, string Value)? ParseProperty(string line)
    {
        var separator = line.IndexOfAny(['=', ':']);
        if (separator <= 0) return null;
        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        return key.Length == 0 ? null : (key, value);
    }

    private static bool Matches(string pattern, string configDirectory, string filePath)
    {
        var relative = Path.GetRelativePath(configDirectory, filePath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal)) return false;
        pattern = pattern.Trim().Replace('\\', '/');
        if (pattern.StartsWith('/')) pattern = pattern[1..];
        // EditorConfigのスラッシュを含まないパターン（代表例: [*.cs]）は、
        // config直下だけでなく配下の全階層のファイル名へ適用する。
        var candidate = pattern.Contains('/') ? relative : Path.GetFileName(relative);
        return GlobRegex(pattern).IsMatch(candidate);
    }

    private static System.Text.RegularExpressions.Regex GlobRegex(string pattern)
    {
        var builder = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                {
                    i++;
                    builder.Append("(?:.*/)?");
                }
                else builder.Append(".*");
            }
            else if (c == '*') builder.Append("[^/]*");
            else if (c == '?') builder.Append("[^/]");
            else if (c == '{')
            {
                var end = pattern.IndexOf('}', i + 1);
                if (end > i)
                {
                    var alternatives = pattern[(i + 1)..end].Split(',')
                        .Select(System.Text.RegularExpressions.Regex.Escape);
                    builder.Append("(?:").Append(string.Join('|', alternatives)).Append(")");
                    i = end;
                }
                else builder.Append("\\{");
            }
            else builder.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
        }
        builder.Append('$');
        return new System.Text.RegularExpressions.Regex(builder.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}

/// <summary>Roslyn Source GeneratorへLoomoが解決した.editorconfigを渡すAdapter。</summary>
public sealed class CSharpAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
{
    private readonly CSharpEditorConfigService _service;
    private readonly string _globalPath;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AnalyzerConfigOptions> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public CSharpAnalyzerConfigOptionsProvider(
        CSharpEditorConfigService service, string globalPath)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _globalPath = Path.GetFullPath(globalPath);
    }

    public override AnalyzerConfigOptions GlobalOptions => Get(_globalPath);

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
        => Get(tree.FilePath);

    public override AnalyzerConfigOptions GetOptions(AdditionalText text)
        => Get(text.Path);

    private AnalyzerConfigOptions Get(string? path)
    {
        var fullPath = string.IsNullOrWhiteSpace(path) ? _globalPath : Path.GetFullPath(path);
        return _cache.GetOrAdd(fullPath,
            value => new CSharpAnalyzerConfigOptions(_service.Resolve(value).Properties));
    }

    private sealed class CSharpAnalyzerConfigOptions(
        IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
            => values.TryGetValue(key, out value!);
    }
}
