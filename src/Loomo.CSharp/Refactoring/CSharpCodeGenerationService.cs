using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>C# 固有のコード生成の入口。LSP の code action に依存せず、Roslyn の構文と（あれば）
/// 意味モデルだけで安全に作れる操作を扱う。
/// <para>ここが持つのは<b>振り分けだけ</b>——キャレット位置から対象の型を決め、
/// <see cref="CSharpCodeGenerationKind"/> ごとの生成器へ渡し、返ってきたメンバー本文を型の末尾へ
/// 挿入する。生成そのものは種類ごとの CSharp*Generator にあり、共有部品は
/// <see cref="GenerationSyntax"/>（探索）／<see cref="GenerationNames"/>（命名）／
/// <see cref="MemberFormat"/>（書式）に分かれている。新しい生成を足すときは、この列挙子と
/// 生成器を 1 つずつ増やす——このクラスへ本体を書き足さない。</para></summary>
public static class CSharpCodeGenerationService
{
    public static CSharpCodeGenerationResult Generate(
        string filePath,
        string text,
        int line,
        int character,
        CSharpCodeGenerationKind kind)
        => Generate(filePath, text, line, character, kind, null);

    /// <summary>選択中プロジェクトのソース断片を読み取り、同一ファイルに無いinterface／基底クラスも
    /// 構文だけで解決する。編集対象は常に<paramref name="filePath"/>だけで、他ファイルは変更しない。</summary>
    public static CSharpCodeGenerationResult Generate(
        string filePath,
        string text,
        int line,
        int character,
        CSharpCodeGenerationKind kind,
        IReadOnlyDictionary<string, string>? workspaceTexts)
        => Generate(filePath, text, line, character, kind, workspaceTexts, null);

    public static CSharpCodeGenerationResult Generate(
        string filePath,
        string text,
        int line,
        int character,
        CSharpCodeGenerationKind kind,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        CSharpGenerationOptions? generationOptions)
    {
        generationOptions ??= new CSharpGenerationOptions();
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return CSharpCodeGenerationResult.Failed("C# ファイルでのみコード生成を実行できます。");

        var source = SourceText.From(text);
        if (line < 0 || line >= source.Lines.Count)
            return CSharpCodeGenerationResult.Failed("キャレット位置が文書の範囲外です。");

        var position = GenerationSyntax.ClampToLine(source, line, character);
        var parseOptions = generationOptions.ParseOptions ?? CSharpParseOptions.Default;
        var root = CSharpSyntaxTree.ParseText(source, parseOptions).GetRoot();
        var roots = GenerationSyntax.ParseWorkspaceRoots(filePath, root, workspaceTexts, parseOptions,
            generationOptions.WorkspaceParseOptions);
        var semanticModel = generationOptions.SemanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        var type = root.FindToken(position).Parent?
            .AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(GenerationSyntax.IsClassOrStruct);
        if (type is null)
            return CSharpCodeGenerationResult.Failed("クラスまたは構造体の中にキャレットを置いてください。");

        if (kind == CSharpCodeGenerationKind.DisposePattern)
            return CSharpDisposeGenerator.GenerateDisposePattern(filePath, source, type, semanticModel);
        if (kind == CSharpCodeGenerationKind.AsyncDisposePattern)
            return CSharpDisposeGenerator.GenerateAsyncDisposePattern(filePath, source, type, semanticModel);
        if (kind == CSharpCodeGenerationKind.FieldFromConstructorParameter)
            return CSharpFieldFromParameterGenerator.Generate(
                filePath, source, type, position, generationOptions);

        var generated = kind switch
        {
            CSharpCodeGenerationKind.Constructor => CSharpConstructorGenerator.Generate(
                type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.PropertiesFromFields => CSharpPropertyGenerator.Generate(
                type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.EqualsAndGetHashCode => CSharpValueSemanticsGenerator.GenerateEquality(
                type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.ToString => CSharpValueSemanticsGenerator.GenerateToString(
                type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.Deconstruct => CSharpValueSemanticsGenerator.GenerateDeconstruct(
                type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.MethodFromUsage => CSharpMethodFromUsageGenerator.Generate(
                type, root, position, generationOptions, semanticModel),
            CSharpCodeGenerationKind.ImplementInterface => CSharpInterfaceImplementationGenerator.Generate(
                type, roots, semanticModel),
            CSharpCodeGenerationKind.OverrideMembers => CSharpOverrideMemberGenerator.Generate(
                type, roots, semanticModel),
            CSharpCodeGenerationKind.DelegatingMembers => CSharpDelegatingMemberGenerator.Generate(
                type, roots, position, semanticModel),
            CSharpCodeGenerationKind.NullGuards => (null, null, "Null guard はメソッド位置で実行してください。"),
            _ => (Text: (string?)null, Summary: (string?)null, Error: "未対応のコード生成です。"),
        };
        if (generated.Error is not null) return CSharpCodeGenerationResult.Failed(generated.Error);

        var edit = GenerationSyntax.InsertBeforeCloseBrace(filePath, source, type, generated.Text!);
        return edit is null
            ? CSharpCodeGenerationResult.Failed("型の末尾へ生成コードを挿入できませんでした。")
            : new CSharpCodeGenerationResult(edit, generated.Summary!);
    }

    /// <summary>選択したJSONをC#型へ変換し、指定位置へ挿入するWorkspaceEditを作る。
    /// 入力の選択範囲自体は置換せず、生成コードを追加するため既存ソースを壊さない。</summary>
    public static CSharpCodeGenerationResult GenerateJsonTypes(
        string filePath, string text, int line, int character, string json,
        string rootTypeName = "Root", CSharpGenerationOptions? generationOptions = null)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return CSharpCodeGenerationResult.Failed("C# ファイルでのみコード生成を実行できます。");

        var generated = JsonToCSharpGenerator.Generate(
            json, rootTypeName, generationOptions?.NullableEnabled ?? true);
        if (generated.Error is { Length: > 0 }) return CSharpCodeGenerationResult.Failed(generated.Error);
        if (generated.Text is not { Length: > 0 }) return CSharpCodeGenerationResult.Failed("生成できる型がありません。");

        var source = SourceText.From(text);
        if (line < 0 || line >= source.Lines.Count)
            return CSharpCodeGenerationResult.Failed("キャレット位置が文書の範囲外です。");
        var position = GenerationSyntax.ClampToLine(source, line, character);
        var textLine = source.Lines[line];
        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = position == textLine.Start ? "" : newline;
        var insertion = prefix + generated.Text + newline;
        var lspPosition = new LspPosition(line, position - textLine.Start);
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [LspUri.FromPath(Path.GetFullPath(filePath))] =
                    [new LspTextEdit(new LspRange(lspPosition, lspPosition), insertion)],
            });
        return new CSharpCodeGenerationResult(edit, generated.Summary);
    }

    /// <summary>メソッド／コンストラクターの参照型引数を検査するコード生成。
    /// キャレットはメンバー本文に置く（型の末尾へは挿入しない）ため、振り分けを通さない。</summary>
    public static CSharpCodeGenerationResult GenerateNullGuards(
        string filePath, string text, int line, int character,
        CSharpCompilation? semanticCompilation = null)
        => CSharpNullGuardGenerator.Generate(filePath, text, line, character, semanticCompilation);
}
