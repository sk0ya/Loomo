using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>MSBuild評価済みのC#コンパイル条件をRoslynの構文／コンパイル設定へ変換する。
/// IDE fallbackとCodeFixが同じTFM・DefineConstants・LangVersion・Nullableを使うための共通部品。</summary>
internal static class CSharpProjectCompilationOptions
{
    public static CSharpParseOptions Parse(TargetFrameworkModel? target)
    {
        var options = CSharpParseOptions.Default
            .WithPreprocessorSymbols(target?.DefineConstants ?? []);
        var languageVersion = target?.LangVersion?.Trim();
        if (string.IsNullOrWhiteSpace(languageVersion) ||
            languageVersion.Equals("default", StringComparison.OrdinalIgnoreCase))
            return options;
        if (languageVersion.Equals("preview", StringComparison.OrdinalIgnoreCase))
            return options.WithLanguageVersion(LanguageVersion.Preview);
        if (languageVersion.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return options.WithLanguageVersion(LanguageVersion.Latest);
        if (languageVersion.Equals("latestmajor", StringComparison.OrdinalIgnoreCase))
            return options.WithLanguageVersion(LanguageVersion.LatestMajor);

        var normalized = languageVersion.EndsWith(".0", StringComparison.Ordinal)
            ? languageVersion[..^2] : languageVersion.Replace(".", "", StringComparison.Ordinal);
        return Enum.TryParse<LanguageVersion>("CSharp" + normalized, ignoreCase: true, out var parsed)
            ? options.WithLanguageVersion(parsed) : options;
    }

    public static CSharpCompilationOptions Compilation(
        TargetFrameworkModel? target, CSharpEditorConfig? editorConfig = null)
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: target?.Nullable?.Trim().ToLowerInvariant() switch
            {
                "enable" or "warnings" or "annotations" => NullableContextOptions.Enable,
                "disable" => NullableContextOptions.Disable,
                _ => NullableContextOptions.Enable,
            },
            warningLevel: 4);

        if (editorConfig is null) return options;

        var diagnosticOptions = editorConfig.Properties
            .Keys
            .Where(key => key.StartsWith("dotnet_diagnostic.", StringComparison.OrdinalIgnoreCase)
                && key.EndsWith(".severity", StringComparison.OrdinalIgnoreCase))
            .Select(key => key["dotnet_diagnostic.".Length..^".severity".Length])
            .Where(id => id.Length > 0)
            .Select(id => (Id: id, Severity: editorConfig.GetDiagnosticSeverity(id)))
            .Where(item => item.Severity != CSharpDiagnosticSeverity.Default)
            .ToImmutableDictionary(item => item.Id, item => ToReportDiagnostic(item.Severity),
                StringComparer.OrdinalIgnoreCase);
        return diagnosticOptions.Count == 0
            ? options
            : options.WithSpecificDiagnosticOptions(diagnosticOptions);
    }

    private static ReportDiagnostic ToReportDiagnostic(CSharpDiagnosticSeverity severity)
        => severity switch
        {
            CSharpDiagnosticSeverity.None => ReportDiagnostic.Suppress,
            CSharpDiagnosticSeverity.Silent => ReportDiagnostic.Hidden,
            CSharpDiagnosticSeverity.Suggestion => ReportDiagnostic.Info,
            CSharpDiagnosticSeverity.Warning => ReportDiagnostic.Warn,
            CSharpDiagnosticSeverity.Error => ReportDiagnostic.Error,
            _ => ReportDiagnostic.Default,
        };
}
