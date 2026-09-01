using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>プロジェクト文脈からC#コード生成オプションを組み立てる。
/// UI層がnullable／命名規則／ParseOptionsの判定を重複して持たないための境界。</summary>
public static class CSharpGenerationOptionsFactory
{
    public static CSharpGenerationOptions Create(
        SolutionModel? solution, string filePath,
        CSharpEditorConfigService? editorConfig = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var config = (editorConfig ?? new CSharpEditorConfigService()).Resolve(filePath);
        var target = solution?.ProjectForFile(Path.GetFullPath(filePath))?.SelectedTargetFrameworkModel;
        return new CSharpGenerationOptions(
            NullableEnabled: target?.Nullable is null || target.NullableEnabled,
            FieldNaming: config.ResolveNamingStyle("field", "private"),
            PropertyNaming: config.ResolveNamingStyle("property", "public"),
            ParameterNaming: config.ResolveNamingStyle("parameter", "*"),
            ParseOptions: CSharpWorkspaceSourceLoader.ParseOptionsForFile(solution, filePath));
    }
}
