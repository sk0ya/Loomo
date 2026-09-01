using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>シグネチャ上のパラメーター1件。<paramref name="Modifiers"/> は
/// <c>ref</c>/<c>out</c>/<c>in</c>/<c>this</c>/<c>params</c> をそのまま並べた文字列（無ければ空）。</summary>
public sealed record SignatureParameter(
    string Name,
    string Type,
    string Modifiers = "",
    string? DefaultValue = null)
{
    /// <summary>宣言に書き戻すときの1件ぶんのテキスト。</summary>
    public string ToDeclarationText()
    {
        var head = Modifiers.Length > 0 ? $"{Modifiers} {Type} {Name}" : $"{Type} {Name}";
        return DefaultValue is { Length: > 0 } value ? $"{head} = {value}" : head;
    }
}

/// <summary>変更後のパラメーター1件。<paramref name="OriginalIndex"/> が
/// <see cref="Added"/>（-1）なら新規追加で、そのとき呼び出し側に何を書くかが
/// <paramref name="CallSiteArgument"/>。既存パラメーターなら元の実引数をそのまま運ぶ。</summary>
public sealed record SignatureParameterChange(
    int OriginalIndex,
    SignatureParameter Parameter,
    string? CallSiteArgument = null)
{
    public const int Added = -1;
    public bool IsNew => OriginalIndex == Added;
}

/// <summary>ダイアログが返す「変更後のシグネチャ」。</summary>
public sealed record SignatureChange(
    string ReturnType,
    IReadOnlyList<SignatureParameterChange> Parameters);

/// <summary>書き換え対象として読み取ったメソッド／コンストラクター宣言。
/// 範囲は LSP 座標（0始まりの行・UTF-16 単位の桁）で持つ。</summary>
public sealed record MethodSignature(
    string FilePath,
    string Uri,
    string Name,
    string ReturnType,
    bool IsConstructor,
    IReadOnlyList<SignatureParameter> Parameters,
    LspRange ParameterListRange,
    LspRange? ReturnTypeRange,
    LspPosition NamePosition)
{
    /// <summary>UI に出す「今のシグネチャ」1行。</summary>
    public string Display
    {
        get
        {
            var parameters = string.Join(", ", Parameters.Select(p => p.ToDeclarationText()));
            return IsConstructor
                ? $"{Name}({parameters})"
                : $"{ReturnType} {Name}({parameters})";
        }
    }
}

/// <summary>宣言の読み取り結果。<paramref name="Error"/> が非 null ならそのまま表示して終わる。</summary>
public sealed record SignatureTarget(MethodSignature? Signature, string? Error);

/// <summary>書き換え計画。<paramref name="Changes"/> は URI→編集列で、
/// そのままワークスペースへ適用できる形。</summary>
public sealed record SignaturePlan(
    IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> Changes,
    int SiteCount,
    string? Error,
    int SkippedOutsideWorkspace = 0,
    IReadOnlyDictionary<string, string>? ExpectedTexts = null)
{
    public static SignaturePlan Failed(string error) =>
        new(new Dictionary<string, IReadOnlyList<LspTextEdit>>(), 0, error);
}
