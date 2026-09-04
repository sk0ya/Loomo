using Editor.Core.Lsp;
using Microsoft.CodeAnalysis.CSharp;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>C# 固有のコード生成の種類。実際の生成は種類ごとの生成器（CSharp*Generator）が持ち、
/// 入口の振り分けは <see cref="CSharpCodeGenerationService"/> が行う。</summary>
public enum CSharpCodeGenerationKind
{
    Constructor,
    PropertiesFromFields,
    EqualsAndGetHashCode,
    ToString,
    Deconstruct,
    NullGuards,
    MethodFromUsage,
    ImplementInterface,
    OverrideMembers,
    DelegatingMembers,
    DisposePattern,
    AsyncDisposePattern,
    FieldFromConstructorParameter,
}

/// <summary>生成結果。LSP の <see cref="LspWorkspaceEdit"/> に載せて、UI 側の preview ／ rollback
/// 経路へ渡す。</summary>
public sealed record CSharpCodeGenerationResult(
    LspWorkspaceEdit? Edit,
    string Summary,
    string? Error = null,
    IReadOnlyDictionary<string, string>? ExpectedTexts = null)
{
    /// <summary>生成できなかった理由を返す。編集は伴わない。</summary>
    public static CSharpCodeGenerationResult Failed(string error) => new(null, "", error);
}

/// <summary>C#プロジェクトのnullable／naming設定を生成器へ渡すスナップショット。</summary>
public sealed record CSharpGenerationOptions(
    bool NullableEnabled = true,
    CSharpNamingStyle? FieldNaming = null,
    CSharpNamingStyle? PropertyNaming = null,
    CSharpNamingStyle? ParameterNaming = null,
    CSharpParseOptions? ParseOptions = null,
    IReadOnlyDictionary<string, CSharpParseOptions>? WorkspaceParseOptions = null,
    CSharpCompilation? SemanticCompilation = null);
