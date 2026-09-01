using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>C# 固有のコード生成。LSP の code action に依存しない、構文だけで安全に作れる操作を置く。
/// 生成結果は LSP の <see cref="LspWorkspaceEdit"/> に変換し、UI 側の preview／rollback 経路へ渡す。</summary>
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

public sealed record CSharpCodeGenerationResult(
    LspWorkspaceEdit? Edit,
    string Summary,
    string? Error = null,
    IReadOnlyDictionary<string, string>? ExpectedTexts = null);

/// <summary>C#プロジェクトのnullable／naming設定を生成器へ渡すスナップショット。</summary>
public sealed record CSharpGenerationOptions(
    bool NullableEnabled = true,
    CSharpNamingStyle? FieldNaming = null,
    CSharpNamingStyle? PropertyNaming = null,
    CSharpNamingStyle? ParameterNaming = null,
    CSharpParseOptions? ParseOptions = null,
    IReadOnlyDictionary<string, CSharpParseOptions>? WorkspaceParseOptions = null,
    CSharpCompilation? SemanticCompilation = null);

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
            return Failed("C# ファイルでのみコード生成を実行できます。");

        var source = SourceText.From(text);
        if (line < 0 || line >= source.Lines.Count)
            return Failed("キャレット位置が文書の範囲外です。");

        var position = ClampToLine(source, line, character);
        var parseOptions = generationOptions.ParseOptions ?? CSharpParseOptions.Default;
        var root = CSharpSyntaxTree.ParseText(source, parseOptions).GetRoot();
        var roots = ParseWorkspaceRoots(filePath, root, workspaceTexts, parseOptions,
            generationOptions.WorkspaceParseOptions);
        var semanticModel = generationOptions.SemanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        var type = root.FindToken(position).Parent?
            .AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(IsClassOrStruct);
        if (type is null)
            return Failed("クラスまたは構造体の中にキャレットを置いてください。");

        if (kind == CSharpCodeGenerationKind.DisposePattern)
            return GenerateDisposePattern(filePath, source, type, semanticModel);
        if (kind == CSharpCodeGenerationKind.AsyncDisposePattern)
            return GenerateAsyncDisposePattern(filePath, source, type, semanticModel);
        if (kind == CSharpCodeGenerationKind.FieldFromConstructorParameter)
            return GenerateFieldFromConstructorParameter(filePath, source, type, position, generationOptions);

        var generated = kind switch
        {
            CSharpCodeGenerationKind.Constructor => GenerateConstructor(type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.PropertiesFromFields => GenerateProperties(
                type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.EqualsAndGetHashCode => GenerateEquality(
                type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.ToString => GenerateToString(
                type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.Deconstruct => GenerateDeconstruct(
                type, generationOptions, semanticModel),
            CSharpCodeGenerationKind.MethodFromUsage => GenerateMethodFromUsage(
                type, root, position, generationOptions, semanticModel),
            CSharpCodeGenerationKind.ImplementInterface => GenerateInterfaceMembers(type, roots, semanticModel),
            CSharpCodeGenerationKind.OverrideMembers => GenerateOverrideMembers(type, roots, semanticModel),
            CSharpCodeGenerationKind.DelegatingMembers => GenerateDelegatingMembers(type, roots, position, semanticModel),
            CSharpCodeGenerationKind.NullGuards => (null, null, "Null guard はメソッド位置で実行してください。"),
            _ => (Text: (string?)null, Summary: (string?)null, Error: "未対応のコード生成です。"),
        };
        if (generated.Error is not null) return Failed(generated.Error);

        var edit = InsertBeforeCloseBrace(filePath, source, type, generated.Text!);
        return edit is null
            ? Failed("型の末尾へ生成コードを挿入できませんでした。")
            : new CSharpCodeGenerationResult(edit, generated.Summary!);
    }

    private static CSharpCodeGenerationResult GenerateDisposePattern(
        string filePath, SourceText source, TypeDeclarationSyntax type,
        SemanticModel? semanticModel)
    {
        if (type is not ClassDeclarationSyntax)
            return Failed("Disposeパターンはクラスでのみ生成できます。");

        var existing = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => string.Equals(m.Identifier.ValueText, "Dispose", StringComparison.Ordinal))
            .ToList();
        if (existing.Any(m => m.ParameterList.Parameters.Count == 0)
            || existing.Any(m => m.ParameterList.Parameters.Count == 1
                && string.Equals(m.ParameterList.Parameters[0].Type?.ToString(), "bool", StringComparison.Ordinal)))
            return Failed("Disposeメソッドが既にあります。");

        var disposableContract = semanticModel?.Compilation.GetTypeByMetadataName("System.IDisposable");
        var allFields = InstanceFields(type).ToList();
        var semanticTypeSymbol = semanticModel is not null &&
            FindEquivalentType(type, semanticModel) is { } semanticType
            ? semanticModel.GetDeclaredSymbol(semanticType) as INamedTypeSymbol
            : null;
        if (semanticTypeSymbol is not null)
        {
            allFields.AddRange(GetSemanticPartialFields(semanticTypeSymbol, semanticModel!));
            if (semanticTypeSymbol.GetMembers("Dispose").OfType<IMethodSymbol>().Any(method =>
                    method.Parameters.Length == 0 ||
                    (method.Parameters.Length == 1 &&
                        method.Parameters[0].Type.SpecialType == SpecialType.System_Boolean)))
                return Failed("Disposeメソッドが既にあります。");
        }
        var disposableFields = allFields
            .Where(field => IsDisposableField(field, semanticModel, disposableContract))
            .ToList();
        var hasDisposableContract = HasDisposableContract(type, semanticModel, disposableContract);
        if (!hasDisposableContract && disposableFields.Count == 0)
            return Failed("IDisposableフィールドまたはIDisposable実装が見つかりません。");

        var inheritedDisposePattern = false;
        if (semanticModel is not null && disposableContract is not null &&
            semanticTypeSymbol is not null &&
            semanticTypeSymbol.BaseType?.AllInterfaces.Any(@interface =>
                SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                    disposableContract)) == true &&
            disposableFields.Count > 0)
        {
            // A derived IDisposable type must extend the base Dispose(bool) contract.
            // Emitting another virtual method would hide the base implementation and
            // leave the derived resource undisposed when callers invoke base.Dispose().
            if (FindOverridableDispose(semanticTypeSymbol, disposableContract) is null)
                return Failed("基底型のIDisposableパターンを安全に拡張できません。");
            inheritedDisposePattern = true;
        }

        var disposeBody = disposableFields.Count == 0
            ? ""
            : string.Join("\n", disposableFields.Select(field =>
                $"        {DisposeField(field, semanticModel, disposableContract)}"));
        var generated = inheritedDisposePattern
            ? "protected override void Dispose(bool disposing)\n{\n"
              + "    if (disposing)\n    {\n"
              + disposeBody
              + (disposeBody.Length > 0 ? "\n" : "")
              + "    }\n"
              + "    base.Dispose(disposing);\n"
              + "}"
            : "public void Dispose()\n{\n"
              + "    Dispose(true);\n"
              + "    global::System.GC.SuppressFinalize(this);\n"
              + "}\n\n"
              + $"protected{(type.Modifiers.Any(m => m.IsKind(SyntaxKind.SealedKeyword)) ? "" : " virtual")} void Dispose(bool disposing)\n{{\n"
              + "    if (disposing)\n    {\n"
              + disposeBody
              + (disposeBody.Length > 0 ? "\n" : "")
              + "    }\n"
              + "}";
        var memberEdit = InsertBeforeCloseBrace(filePath, source, type, generated);
        if (memberEdit is null) return Failed("Disposeパターンを型の末尾へ挿入できませんでした。");

        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edits = memberEdit.Changes.TryGetValue(uri, out var existingEdits)
            ? existingEdits.ToList()
            : new List<LspTextEdit>();
        if (!hasDisposableContract)
        {
            var anchor = type.BaseList is { Types.Count: > 0 }
                ? type.BaseList.Types.Last().Type.Span.End
                : (type.TypeParameterList?.Span.End ?? type.Identifier.Span.End);
            var anchorLine = source.Lines.GetLineFromPosition(anchor);
            var anchorColumn = anchor - anchorLine.Start;
            var text = type.BaseList is { Types.Count: > 0 }
                ? ", global::System.IDisposable"
                : " : global::System.IDisposable";
            edits.Add(new LspTextEdit(
                new LspRange(
                    new LspPosition(anchorLine.LineNumber, anchorColumn),
                    new LspPosition(anchorLine.LineNumber, anchorColumn)), text));
        }

        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(
                new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
                {
                    [uri] = edits,
                }),
            "Disposeパターンを生成");
    }

    private static bool IsDisposableField(
        FieldInfo field,
        SemanticModel? semanticModel,
        INamedTypeSymbol? disposableContract)
    {
        if (field.SemanticSymbol is { } semanticSymbol && disposableContract is not null)
        {
            return SymbolEqualityComparer.Default.Equals(semanticSymbol.Type, disposableContract) ||
                   semanticSymbol.Type.AllInterfaces.Any(@interface =>
                       SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                           disposableContract));
        }
        if (semanticModel is not null && disposableContract is not null &&
            FindEquivalentField(field, semanticModel) is { } semanticField &&
            semanticModel.GetDeclaredSymbol(semanticField) is IFieldSymbol symbol)
        {
            var fieldType = symbol.Type;
            return SymbolEqualityComparer.Default.Equals(fieldType, disposableContract) ||
                   fieldType.AllInterfaces.Any(@interface =>
                       SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition, disposableContract));
        }

        return LooksDisposable(field.Type);
    }

    private static CSharpCodeGenerationResult GenerateAsyncDisposePattern(
        string filePath, SourceText source, TypeDeclarationSyntax type,
        SemanticModel? semanticModel)
    {
        if (type is not ClassDeclarationSyntax)
            return Failed("非同期Disposeパターンはクラスでのみ生成できます。");
        if (semanticModel is null)
            return Failed("非同期Disposeパターンは意味モデルが必要です。");

        var asyncDisposableContract = semanticModel.Compilation
            .GetTypeByMetadataName("System.IAsyncDisposable");
        if (asyncDisposableContract is null)
            return Failed("System.IAsyncDisposableを解決できません。");

        var existing = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => string.Equals(m.Identifier.ValueText, "DisposeAsync", StringComparison.Ordinal))
            .ToList();

        var semanticType = FindEquivalentType(type, semanticModel);
        if (semanticType is null || semanticModel.GetDeclaredSymbol(semanticType) is not INamedTypeSymbol typeSymbol)
            return Failed("対象クラスの意味モデルを解決できません。");
        if (existing.Any(m => m.ParameterList.Parameters.Count == 0) ||
            typeSymbol.GetMembers("DisposeAsync").OfType<IMethodSymbol>()
                .Any(method => method.Parameters.Length == 0))
            return Failed("DisposeAsyncメソッドが既にあります。");

        var inheritedAsyncDisposeCore = typeSymbol.BaseType?.AllInterfaces.Any(@interface =>
                SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                    asyncDisposableContract)) == true
            ? FindOverridableDisposeAsyncCore(typeSymbol,
                semanticModel.Compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask"))
            : null;
        if (typeSymbol.BaseType?.AllInterfaces.Any(@interface =>
                SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                    asyncDisposableContract)) == true && inheritedAsyncDisposeCore is null)
            return Failed("基底型のIAsyncDisposableパターンを安全に拡張できません。");

        var allFields = InstanceFields(type).ToList();
        allFields.AddRange(GetSemanticPartialFields(typeSymbol, semanticModel));
        var asyncDisposableFields = allFields
            .Where(field => IsAsyncDisposableField(field, semanticModel, asyncDisposableContract))
            .ToList();
        var hasAsyncDisposableContract = HasAsyncDisposableContract(typeSymbol, asyncDisposableContract);
        if (!hasAsyncDisposableContract && asyncDisposableFields.Count == 0)
            return Failed("IAsyncDisposableフィールドまたはIAsyncDisposable実装が見つかりません。");
        if (inheritedAsyncDisposeCore is not null && asyncDisposableFields.Count == 0)
            return Failed("追加で解放するIAsyncDisposableフィールドが見つかりません。");

        var disposeBody = string.Join("\n", asyncDisposableFields.Select(field =>
            AsyncDisposeField(field, semanticModel)));
        var generated = inheritedAsyncDisposeCore is not null
            ? "protected override async global::System.Threading.Tasks.ValueTask DisposeAsyncCore()\n{\n"
              + disposeBody
              + (inheritedAsyncDisposeCore.IsAbstract
                  ? "\n"
                  : "\n    await base.DisposeAsyncCore().ConfigureAwait(false);\n")
              + "}"
            : asyncDisposableFields.Count == 0
            ? "public global::System.Threading.Tasks.ValueTask DisposeAsync()\n{\n"
              + "    global::System.GC.SuppressFinalize(this);\n"
              + "    return global::System.Threading.Tasks.ValueTask.CompletedTask;\n"
              + "}"
            : "public async global::System.Threading.Tasks.ValueTask DisposeAsync()\n{\n"
              + disposeBody
              + "\n    global::System.GC.SuppressFinalize(this);\n"
              + "}";
        var memberEdit = InsertBeforeCloseBrace(filePath, source, type, generated);
        if (memberEdit is null) return Failed("非同期Disposeパターンを型の末尾へ挿入できませんでした。");

        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var edits = memberEdit.Changes.TryGetValue(uri, out var existingEdits)
            ? existingEdits.ToList()
            : new List<LspTextEdit>();
        if (!hasAsyncDisposableContract)
        {
            var anchor = type.BaseList is { Types.Count: > 0 }
                ? type.BaseList.Types.Last().Type.Span.End
                : (type.TypeParameterList?.Span.End ?? type.Identifier.Span.End);
            var anchorLine = source.Lines.GetLineFromPosition(anchor);
            var anchorColumn = anchor - anchorLine.Start;
            var text = type.BaseList is { Types.Count: > 0 }
                ? ", global::System.IAsyncDisposable"
                : " : global::System.IAsyncDisposable";
            edits.Add(new LspTextEdit(
                new LspRange(
                    new LspPosition(anchorLine.LineNumber, anchorColumn),
                    new LspPosition(anchorLine.LineNumber, anchorColumn)), text));
        }

        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(
                new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
                {
                    [uri] = edits,
                }),
            "非同期Disposeパターンを生成");
    }

    private static bool IsAsyncDisposableField(
        FieldInfo field,
        SemanticModel semanticModel,
        INamedTypeSymbol asyncDisposableContract)
    {
        var symbol = field.SemanticSymbol;
        if (symbol is null && FindEquivalentField(field, semanticModel) is { } semanticField)
            symbol = semanticModel.GetDeclaredSymbol(semanticField) as IFieldSymbol;
        return symbol is not null && ImplementsContract(symbol.Type, asyncDisposableContract);
    }

    private static string AsyncDisposeField(FieldInfo field, SemanticModel semanticModel)
    {
        var symbol = field.SemanticSymbol;
        if (symbol is null && FindEquivalentField(field, semanticModel) is { } semanticField)
            symbol = semanticModel.GetDeclaredSymbol(semanticField) as IFieldSymbol;
        var type = symbol?.Type;
        var expression = field.Identifier.Text;
        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return $"    if ({expression}.HasValue)\n    {{\n"
                + $"        await {expression}.Value.DisposeAsync().ConfigureAwait(false);\n"
                + "    }";
        }
        if (type?.IsValueType == true)
            return $"    await {expression}.DisposeAsync().ConfigureAwait(false);";

        return $"    if ({expression} is not null)\n    {{\n"
            + $"        await {expression}.DisposeAsync().ConfigureAwait(false);\n"
            + "    }";
    }

    private static bool ImplementsContract(ITypeSymbol type, INamedTypeSymbol contract)
        => (type as INamedTypeSymbol is { } named &&
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, contract)) ||
            type.AllInterfaces.Any(@interface =>
                SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition, contract));

    private static bool HasAsyncDisposableContract(
        INamedTypeSymbol type, INamedTypeSymbol asyncDisposableContract)
        => type.AllInterfaces.Any(@interface =>
            SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                asyncDisposableContract));

    private static IMethodSymbol? FindOverridableDisposeAsyncCore(
        INamedTypeSymbol type, INamedTypeSymbol? valueTaskType)
    {
        if (valueTaskType is null) return null;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            var method = current.GetMembers("DisposeAsyncCore").OfType<IMethodSymbol>()
                .FirstOrDefault(candidate => candidate.Parameters.Length == 0 &&
                    SymbolEqualityComparer.Default.Equals(candidate.ReturnType, valueTaskType) &&
                    candidate.DeclaredAccessibility is RoslynAccessibility.Protected or
                        RoslynAccessibility.ProtectedAndInternal or RoslynAccessibility.ProtectedOrInternal &&
                    (candidate.IsVirtual || candidate.IsAbstract || candidate.IsOverride));
            if (method is not null) return method;
        }

        return null;
    }

    /// <summary>値型のIDisposableへnull条件演算子を適用するとコンパイルできないため、
    /// 意味モデルがある場合だけnullable値型と非nullable値型を分ける。構文fallbackは
    /// 既存の既知の参照型候補だけを通しているので、従来どおりnull-safeな呼出しを出す。</summary>
    private static string DisposeField(
        FieldInfo field,
        SemanticModel? semanticModel,
        INamedTypeSymbol? disposableContract)
    {
        if (field.SemanticSymbol is { } semanticSymbol)
        {
            var type = semanticSymbol.Type;
            var nullableValueType = type is INamedTypeSymbol named &&
                named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            return type.IsValueType && !nullableValueType
                ? $"{field.Identifier.Text}.Dispose();"
                : $"{field.Identifier.Text}?.Dispose();";
        }
        if (semanticModel is not null && disposableContract is not null &&
            FindEquivalentField(field, semanticModel) is { } semanticField &&
            semanticModel.GetDeclaredSymbol(semanticField) is IFieldSymbol symbol)
        {
            var type = symbol.Type;
            var nullableValueType = type is INamedTypeSymbol named &&
                named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            return type.IsValueType && !nullableValueType
                ? $"{field.Identifier.Text}.Dispose();"
                : $"{field.Identifier.Text}?.Dispose();";
        }

        return $"{field.Identifier.Text}?.Dispose();";
    }

    private static bool HasDisposableContract(
        TypeDeclarationSyntax type,
        SemanticModel? semanticModel,
        INamedTypeSymbol? disposableContract)
    {
        if (semanticModel is not null && disposableContract is not null &&
            FindEquivalentType(type, semanticModel) is { } semanticType &&
            semanticModel.GetDeclaredSymbol(semanticType) is INamedTypeSymbol symbol)
        {
            return symbol.AllInterfaces.Any(@interface =>
                SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition, disposableContract));
        }

        return type.BaseList?.Types
            .Select(baseType => BaseTypeName(baseType.Type))
            .Any(name => string.Equals(name, "IDisposable", StringComparison.Ordinal)) == true;
    }

    private static IMethodSymbol? FindOverridableDispose(
        INamedTypeSymbol type, INamedTypeSymbol disposableContract)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (!current.AllInterfaces.Any(@interface =>
                    SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                        disposableContract)))
                continue;

            var method = current.GetMembers("Dispose").OfType<IMethodSymbol>()
                .FirstOrDefault(candidate => candidate.Parameters.Length == 1 &&
                    candidate.Parameters[0].Type.SpecialType == SpecialType.System_Boolean &&
                    candidate.DeclaredAccessibility is RoslynAccessibility.Protected or
                        RoslynAccessibility.ProtectedAndInternal or RoslynAccessibility.ProtectedOrInternal &&
                    (candidate.IsVirtual || candidate.IsAbstract || candidate.IsOverride));
            if (method is not null) return method;
        }

        return null;
    }

    private static VariableDeclaratorSyntax? FindEquivalentField(
        FieldInfo field, SemanticModel semanticModel)
        => field.Declarator is null ? null : semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == field.Declarator.SpanStart &&
                string.Equals(candidate.Identifier.ValueText, field.Identifier.ValueText,
                    StringComparison.Ordinal));

    /// <summary>コンストラクターのパラメーターからprivate readonlyフィールドと代入を生成する。
    /// constructor bodyが複数行で、パラメーターが直接解決できる場合だけ対象にし、既存の
    /// フィールド名との衝突や、ref/outパラメーターからの不正なフィールド生成を拒否する。</summary>
    private static CSharpCodeGenerationResult GenerateFieldFromConstructorParameter(
        string filePath, SourceText source, TypeDeclarationSyntax type, int position,
        CSharpGenerationOptions options)
    {
        if (type is not ClassDeclarationSyntax)
            return Failed("フィールド生成はクラスでのみ実行できます。");

        var constructor = type.Members.OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.ParameterList.Parameters.Any(parameter =>
                position >= parameter.SpanStart && position <= parameter.Span.End));
        if (constructor?.Body is not { } body)
            return Failed("フィールドを生成するコンストラクターのパラメーターにcaretを置いてください。");

        var parameter = constructor.ParameterList.Parameters.First(candidate =>
            position >= candidate.SpanStart && position <= candidate.Span.End);
        if (parameter.Type is null || parameter.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.RefKeyword) ||
                modifier.IsKind(SyntaxKind.OutKeyword) ||
                modifier.IsKind(SyntaxKind.ThisKeyword)))
            return Failed("ref／out／拡張パラメーターからフィールドは生成できません。");

        var fieldName = ToFieldName(parameter.Identifier.ValueText, options.FieldNaming);
        if (type.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(field => field.Declaration.Variables)
            .Any(variable => string.Equals(variable.Identifier.ValueText, fieldName, StringComparison.Ordinal)))
            return Failed($"フィールド「{fieldName}」が既にあります。");

        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var fieldTarget = FindFieldInsertion(source, type);
        if (fieldTarget is null)
            return Failed("フィールドを安全に挿入できる行を見つけられません。");

        var assignmentTarget = FindConstructorAssignmentInsertion(source, constructor, body);
        if (assignmentTarget is null)
            return Failed("コンストラクター本文が複数行でないため、安全に代入を挿入できません。");

        var fieldText = $"{fieldTarget.Value.Indent}private readonly {parameter.Type} {fieldName};{newline}";
        // ValueTextは予約語の先頭に付く「@」を含まない。生成した代入側でも
        // 元のパラメーターを識別子として再構成するため、構文上のTextではなく
        // 共通のエスケープを通す（例: `string @class` → `this._class = @class`）。
        var parameterReference = EscapeIdentifier(parameter.Identifier.ValueText);
        var assignmentText = $"{assignmentTarget.Value.Indent}this.{fieldName} = {parameterReference};{newline}";
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [uri] =
            [
                new LspTextEdit(
                    new LspRange(fieldTarget.Value.Position, fieldTarget.Value.Position), fieldText),
                new LspTextEdit(
                    new LspRange(assignmentTarget.Value.Position, assignmentTarget.Value.Position),
                    assignmentText),
            ],
        };
        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(changes),
            $"パラメーター「{parameter.Identifier.ValueText}」からフィールド「{fieldName}」を生成");
    }

    private static (LspPosition Position, string Indent)? FindFieldInsertion(
        SourceText source, TypeDeclarationSyntax type)
    {
        var member = type.Members.FirstOrDefault();
        var line = member is null
            ? source.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart)
            : source.Lines.GetLineFromPosition(member.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start,
            member is null ? type.CloseBraceToken.SpanStart : member.SpanStart));
        if (prefix.Any(character => !char.IsWhiteSpace(character))) return null;
        var indent = member is null ? prefix + "    " : prefix;
        return (new LspPosition(line.LineNumber, 0), indent);
    }

    private static (LspPosition Position, string Indent)? FindConstructorAssignmentInsertion(
        SourceText source, ConstructorDeclarationSyntax constructor, BlockSyntax body)
    {
        var firstStatement = body.Statements.FirstOrDefault();
        var line = firstStatement is null
            ? source.Lines.GetLineFromPosition(body.CloseBraceToken.SpanStart)
            : source.Lines.GetLineFromPosition(firstStatement.SpanStart);
        var prefixEnd = firstStatement is null ? body.CloseBraceToken.SpanStart : firstStatement.SpanStart;
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, prefixEnd));
        if (prefix.Any(character => !char.IsWhiteSpace(character))) return null;

        var indent = firstStatement is not null
            ? prefix
            : new string(source.ToString(TextSpan.FromBounds(
                source.Lines.GetLineFromPosition(constructor.SpanStart).Start,
                constructor.SpanStart)).TakeWhile(char.IsWhiteSpace).ToArray()) + "    ";
        return (new LspPosition(line.LineNumber, 0), indent);
    }

    private static string ToFieldName(string parameterName, CSharpNamingStyle? style = null)
    {
        var name = parameterName.TrimStart('_');
        if (name.StartsWith("m_", StringComparison.Ordinal)) name = name[2..];
        if (name.Length == 0) name = "value";
        name = ApplyNamingCapitalization(name, style?.Capitalization ?? "camel_case");
        return (style?.RequiredPrefix ?? "_") + name;
    }

    /// <summary>メソッド／コンストラクターの参照型引数を検査するコード生成。
    /// 型の意味解決は行わず、明らかな値型だけを除外する。</summary>
    public static CSharpCodeGenerationResult GenerateNullGuards(
        string filePath, string text, int line, int character,
        CSharpCompilation? semanticCompilation = null)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみコード生成を実行できます。");
        var source = SourceText.From(text);
        if (line < 0 || line >= source.Lines.Count)
            return Failed("キャレット位置が文書の範囲外です。");

        var position = ClampToLine(source, line, character);
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var method = root.FindToken(position).Parent?
            .AncestorsAndSelf()
            .OfType<BaseMethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Body is not null);
        if (method?.Body is not { } body)
            return Failed("本文を持つメソッドまたはコンストラクターの中にキャレットを置いてください。");

        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        var semanticMethod = semanticModel is null
            ? null
            : FindEquivalentMethod(method, semanticModel);
        var semanticParameters = semanticMethod is null
            ? null
            : semanticMethod.ParameterList.Parameters
                .Select(parameter => (parameter, symbol: semanticModel.GetDeclaredSymbol(parameter)))
                .ToDictionary(pair => pair.parameter.SpanStart, pair => pair.symbol);
        var parameters = method.ParameterList.Parameters
            .Where(p => p.Type is not null && !p.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword)) &&
                (semanticParameters is not null
                    ? semanticParameters.TryGetValue(p.SpanStart, out var symbol) &&
                      IsReferenceLike(symbol)
                    : IsReferenceLike(p.Type)))
            .ToList();
        if (parameters.Count == 0)
            return Failed("null guardを生成できる参照型の引数がありません。");

        var bodyText = body.ToString();
        parameters = parameters.Where(p => !bodyText.Contains(
            $"ThrowIfNull({p.Identifier.Text}", StringComparison.Ordinal)).ToList();
        if (parameters.Count == 0)
            return Failed("引数のnull guardは既にあります。");

        var target = body.Statements.FirstOrDefault()?.SpanStart ?? body.CloseBraceToken.SpanStart;
        var targetLine = source.Lines.GetLineFromPosition(target);
        var prefix = source.ToString(TextSpan.FromBounds(targetLine.Start, target));
        if (prefix.Any(c => !char.IsWhiteSpace(c)))
            return Failed("メソッド本文が1行に書かれているため、安全に挿入できません。");
        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = string.Join(newline, parameters.Select(p =>
            $"{prefix}global::System.ArgumentNullException.ThrowIfNull({p.Identifier.Text});")) + newline;
        var range = new LspRange(
            new LspPosition(targetLine.LineNumber, 0),
            new LspPosition(targetLine.LineNumber, 0));
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [LspUri.FromPath(Path.GetFullPath(filePath))] = [new LspTextEdit(range, insertion)],
            });
        return new CSharpCodeGenerationResult(edit, "null guardを生成");
    }

    /// <summary>選択したJSONをC#型へ変換し、指定位置へ挿入するWorkspaceEditを作る。
    /// 入力の選択範囲自体は置換せず、生成コードを追加するため既存ソースを壊さない。</summary>
    public static CSharpCodeGenerationResult GenerateJsonTypes(
        string filePath, string text, int line, int character, string json,
        string rootTypeName = "Root", CSharpGenerationOptions? generationOptions = null)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみコード生成を実行できます。");

        var generated = JsonToCSharpGenerator.Generate(
            json, rootTypeName, generationOptions?.NullableEnabled ?? true);
        if (generated.Error is { Length: > 0 }) return Failed(generated.Error);
        if (generated.Text is not { Length: > 0 }) return Failed("生成できる型がありません。");

        var source = SourceText.From(text);
        if (line < 0 || line >= source.Lines.Count)
            return Failed("キャレット位置が文書の範囲外です。");
        var position = ClampToLine(source, line, character);
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

    /// <summary>未定義のローカル／thisメソッド呼び出しから、呼び出し先のprivateメソッドを生成する。
    /// 意味モデルを持たないため、対象は現在の型に属する呼び出しだけに限定し、引数は構文から安全に推測できる型へ落とす。</summary>
    private static (string? Text, string? Summary, string? Error) GenerateMethodFromUsage(
        TypeDeclarationSyntax type, SyntaxNode root, int position, CSharpGenerationOptions options,
        SemanticModel? semanticModel)
    {
        var invocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(candidate => position >= candidate.SpanStart && position <= candidate.Span.End)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (invocation is null || !invocation.Ancestors().Contains(type))
            return (null, null, "未定義メソッド呼び出しの中にキャレットを置いてください。");

        var methodName = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax member when member.Expression is ThisExpressionSyntax
                => member.Name.Identifier.ValueText,
            _ => "",
        };
        if (methodName.Length == 0 || !SyntaxFacts.IsValidIdentifier(methodName))
            return (null, null, "現在の型に生成できるローカル／thisメソッド呼び出しではありません。");

        var genericArity = invocation.Expression switch
        {
            GenericNameSyntax generic => generic.TypeArgumentList.Arguments.Count,
            MemberAccessExpressionSyntax member when member.Expression is ThisExpressionSyntax &&
                member.Name is GenericNameSyntax generic => generic.TypeArgumentList.Arguments.Count,
            _ => 0,
        };
        if (type.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                string.Equals(method.Identifier.ValueText, methodName, StringComparison.Ordinal)
                && method.ParameterList.Parameters.Count == invocation.ArgumentList.Arguments.Count
                && (method.TypeParameterList?.Parameters.Count ?? 0) == genericArity))
            return (null, null, "同じ名前と引数数のメソッドが既にあります。");

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new List<string>();
        foreach (var (argument, index) in invocation.ArgumentList.Arguments.Select((value, index) => (value, index)))
        {
            var requestedName = argument.NameColon?.Name.Identifier.ValueText;
            var name = MakeUniqueParameterName(
                string.IsNullOrWhiteSpace(requestedName) ? $"arg{index + 1}" : requestedName,
                usedNames, options.ParameterNaming);
            var modifier = argument.RefKindKeyword.Kind() switch
            {
                SyntaxKind.RefKeyword => "ref ",
                SyntaxKind.OutKeyword => "out ",
                SyntaxKind.InKeyword => "in ",
                _ => "",
            };
            parameters.Add($"{modifier}{InferUsageArgumentType(
                argument.Expression, options.NullableEnabled, semanticModel)} {name}");
        }

        var returnType = InferUsageReturnType(invocation, options.NullableEnabled);
        var body = "    throw new global::System.NotImplementedException();";
        var typeParameters = genericArity == 0
            ? ""
            : "<" + string.Join(", ", Enumerable.Range(1, genericArity).Select(index => $"T{index}")) + ">";
        var generated = $"private {returnType} {methodName}{typeParameters}({string.Join(", ", parameters)})\n{{\n{body}\n}}";
        return (generated, "使用箇所からメソッドを生成", null);
    }

    private static string InferUsageReturnType(InvocationExpressionSyntax invocation, bool nullableEnabled)
    {
        var returnStatement = invocation.Ancestors().OfType<ReturnStatementSyntax>().FirstOrDefault();
        if (returnStatement is not null)
        {
            var declared = returnStatement.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (declared is not null) return declared.ReturnType.ToString();
            var local = returnStatement.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault();
            if (local is not null) return local.ReturnType.ToString();
            return nullableEnabled ? "object?" : "object";
        }

        var variable = invocation.Ancestors().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Initializer?.Value, invocation));
        if (variable?.Parent?.Parent is VariableDeclarationSyntax declaration
            && !declaration.Type.IsVar)
            return declaration.Type.ToString();

        return invocation.Ancestors().OfType<ExpressionStatementSyntax>().Any()
            ? "void"
            : nullableEnabled ? "object?" : "object";
    }

    private static string InferUsageArgumentType(
        ExpressionSyntax expression, bool nullableEnabled, SemanticModel? semanticModel)
    {
        if (semanticModel is not null)
        {
            try
            {
                var semanticExpression = semanticModel.SyntaxTree.GetRoot().DescendantNodes()
                    .OfType<ExpressionSyntax>()
                    .FirstOrDefault(candidate => candidate.SpanStart == expression.SpanStart &&
                        candidate.Span.Length == expression.Span.Length &&
                        candidate.RawKind == expression.RawKind);
                if (semanticExpression is null) return InferUsageArgumentType(expression, nullableEnabled, null);
                var typeInfo = semanticModel.GetTypeInfo(semanticExpression);
                var semanticType = typeInfo.ConvertedType ?? typeInfo.Type;
                if (semanticType is { TypeKind: not TypeKind.Error })
                {
                    if (!nullableEnabled && semanticType.IsReferenceType)
                        semanticType = semanticType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
                    return DisplayGeneratedType(semanticType);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        return expression switch
        {
            LiteralExpressionSyntax literal => literal.Kind() switch
            {
                SyntaxKind.StringLiteralExpression or SyntaxKind.InterpolatedStringExpression => "string",
                SyntaxKind.CharacterLiteralExpression => "char",
                SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression => "bool",
                SyntaxKind.NumericLiteralExpression => InferNumericType(literal.Token.Text),
                SyntaxKind.NullLiteralExpression => nullableEnabled ? "object?" : "object",
                _ => nullableEnabled ? "object?" : "object",
            },
            InterpolatedStringExpressionSyntax => "string",
            ObjectCreationExpressionSyntax creation => creation.Type.ToString(),
            ArrayCreationExpressionSyntax creation => creation.Type.ToString(),
            ImplicitArrayCreationExpressionSyntax => "global::System.Array",
            CastExpressionSyntax cast => cast.Type.ToString(),
            DefaultExpressionSyntax @default => @default.Type.ToString(),
            TypeOfExpressionSyntax => "global::System.Type",
            AnonymousObjectCreationExpressionSyntax => "object",
            SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax => "global::System.Delegate",
            _ => nullableEnabled ? "object?" : "object",
        };
    }

    private static string DisplayGeneratedType(ITypeSymbol type)
    {
        if (type.SpecialType is not SpecialType.None)
            return type.SpecialType switch
            {
                SpecialType.System_Boolean => "bool",
                SpecialType.System_Byte => "byte",
                SpecialType.System_SByte => "sbyte",
                SpecialType.System_Char => "char",
                SpecialType.System_Decimal => "decimal",
                SpecialType.System_Double => "double",
                SpecialType.System_Single => "float",
                SpecialType.System_Int16 => "short",
                SpecialType.System_Int32 => "int",
                SpecialType.System_Int64 => "long",
                SpecialType.System_UInt16 => "ushort",
                SpecialType.System_UInt32 => "uint",
                SpecialType.System_UInt64 => "ulong",
                SpecialType.System_String => "string",
                SpecialType.System_Object => "object",
                _ => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            };
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static string InferNumericType(string token)
    {
        var value = token.Trim();
        if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase)) return "decimal";
        if (value.EndsWith("f", StringComparison.OrdinalIgnoreCase)) return "float";
        if (value.EndsWith("d", StringComparison.OrdinalIgnoreCase)
            || value.Contains('.', StringComparison.Ordinal)
            || value.Contains('e', StringComparison.OrdinalIgnoreCase)) return "double";
        if (value.EndsWith("ul", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("lu", StringComparison.OrdinalIgnoreCase)) return "ulong";
        if (value.EndsWith("u", StringComparison.OrdinalIgnoreCase)) return "uint";
        if (value.EndsWith("l", StringComparison.OrdinalIgnoreCase)) return "long";
        return "int";
    }

    private static (string? Text, string? Summary, string? Error) GenerateConstructor(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        // C# 12のprimary constructorは通常のConstructorDeclarationSyntaxとして
        // Membersに現れない。フィールドを持つ型でもここへ通常constructorを重ねると
        // 同じ責務の二重生成になるため、primary constructorは明示的に対象外にする。
        if (type.ParameterList is not null)
            return (null, null, "primary constructorを持つ型にはconstructorを追加できません。");

        var fields = InstanceFields(type).ToList();
        var properties = InstanceAutoProperties(type)
            .Where(property => !fields.Any(field =>
                string.Equals(ToPropertyName(field.Identifier.ValueText, options.PropertyNaming),
                    property.Identifier.ValueText, StringComparison.Ordinal)))
            .ToList();
        var semanticTypeSymbol = semanticModel is not null &&
            FindEquivalentType(type, semanticModel) is { } semanticTypeNode
            ? semanticModel.GetDeclaredSymbol(semanticTypeNode) as INamedTypeSymbol
            : null;
        var constructorMembers = fields
            .Select(field => new ConstructorMember(field.Identifier.ValueText, field.Type.ToString()))
            .Concat(properties.Select(property =>
                new ConstructorMember(property.Identifier.ValueText, property.Type.ToString())))
            .ToList();
        if (semanticTypeSymbol is not null)
            AddSemanticPartialConstructorMembers(type, semanticTypeSymbol, semanticModel!, constructorMembers,
                options.PropertyNaming);

        if (constructorMembers.Count == 0 &&
            (semanticTypeSymbol is null || !HasSemanticBaseConstructionTarget(semanticTypeSymbol)))
            return (null, null, "生成対象のインスタンスフィールドがありません。");

        var typeName = type.Identifier.ValueText;
        if (type.Members.OfType<ConstructorDeclarationSyntax>()
            .Any(c => string.Equals(c.Identifier.ValueText, typeName, StringComparison.Ordinal)))
            return (null, null, "コンストラクターが既にあります。");

        var usedParameters = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new List<string>();
        var assignments = new List<string>();
        var baseInitializer = "";
        var requiredBaseMembers = Array.Empty<ISymbol>();
        var baseSetsRequiredMembers = false;
        if (semanticTypeSymbol is { } typeSymbol)
        {
            var baseResult = GetBaseConstructor(typeSymbol);
            if (baseResult.Error is not null)
                return (null, null, baseResult.Error);
            if (baseResult.Constructor is { } baseConstructor)
            {
                var baseArguments = new List<string>();
                foreach (var parameter in baseConstructor.Parameters)
                {
                    var parameterName = MakeUniqueParameterName(parameter.Name, usedParameters,
                        options.ParameterNaming);
                    parameters.Add(FormatParameter(parameter, parameterName));
                    baseArguments.Add(FormatParameterArgument(parameter, parameterName));
                }
                baseInitializer = $" : base({string.Join(", ", baseArguments)})";
            }

            var requiredBaseResult = GetRequiredBaseMembers(typeSymbol, baseResult.Constructor);
            if (requiredBaseResult.Error is not null)
                return (null, null, requiredBaseResult.Error);
            requiredBaseMembers = requiredBaseResult.Members.ToArray();
            baseSetsRequiredMembers = requiredBaseResult.BaseConstructorSetsRequiredMembers;
            foreach (var requiredMember in requiredBaseMembers)
            {
                var parameter = MakeUniqueParameterName(requiredMember.Name, usedParameters,
                    options.ParameterNaming);
                parameters.Add($"{DisplayGeneratedType(GetMemberType(requiredMember))} {parameter}");
                assignments.Add($"base.{EscapeIdentifier(requiredMember.Name)} = {parameter};");
            }
        }
        foreach (var field in constructorMembers)
        {
            var memberName = field.Name;
            var parameter = MakeUniqueParameterName(memberName, usedParameters,
                options.ParameterNaming);
            parameters.Add($"{field.Type} {parameter}");
            assignments.Add($"this.{EscapeIdentifier(memberName)} = {parameter};");
        }

        var body = string.Join("\n", assignments.Select(a => "    " + a));
        var requiredAttribute = HasRequiredMember(type) || requiredBaseMembers.Length > 0 ||
            baseSetsRequiredMembers
            ? "[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]\n"
            : "";
        var generated = $"{requiredAttribute}public {typeName}({string.Join(", ", parameters)}){baseInitializer}\n{{\n{body}\n}}";
        return (generated, "コンストラクターを生成", null);
    }

    private static bool HasRequiredMember(TypeDeclarationSyntax type)
        => type.Members.Any(member => member switch
        {
            FieldDeclarationSyntax field => field.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.RequiredKeyword)),
            PropertyDeclarationSyntax property => property.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.RequiredKeyword)),
            _ => false,
        });

    private static void AddSemanticPartialConstructorMembers(
        TypeDeclarationSyntax activeType,
        INamedTypeSymbol typeSymbol,
        SemanticModel semanticModel,
        List<ConstructorMember> members,
        CSharpNamingStyle? propertyNaming)
    {
        var activeTree = semanticModel.SyntaxTree;
        var fieldNames = members.Select(member => member.Name)
            .ToHashSet(StringComparer.Ordinal);
        var propertyNames = activeType.Members.OfType<PropertyDeclarationSyntax>()
            .Select(property => property.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>()
                     .Where(field => !field.IsImplicitlyDeclared && !field.IsStatic && !field.IsConst &&
                         !field.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
        {
            if (!fieldNames.Add(field.Name)) continue;
            members.Add(new ConstructorMember(field.Name, DisplayGeneratedType(field.Type)));
        }

        foreach (var property in typeSymbol.GetMembers().OfType<IPropertySymbol>()
                     .Where(property => !property.IsStatic && !property.IsIndexer &&
                         !property.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
        {
            var syntax = GetPropertyDeclaration(property);
            if (syntax is null || !IsConstructorProperty(syntax) ||
                !propertyNames.Add(property.Name)) continue;
            var fieldPropertyName = fieldNames.Any(fieldName =>
                string.Equals(ToPropertyName(fieldName, propertyNaming), property.Name,
                    StringComparison.Ordinal));
            if (fieldPropertyName) continue;
            members.Add(new ConstructorMember(property.Name, DisplayGeneratedType(property.Type)));
        }
    }

    private static bool IsConstructorProperty(PropertyDeclarationSyntax property)
        => !property.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.StaticKeyword) ||
                modifier.IsKind(SyntaxKind.AbstractKeyword)) &&
            property.AccessorList is { } accessors && property.ExpressionBody is null &&
            property.Initializer is null &&
            accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null) &&
            accessors.Accessors.Any(accessor =>
                accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                accessor.IsKind(SyntaxKind.InitAccessorDeclaration) ||
                accessor.IsKind(SyntaxKind.SetAccessorDeclaration));

    private static bool IsReadableProperty(PropertyDeclarationSyntax property, bool autoOnly)
    {
        if (property.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.StaticKeyword) ||
                modifier.IsKind(SyntaxKind.AbstractKeyword)))
            return false;
        if (autoOnly)
            return IsConstructorProperty(property) &&
                property.AccessorList!.Accessors.Any(accessor =>
                    accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.InitAccessorDeclaration));
        return property.ExpressionBody is not null ||
            property.AccessorList?.Accessors.Any(accessor =>
                accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                accessor.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;
    }

    private static IEnumerable<FieldInfo> GetSemanticPartialFields(
        INamedTypeSymbol typeSymbol, SemanticModel semanticModel)
    {
        var activeTree = semanticModel.SyntaxTree;
        foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>()
                     .Where(field => !field.IsImplicitlyDeclared && !field.IsStatic && !field.IsConst &&
                         !field.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
        {
            var modifiers = field.IsReadOnly
                ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword))
                : default;
            yield return new FieldInfo(
                SyntaxFactory.ParseTypeName(DisplayGeneratedType(field.Type)),
                IdentifierToken(field.Name), modifiers, null, field);
        }
    }

    private static SyntaxToken IdentifierToken(string name)
        => SyntaxFactory.Identifier(EscapeIdentifier(name));

    private static List<ValueMember> GetSemanticValueMembers(
        TypeDeclarationSyntax type,
        CSharpGenerationOptions options,
        SemanticModel? semanticModel,
        bool autoPropertiesOnly)
    {
        var fields = InstanceFields(type).ToList();
        var members = fields.Select(field => new ValueMember(
                field.Identifier.ValueText, field.Type.ToString(), field.Identifier.Text, true))
            .ToList();
        var fieldNames = members.Select(member => member.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (semanticModel is not null &&
            FindEquivalentType(type, semanticModel) is { } semanticType &&
            semanticModel.GetDeclaredSymbol(semanticType) is INamedTypeSymbol typeSymbol)
        {
            var activeTree = semanticModel.SyntaxTree;
            foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>()
                         .Where(field => !field.IsImplicitlyDeclared && !field.IsStatic && !field.IsConst &&
                             !field.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
            {
                if (fieldNames.Add(field.Name))
                    members.Add(new ValueMember(field.Name, DisplayGeneratedType(field.Type),
                        EscapeIdentifier(field.Name), true));
            }
        }

        var fieldPropertyNames = fieldNames
            .Select(fieldName => ToPropertyName(fieldName, options.PropertyNaming))
            .ToHashSet(StringComparer.Ordinal);
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        var activeProperties = (autoPropertiesOnly
                ? InstanceReadableAutoProperties(type)
                : InstanceReadableProperties(type))
            .Where(property => !fieldPropertyNames.Contains(property.Identifier.ValueText));
        foreach (var property in activeProperties)
        {
            if (propertyNames.Add(property.Identifier.ValueText))
                members.Add(new ValueMember(property.Identifier.ValueText, property.Type.ToString(),
                    property.Identifier.Text, false));
        }

        if (semanticModel is not null &&
            FindEquivalentType(type, semanticModel) is { } propertyType &&
            semanticModel.GetDeclaredSymbol(propertyType) is INamedTypeSymbol propertyTypeSymbol)
        {
            var activeTree = semanticModel.SyntaxTree;
            foreach (var property in propertyTypeSymbol.GetMembers().OfType<IPropertySymbol>()
                         .Where(property => !property.IsStatic && !property.IsIndexer &&
                             !property.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
            {
                var syntax = GetPropertyDeclaration(property);
                if (syntax is null || !IsReadableProperty(syntax, autoPropertiesOnly) ||
                    fieldPropertyNames.Contains(property.Name) || !propertyNames.Add(property.Name))
                    continue;
                members.Add(new ValueMember(property.Name, DisplayGeneratedType(property.Type),
                    EscapeIdentifier(property.Name), false));
            }
        }
        return members;
    }

    private static PropertyDeclarationSyntax? GetPropertyDeclaration(IPropertySymbol property)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            try
            {
                if (reference.GetSyntax() is PropertyDeclarationSyntax syntax)
                    return syntax;
            }
            catch (InvalidOperationException) { }
        }
        return null;
    }

    private static bool HasSemanticBaseConstructionTarget(INamedTypeSymbol type)
    {
        var baseType = type.BaseType;
        if (baseType is null || baseType.SpecialType == SpecialType.System_Object ||
            type.TypeKind == TypeKind.Struct)
            return false;

        var constructors = baseType.InstanceConstructors
            .Where(constructor => constructor.DeclaredAccessibility is
                RoslynAccessibility.Public or RoslynAccessibility.Internal or
                RoslynAccessibility.Protected or RoslynAccessibility.ProtectedOrInternal)
            .ToArray();
        if (constructors.Any(constructor => constructor.Parameters.Length > 0))
            return true;

        for (var current = baseType;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            if (current.GetMembers().Any(IsRequiredMember))
                return true;
        }
        return false;
    }

    private static bool IsRequiredMember(ISymbol member)
        => member switch
        {
            IFieldSymbol field => field.IsRequired,
            IPropertySymbol property => property.IsRequired,
            _ => false,
        };

    private static (IMethodSymbol? Constructor, string? Error) GetBaseConstructor(
        INamedTypeSymbol type)
    {
        var baseType = type.BaseType;
        if (baseType is null || baseType.SpecialType == SpecialType.System_Object ||
            type.TypeKind == TypeKind.Struct)
            return (null, null);

        var constructors = baseType.InstanceConstructors
            .Where(constructor => constructor.DeclaredAccessibility is
                RoslynAccessibility.Public or RoslynAccessibility.Internal or
                RoslynAccessibility.Protected or RoslynAccessibility.ProtectedOrInternal)
            .ToList();
        if (constructors.Any(constructor => constructor.Parameters.Length == 0))
            return (null, null);
        if (constructors.Count == 0)
            return (null, "呼び出し可能な基底クラスコンストラクターがありません。");
        if (constructors.Count != 1)
            return (null, "基底クラスに複数のコンストラクターがあるため、呼び出し先を選択してから生成してください。");
        return (constructors[0], null);
    }

    /// <summary>基底型のrequired契約をコンストラクター生成へ引き継ぐ。
    /// base constructorがSetsRequiredMembersを持たない場合は、派生型から代入できるメンバーだけを
    /// パラメーター化し、private／readonlyなど安全に満たせない契約は生成自体を拒否する。</summary>
    private static (IReadOnlyList<ISymbol> Members, string? Error,
        bool BaseConstructorSetsRequiredMembers) GetRequiredBaseMembers(
        INamedTypeSymbol type, IMethodSymbol? selectedBaseConstructor)
    {
        var baseType = type.BaseType;
        if (baseType is null || baseType.SpecialType == SpecialType.System_Object ||
            type.TypeKind == TypeKind.Struct)
            return ([], null, false);

        var effectiveConstructor = selectedBaseConstructor ?? baseType.InstanceConstructors
            .FirstOrDefault(constructor => constructor.Parameters.Length == 0);
        var baseSetsRequiredMembers = effectiveConstructor is not null &&
            HasSetsRequiredMembers(effectiveConstructor);
        if (baseSetsRequiredMembers)
            return ([], null, true);

        var required = new List<ISymbol>();
        for (var current = baseType;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            required.AddRange(current.GetMembers().Where(IsRequiredMember));
        }

        foreach (var member in required)
        {
            if (!CanAssignRequiredMember(member))
                return ([], $"基底型のrequiredメンバー「{member.Name}」を派生コンストラクターから初期化できません。", false);
        }
        return (required, null, false);
    }

    private static bool HasSetsRequiredMembers(IMethodSymbol constructor)
        => constructor.GetAttributes().Any(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(),
                "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute",
                StringComparison.Ordinal));

    private static bool CanAssignRequiredMember(ISymbol member)
        => member switch
        {
            IFieldSymbol field => field.DeclaredAccessibility != RoslynAccessibility.Private && !field.IsReadOnly,
            IPropertySymbol property => property.SetMethod is { } setter &&
                setter.DeclaredAccessibility != RoslynAccessibility.Private,
            _ => false,
        };

    private static ITypeSymbol GetMemberType(ISymbol member)
        => member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => throw new ArgumentException("requiredメンバーの型を解決できません。", nameof(member)),
        };

    private static string FormatParameter(IParameterSymbol parameter, string name)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => parameter.IsParams ? "params " : "",
        };
        var type = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{modifier}{type} {EscapeIdentifier(name)}";
    }

    private static string FormatParameterArgument(IParameterSymbol parameter, string name)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => "",
        };
        return modifier + EscapeIdentifier(name);
    }

    private static IEnumerable<PropertyDeclarationSyntax> InstanceAutoProperties(
        TypeDeclarationSyntax type)
    {
        foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (property.Modifiers.Any(modifier =>
                    modifier.IsKind(SyntaxKind.StaticKeyword) ||
                    modifier.IsKind(SyntaxKind.AbstractKeyword)))
                continue;
            if (property.AccessorList is not { } accessors || property.ExpressionBody is not null ||
                property.Initializer is not null ||
                accessors.Accessors.Any(accessor => accessor.Body is not null ||
                    accessor.ExpressionBody is not null))
                continue;
            if (!accessors.Accessors.Any(accessor =>
                    accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.InitAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.SetAccessorDeclaration)))
                continue;
            yield return property;
        }
    }

    private static IEnumerable<PropertyDeclarationSyntax> InstanceReadableAutoProperties(
        TypeDeclarationSyntax type)
        => InstanceAutoProperties(type).Where(property =>
            property.AccessorList!.Accessors.Any(accessor =>
                accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                accessor.IsKind(SyntaxKind.InitAccessorDeclaration)));

    /// <summary>式本体を含む、値を読み取れるinstance propertyを返す。
    /// setter-only propertyをDeconstructの出力へ混ぜない。</summary>
    private static IEnumerable<PropertyDeclarationSyntax> InstanceReadableProperties(
        TypeDeclarationSyntax type)
    {
        foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (property.Modifiers.Any(modifier =>
                    modifier.IsKind(SyntaxKind.StaticKeyword) ||
                    modifier.IsKind(SyntaxKind.AbstractKeyword)))
                continue;
            if (property.ExpressionBody is not null)
            {
                yield return property;
                continue;
            }
            if (property.AccessorList?.Accessors.Any(accessor =>
                    accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.InitAccessorDeclaration)) == true)
                yield return property;
        }
    }

    private static (string? Text, string? Summary, string? Error) GenerateProperties(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        var existing = type.Members.OfType<PropertyDeclarationSyntax>()
            .Select(p => p.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        var fields = InstanceFields(type)
            .Select(field => new PropertyGenerationField(
                field.Type.ToString(), field.Identifier.ValueText,
                field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ReadOnlyKeyword))))
            .ToList();
        if (semanticModel is not null &&
            FindEquivalentType(type, semanticModel) is { } semanticType &&
            semanticModel.GetDeclaredSymbol(semanticType) is INamedTypeSymbol typeSymbol)
        {
            var activeTree = semanticModel.SyntaxTree;
            var fieldNames = fields.Select(field => field.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>()
                         .Where(field => !field.IsImplicitlyDeclared && !field.IsStatic && !field.IsConst &&
                             !field.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == activeTree)))
            {
                if (fieldNames.Add(field.Name))
                    fields.Add(new PropertyGenerationField(
                        DisplayGeneratedType(field.Type), field.Name, field.IsReadOnly));
            }
        }

        fields = fields
            .Where(field => !existing.Contains(ToPropertyName(field.Name, options.PropertyNaming)))
            .ToList();
        if (fields.Count == 0)
            return (null, null, "生成対象のフィールドがないか、プロパティが既にあります。");

        var members = fields.Select(field =>
        {
            var fieldName = EscapeIdentifier(field.Name);
            var propertyName = ToPropertyName(field.Name, options.PropertyNaming);
            var readOnly = field.ReadOnly;
            var setter = readOnly ? "" : $" set => {fieldName} = value;";
            return $"public {field.Type} {propertyName} {{ get => {fieldName};{setter} }}";
        });
        return (string.Join("\n\n", members), "プロパティを生成", null);
    }

    private static (string? Text, string? Summary, string? Error) GenerateEquality(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        if (type is RecordDeclarationSyntax)
            return (null, null, "recordはコンパイラが値等価性を生成するため、Equals／GetHashCodeを追加しません。");

        var members = GetSemanticValueMembers(type, options, semanticModel, autoPropertiesOnly: true);
        if (members.Count == 0)
            return (null, null, "比較対象のインスタンスフィールドまたはauto-propertyがありません。");

        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Select(m => m.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        if (methods.Contains("Equals") || methods.Contains("GetHashCode"))
            return (null, null, "Equals または GetHashCode が既にあります。");

        var typeName = type.Identifier.ValueText;
        var comparisonLines = new List<string> { $"    return obj is {typeName} other" };
        comparisonLines.AddRange(members.Select(member =>
            $"        && global::System.Object.Equals({member.Expression}, other.{member.Expression})"));
        comparisonLines[^1] += ";";
        var comparisons = string.Join("\n", comparisonLines);
        var hashExpressions = members.Select(member => member.Expression)
            .ToList();
        var hash = hashExpressions.Count <= 8
            ? $"return global::System.HashCode.Combine({string.Join(", ", hashExpressions)});"
            : string.Join("\n", [
                "var hash = new global::System.HashCode();",
                .. hashExpressions.Select(expression => $"hash.Add({expression});"),
                "return hash.ToHashCode();",
            ]);
        var objectType = options.NullableEnabled ? "object?" : "object";
        var generated = $"public override bool Equals({objectType} obj)\n{{\n" +
            $"{comparisons}\n" +
            "}\n\n" +
            "public override int GetHashCode()\n{\n" + hash + "\n}";
        return (generated, "Equals／GetHashCodeを生成", null);
    }

    private static (string? Text, string? Summary, string? Error) GenerateToString(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        if (type is RecordDeclarationSyntax)
            return (null, null, "recordはコンパイラがToStringを生成するため、追加しません。");

        if (type.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                string.Equals(method.Identifier.ValueText, "ToString", StringComparison.Ordinal) &&
                method.ParameterList.Parameters.Count == 0))
            return (null, null, "ToStringメソッドが既にあります。");

        var members = GetSemanticValueMembers(type, options, semanticModel, autoPropertiesOnly: false);
        if (members.Count == 0)
            return (null, null, "ToStringに含めるインスタンスメンバーがありません。");

        var parts = string.Join(", ", members.Select(member =>
            $"{{nameof({member.Expression})}}={{{member.Expression}}}"));
        var generated = "public override string ToString()\n{\n"
            + $"    return $\"{parts}\";\n"
            + "}";
        return (generated, "ToStringを生成", null);
    }

    /// <summary>インスタンスフィールド／読み取り可能なプロパティからDeconstructを生成する。
    /// recordはコンパイラーが既に生成するため対象外とし、indexer・static・write-onlyは含めない。</summary>
    private static (string? Text, string? Summary, string? Error) GenerateDeconstruct(
        TypeDeclarationSyntax type, CSharpGenerationOptions options, SemanticModel? semanticModel)
    {
        if (type is RecordDeclarationSyntax)
            return (null, null, "recordはコンパイラーがDeconstructを生成するため、追加しません。");

        var members = GetSemanticValueMembers(type, options, semanticModel, autoPropertiesOnly: false)
            .Select(member =>
                (member.Type, member.Name, Expression: "this." + member.Expression))
            .ToList();

        if (members.Count == 0)
            return (null, null, "Deconstructに含めるインスタンスメンバーがありません。");

        var existingArities = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => string.Equals(method.Identifier.ValueText, "Deconstruct",
                StringComparison.Ordinal))
            .Select(method => method.ParameterList.Parameters.Count)
            .ToHashSet();
        if (existingArities.Contains(members.Count))
            return (null, null, "同じ引数数のDeconstructメソッドが既にあります。");

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var parameters = members.Select(member =>
        {
            var parameterName = MakeUniqueParameterName(member.Name, usedNames, options.ParameterNaming);
            return (member.Type, Name: parameterName, member.Expression);
        }).ToList();
        var parameterText = string.Join(", ", parameters.Select(parameter =>
            $"out {parameter.Type} {parameter.Name}"));
        var assignments = string.Join("\n", parameters.Select(parameter =>
            $"{parameter.Name} = {parameter.Expression};"));
        return ($"public void Deconstruct({parameterText})\n{{\n" +
                string.Join("\n", assignments.Split('\n').Select(line => "    " + line)) +
                "\n}", "Deconstructを生成", null);
    }

    private static (string? Text, string? Summary, string? Error) GenerateInterfaceMembers(
        TypeDeclarationSyntax type, IReadOnlyList<SyntaxNode> roots, SemanticModel? semanticModel)
    {
        var semanticTypeSymbol = semanticModel is not null
            ? FindEquivalentType(type, semanticModel) is { } semanticType
                ? semanticModel.GetDeclaredSymbol(semanticType) as INamedTypeSymbol
                : null
            : null;
        var semanticResult = semanticModel is null
            ? null
            : GenerateSemanticInterfaceMembers(type, semanticModel);

        var interfaces = semanticModel is not null
            ? FindSemanticInterfaceHierarchy(type, semanticModel).ToList()
            : FindInterfaceHierarchy(type, roots,
                type.BaseList?.Types.Select(baseType => BaseTypeName(baseType.Type)) ?? [])
                .ToList();
        if (interfaces.Count == 0 && semanticModel is not null)
        {
            // メタデータだけのinterfaceはソースstubを生成できないため、同一ソースのfallbackを
            // 最後に試す。ただし意味モデルで一意に解決できた結果を名前検索で広げない。
            interfaces = FindInterfaceHierarchy(type, roots,
                type.BaseList?.Types.Select(baseType => BaseTypeName(baseType.Type)) ?? [])
                .ToList();
        }
        if (interfaces.Count == 0 && semanticResult?.Text is null)
            return semanticResult ?? (null, null, "実装対象のインターフェース定義を同じファイル内で解決できません。");

        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in interfaces)
        {
            foreach (var member in contract.Members)
            {
                if (member is EventFieldDeclarationSyntax eventFields)
                {
                    if (!IsImplementableContractMember(eventFields)) continue;
                    foreach (var variable in eventFields.Declaration.Variables)
                    {
                        var eventKey = "event:" + variable.Identifier.ValueText;
                        if (!generatedKeys.Add(eventKey) || HasEvent(type, variable.Identifier.ValueText) ||
                            (semanticTypeSymbol is not null &&
                             HasSemanticInterfaceImplementation(semanticTypeSymbol, eventFields, semanticModel!,
                                 variable.Identifier.ValueText)))
                            continue;
                        generated.Add(GenerateEventStub(eventFields.Declaration.Type,
                            variable.Identifier.ValueText, "public"));
                    }
                    continue;
                }
                var key = member switch
                {
                    MethodDeclarationSyntax method => MethodKey(method.Identifier.ValueText, method.ParameterList),
                    PropertyDeclarationSyntax property => "property:" + property.Identifier.ValueText,
                    EventDeclarationSyntax @event => "event:" + @event.Identifier.ValueText,
                    _ => "",
                };
                if (key.Length == 0 || !generatedKeys.Add(key)) continue;

                switch (member)
                {
                    case MethodDeclarationSyntax method when IsImplementableContractMember(method)
                        && !HasMethod(type, method)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticInterfaceImplementation(semanticTypeSymbol, method, semanticModel!)):
                        generated.Add(GenerateMethodStub(method, "public"));
                        break;
                    case PropertyDeclarationSyntax property when IsImplementableContractMember(property)
                        && !HasProperty(type, property.Identifier.ValueText)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticInterfaceImplementation(semanticTypeSymbol, property, semanticModel!)):
                        generated.Add(GeneratePropertyStub(property, "public"));
                        break;
                    case EventDeclarationSyntax @event when IsImplementableContractMember(@event)
                        && !HasEvent(type, @event.Identifier.ValueText)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticInterfaceImplementation(semanticTypeSymbol, @event, semanticModel!)):
                        generated.Add(GenerateEventStub(@event, "public"));
                        break;
                }
            }
        }

        if (semanticResult?.Text is { Length: > 0 } semanticText)
            generated.Add(semanticText);

        if (generated.Count == 0)
            return semanticResult ?? (null, null, "インターフェースの未実装メンバーがないか、構文だけでは生成できないメンバーです。");
        return (string.Join("\n\n", generated), "インターフェースメンバーを生成", null);
    }

    /// <summary>ソース宣言を持たないBCL／NuGet interfaceを意味モデルから生成する。
    /// SourceDeclarationsが空でも、member symbolのidentityと型引数は失わない。</summary>
    private static (string? Text, string? Summary, string? Error)? GenerateSemanticInterfaceMembers(
        TypeDeclarationSyntax type, SemanticModel semanticModel)
    {
        var semanticType = FindEquivalentType(type, semanticModel);
        if (semanticType is null ||
            semanticModel.GetDeclaredSymbol(semanticType) is not INamedTypeSymbol typeSymbol)
            return null;

        var interfaces = typeSymbol.AllInterfaces
            .Where(contract => contract.TypeKind == TypeKind.Interface)
            .GroupBy(contract => contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (interfaces.Length == 0) return null;

        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in interfaces.Where(contract =>
                     !SourceDeclarations<InterfaceDeclarationSyntax>(contract).Any()))
        {
            foreach (var member in contract.GetMembers())
            {
                if (!IsAbstractInterfaceMember(member) ||
                    typeSymbol.FindImplementationForInterfaceMember(member) is not null)
                    continue;

                switch (member)
                {
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    {
                        var key = "method:" + method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        if (generatedKeys.Add(key)) generated.Add(GenerateMethodStub(method));
                        break;
                    }
                    case IPropertySymbol property:
                    {
                        var key = "property:" + property.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        if (generatedKeys.Add(key)) generated.Add(GeneratePropertyStub(property));
                        break;
                    }
                    case IEventSymbol @event:
                    {
                        var key = "event:" + @event.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        if (generatedKeys.Add(key)) generated.Add(GenerateEventStub(@event));
                        break;
                    }
                }
            }
        }

        return generated.Count == 0
            ? (null, null, "インターフェースの未実装メンバーがありません。")
            : (string.Join("\n\n", generated), "インターフェースメンバーを生成", null);
    }

    private static bool IsAbstractInterfaceMember(ISymbol member)
        => member switch
        {
            // Interface members in metadata do not consistently expose IsAbstract across
            // compiler/runtime versions. Instance members without a default body are the
            // contract; static members are intentionally excluded from this MVP generator.
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary && !method.IsStatic,
            IPropertySymbol property => !property.IsStatic,
            IEventSymbol @event => !@event.IsStatic,
            _ => false,
        };

    private static string GenerateMethodStub(IMethodSymbol method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
        var typeParameters = method.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", method.TypeParameters.Select(p => EscapeIdentifier(p.Name))) + ">";
        var constraints = string.Join(" ", method.TypeParameters
            .Select(FormatTypeParameterConstraints)
            .Where(value => value.Length > 0));
        var returnRef = method.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.RefReadOnly => "ref readonly ",
            _ => "",
        };
        var returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"public {returnRef}{returnType} {EscapeIdentifier(method.Name)}{typeParameters}({parameters}){constraints}\n{{\n    throw new global::System.NotImplementedException();\n}}";
    }

    private static string GeneratePropertyStub(IPropertySymbol property)
    {
        var type = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var name = property.IsIndexer
            ? "this[" + string.Join(", ", property.Parameters.Select(FormatParameter)) + "]"
            : EscapeIdentifier(property.Name);
        var accessors = new List<string>();
        if (property.GetMethod is not null) accessors.Add("get;");
        if (property.SetMethod is not null)
            accessors.Add(property.SetMethod.IsInitOnly ? "init;" : "set;");
        if (accessors.Count == 0) return "";
        return $"public {type} {name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateEventStub(IEventSymbol @event)
    {
        var type = @event.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"public event {type} {EscapeIdentifier(@event.Name)}\n{{\n    add => throw new global::System.NotImplementedException();\n    remove => throw new global::System.NotImplementedException();\n}}";
    }

    /// <summary>partial型の別宣言に既存実装があるかを、構文上の名前ではなくsymbol identityで確認する。
    /// active fileだけを見ると、ImplementInterface／OverrideMembersが重複メンバーを生成してしまう。</summary>
    private static bool HasSemanticInterfaceImplementation(
        INamedTypeSymbol typeSymbol, MemberDeclarationSyntax member, SemanticModel semanticModel,
        string? memberName = null)
    {
        foreach (var memberSymbol in DeclaredMemberSymbols(member, semanticModel, memberName))
        {
            if (typeSymbol.FindImplementationForInterfaceMember(memberSymbol) is not null)
                return true;
        }
        return false;
    }

    private static bool HasSemanticOverride(
        INamedTypeSymbol typeSymbol, MemberDeclarationSyntax member, SemanticModel semanticModel,
        string? memberName = null)
    {
        foreach (var memberSymbol in DeclaredMemberSymbols(member, semanticModel, memberName))
        {
            if (typeSymbol.GetMembers(memberSymbol.Name).Any(existing =>
                    IsSameOverridableSignature(existing, memberSymbol)))
                return true;
        }
        return false;
    }

    private static IEnumerable<ISymbol> DeclaredMemberSymbols(
        MemberDeclarationSyntax member, SemanticModel semanticModel, string? memberName = null)
    {
        SemanticModel model;
        try
        {
            model = semanticModel.Compilation.GetSemanticModel(member.SyntaxTree);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        switch (member)
        {
            case MethodDeclarationSyntax method when model.GetDeclaredSymbol(method) is { } methodSymbol:
                yield return methodSymbol;
                break;
            case PropertyDeclarationSyntax property when model.GetDeclaredSymbol(property) is { } propertySymbol:
                yield return propertySymbol;
                break;
            case EventDeclarationSyntax @event when model.GetDeclaredSymbol(@event) is { } eventSymbol:
                yield return eventSymbol;
                break;
            case EventFieldDeclarationSyntax eventFields:
                foreach (var variable in eventFields.Declaration.Variables)
                {
                    if (memberName is not null &&
                        !string.Equals(variable.Identifier.ValueText, memberName, StringComparison.Ordinal))
                        continue;
                    if (model.GetDeclaredSymbol(variable) is { } variableSymbol)
                        yield return variableSymbol;
                }
                break;
        }
    }

    private static string FormatParameter(IParameterSymbol parameter)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => parameter.IsParams ? "params " : "",
        };
        var type = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{modifier}{type} {EscapeIdentifier(parameter.Name)}";
    }

    private static string FormatTypeParameterConstraints(ITypeParameterSymbol parameter)
    {
        var constraints = new List<string>();
        if (parameter.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
        else if (parameter.HasValueTypeConstraint) constraints.Add("struct");
        else if (parameter.HasReferenceTypeConstraint) constraints.Add("class");
        if (parameter.HasNotNullConstraint) constraints.Add("notnull");
        constraints.AddRange(parameter.ConstraintTypes.Select(type =>
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        if (parameter.HasConstructorConstraint) constraints.Add("new()");
        return constraints.Count == 0
            ? ""
            : "where " + EscapeIdentifier(parameter.Name) + " : " + string.Join(", ", constraints);
    }

    private static string EscapeIdentifier(string name)
        => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

    /// <summary>キャレット位置のフィールドが実装するinterfaceから、単純な委譲メンバーを生成する。
    /// 意味モデルなしでは型引数の代入や明示的実装を解決できないため、非ジェネリックなinterfaceの
    /// 通常メソッド／プロパティ／イベントだけを対象にする。</summary>
    private static (string? Text, string? Summary, string? Error) GenerateDelegatingMembers(
        TypeDeclarationSyntax type, IReadOnlyList<SyntaxNode> roots, int position,
        SemanticModel? semanticModel)
    {
        var fields = type.Members.OfType<FieldDeclarationSyntax>()
            .Where(field => field.Declaration.Variables.Count == 1
                && position >= field.SpanStart && position <= field.Span.End)
            .ToList();
        if (fields.Count != 1)
            return (null, null, "委譲先フィールドの中にキャレットを置いてください。");

        var field = fields[0];
        if (field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)
                || modifier.IsKind(SyntaxKind.ConstKeyword)))
            return (null, null, "インスタンスフィールドからのみ委譲メンバーを生成できます。");
        if (field.Declaration.Type.DescendantNodesAndSelf().OfType<GenericNameSyntax>().Any())
        {
            // Roslynの意味モデルがあれば、IList<string>のような構築済み型も安全に解決できる。
            if (semanticModel is null)
                return (null, null, "ジェネリックな委譲先は構文だけでは型引数を解決できません。");
        }

        if (semanticModel is not null &&
            GenerateSemanticDelegatingMembers(type, field, semanticModel) is { } semanticResult)
            return semanticResult;

        var delegateName = BaseTypeName(field.Declaration.Type);
        var contracts = semanticModel is not null
            ? FindSemanticFieldInterfaceHierarchy(field, semanticModel).ToList()
            : FindInterfaceHierarchy(type, roots, [delegateName]).ToList();
        if (contracts.Count == 0 && semanticModel is not null)
            contracts = FindInterfaceHierarchy(type, roots, [delegateName]).ToList();
        if (contracts.Count == 0)
            return (null, null, "委譲先のinterface定義を同じワークスペース内で一意に解決できません。");

        var fieldName = field.Declaration.Variables[0].Identifier.ValueText;
        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in contracts.SelectMany(contract => contract.Members))
        {
            if (!IsPubliclyDelegatable(member)) continue;

            switch (member)
            {
                case MethodDeclarationSyntax method
                    when method.TypeParameterList is null
                        && !method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword))
                        && !HasMethod(type, method)
                        && generatedKeys.Add(MethodKey(method.Identifier.ValueText, method.ParameterList)):
                    generated.Add(GenerateDelegatingMethod(method, fieldName));
                    break;
                case PropertyDeclarationSyntax property
                    when property.AccessorList is not null
                        && !property.AccessorList.Accessors.Any(accessor =>
                            accessor.IsKind(SyntaxKind.InitAccessorDeclaration))
                        && !HasProperty(type, property.Identifier.ValueText)
                        && generatedKeys.Add("property:" + property.Identifier.ValueText):
                    generated.Add(GenerateDelegatingProperty(property, fieldName));
                    break;
                case EventDeclarationSyntax @event
                    when !HasEvent(type, @event.Identifier.ValueText)
                        && generatedKeys.Add("event:" + @event.Identifier.ValueText):
                    generated.Add(GenerateDelegatingEvent(@event.Type, @event.Identifier.ValueText, fieldName));
                    break;
                case EventFieldDeclarationSyntax eventFields:
                    foreach (var variable in eventFields.Declaration.Variables)
                    {
                        var key = "event:" + variable.Identifier.ValueText;
                        if (!generatedKeys.Add(key) || HasEvent(type, variable.Identifier.ValueText)) continue;
                        generated.Add(GenerateDelegatingEvent(
                            eventFields.Declaration.Type, variable.Identifier.ValueText, fieldName));
                    }
                    break;
            }
        }

        return generated.Count == 0
            ? (null, null, "委譲可能なinterfaceメンバーがないか、既に実装されています。")
            : (string.Join("\n\n", generated),
                $"フィールド「{fieldName}」から委譲メンバーを生成", null);
    }

    /// <summary>ソース宣言を持たないBCL／NuGet interfaceをフィールドから委譲する。
    /// 構築済みgeneric typeの型引数もsymbolから展開して、objectへの劣化を避ける。</summary>
    private static (string? Text, string? Summary, string? Error)? GenerateSemanticDelegatingMembers(
        TypeDeclarationSyntax type, FieldDeclarationSyntax field, SemanticModel semanticModel)
    {
        var semanticField = semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == field.SpanStart);
        var semanticVariable = semanticField?.Declaration.Variables.FirstOrDefault(variable =>
            string.Equals(variable.Identifier.ValueText,
                field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
                StringComparison.Ordinal));
        if (semanticVariable is null || semanticModel.GetDeclaredSymbol(semanticVariable) is not IFieldSymbol fieldSymbol ||
            fieldSymbol.Type is not INamedTypeSymbol fieldType)
            return null;

        var contracts = new[] { fieldType }
            .Concat(fieldType.AllInterfaces)
            .Where(contract => contract.TypeKind == TypeKind.Interface)
            .GroupBy(contract => contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (contracts.Length == 0 || contracts.Any(contract =>
                SourceDeclarations<InterfaceDeclarationSyntax>(contract).Any()))
            return null;

        var containingType = semanticModel.GetDeclaredSymbol(
            FindEquivalentType(type, semanticModel)!) as INamedTypeSymbol;
        if (containingType is null) return null;

        var fieldName = fieldSymbol.Name;
        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        foreach (var member in contract.GetMembers())
        {
            if (!IsAbstractInterfaceMember(member) ||
                containingType.FindImplementationForInterfaceMember(member) is not null)
                continue;

            switch (member)
            {
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    if (generatedKeys.Add(SymbolMemberKey(method)))
                        generated.Add(GenerateDelegatingMethod(method, fieldName));
                    break;
                case IPropertySymbol property when !property.IsWriteOnly &&
                    (property.SetMethod is null || !property.SetMethod.IsInitOnly):
                    if (generatedKeys.Add(SymbolMemberKey(property)))
                        generated.Add(GenerateDelegatingProperty(property, fieldName));
                    break;
                case IEventSymbol @event:
                    if (generatedKeys.Add(SymbolMemberKey(@event)))
                        generated.Add(GenerateDelegatingEvent(@event, fieldName));
                    break;
            }
        }

        return generated.Count == 0
            ? (null, null, "委譲可能なinterfaceメンバーがないか、既に実装されています。")
            : (string.Join("\n\n", generated),
                $"フィールド「{fieldName}」から委譲メンバーを生成", null);
    }

    private static string GenerateDelegatingMethod(IMethodSymbol method, string fieldName)
    {
        var typeParameters = method.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", method.TypeParameters.Select(p => EscapeIdentifier(p.Name))) + ">";
        var call = fieldName + "." + EscapeIdentifier(method.Name) + typeParameters + "(" +
            string.Join(", ", method.Parameters.Select(FormatParameterArgument)) + ")";
        var body = method.ReturnsVoid ? $"    {call};" : $"    return {call};";
        var constraints = string.Join(" ", method.TypeParameters
            .Select(FormatTypeParameterConstraints)
            .Where(value => value.Length > 0));
        return $"public {FormatRefReturn(method)}{method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {EscapeIdentifier(method.Name)}{typeParameters}({string.Join(", ", method.Parameters.Select(FormatParameter))}){constraints}\n{{\n{body}\n}}";
    }

    private static string GenerateDelegatingProperty(IPropertySymbol property, string fieldName)
    {
        var type = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var name = property.IsIndexer
            ? "this[" + string.Join(", ", property.Parameters.Select(FormatParameter)) + "]"
            : EscapeIdentifier(property.Name);
        var arguments = string.Join(", ", property.Parameters.Select(FormatParameterArgument));
        var receiver = fieldName + (property.IsIndexer ? "[" + arguments + "]" : "." + EscapeIdentifier(property.Name));
        var accessors = new List<string>();
        if (property.GetMethod is not null) accessors.Add("get => " + receiver + ";");
        if (property.SetMethod is not null) accessors.Add("set => " + receiver + " = value;");
        return $"public {type} {name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateDelegatingEvent(IEventSymbol @event, string fieldName)
        => $"public event {@event.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {EscapeIdentifier(@event.Name)}\n{{\n    add => {fieldName}.{EscapeIdentifier(@event.Name)} += value;\n    remove => {fieldName}.{EscapeIdentifier(@event.Name)} -= value;\n}}";

    private static string FormatRefReturn(IMethodSymbol method)
        => method.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.RefReadOnly => "ref readonly ",
            _ => "",
        };

    private static string FormatParameterArgument(IParameterSymbol parameter)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => "",
        };
        return modifier + EscapeIdentifier(parameter.Name);
    }

    private static string SymbolMemberKey(ISymbol member)
        => member switch
        {
            IMethodSymbol method => "method:" + method.Name + "/" + method.Arity + "/" +
                string.Join(",", method.Parameters.Select(parameter =>
                    parameter.RefKind + ":" + parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))),
            IPropertySymbol property => "property:" + property.Name + "/" + property.IsIndexer + "/" +
                string.Join(",", property.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))),
            IEventSymbol @event => "event:" + @event.Name,
            _ => member.Kind + ":" + member.Name,
        };

    private static string GenerateDelegatingMethod(MethodDeclarationSyntax method, string fieldName)
    {
        var call = fieldName + "." + method.Identifier.ValueText + "("
            + string.Join(", ", method.ParameterList.Parameters.Select(FormatParameterArgument)) + ")";
        var body = IsVoid(method.ReturnType) ? $"    {call};" : $"    return {call};";
        var constraints = method.ConstraintClauses.Count == 0
            ? ""
            : " " + string.Join(" ", method.ConstraintClauses.Select(clause => clause.ToString()));
        return $"public {method.ReturnType} {method.Identifier}({FormatParameters(method.ParameterList)}){constraints}\n{{\n{body}\n}}";
    }

    private static string GenerateDelegatingProperty(PropertyDeclarationSyntax property, string fieldName)
    {
        var accessors = property.AccessorList!.Accessors
            .Where(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
                || accessor.IsKind(SyntaxKind.SetAccessorDeclaration))
            .Select(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration)
                ? $"get => {fieldName}.{property.Identifier.ValueText};"
                : $"set => {fieldName}.{property.Identifier.ValueText} = value;")
            .ToList();
        return $"public {property.Type} {property.Identifier} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateDelegatingEvent(TypeSyntax eventType, string name, string fieldName)
        => $"public event {eventType} {name}\n{{\n    add => {fieldName}.{name} += value;\n    remove => {fieldName}.{name} -= value;\n}}";

    private static bool IsPubliclyDelegatable(MemberDeclarationSyntax member)
        => !member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)
            || modifier.IsKind(SyntaxKind.PrivateKeyword)
            || modifier.IsKind(SyntaxKind.ProtectedKeyword)
            || modifier.IsKind(SyntaxKind.InternalKeyword));

    private static string FormatParameterArgument(ParameterSyntax parameter)
    {
        var modifier = parameter.Modifiers.Any(token => token.IsKind(SyntaxKind.RefKeyword)) ? "ref "
            : parameter.Modifiers.Any(token => token.IsKind(SyntaxKind.OutKeyword)) ? "out "
            : parameter.Modifiers.Any(token => token.IsKind(SyntaxKind.InKeyword)) ? "in "
            : "";
        return modifier + parameter.Identifier.ValueText;
    }

    private static bool IsVoid(TypeSyntax type)
        => type is PredefinedTypeSyntax predefined
            && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    private static (string? Text, string? Summary, string? Error) GenerateOverrideMembers(
        TypeDeclarationSyntax type, IReadOnlyList<SyntaxNode> roots, SemanticModel? semanticModel)
    {
        var semanticTypeSymbol = semanticModel is not null
            ? FindEquivalentType(type, semanticModel) is { } semanticType
                ? semanticModel.GetDeclaredSymbol(semanticType) as INamedTypeSymbol
                : null
            : null;
        if (semanticModel is not null &&
            GenerateSemanticOverrideMembers(type, semanticModel) is { } semanticResult)
            return semanticResult;

        var bases = semanticModel is not null
            ? FindSemanticBaseDeclarations(type, semanticModel).ToList()
            : type.BaseList?.Types
                .Select(baseType => BaseTypeName(baseType.Type))
                .Where(name => name.Length > 0)
                .SelectMany(name => FindRelatedTypes<ClassDeclarationSyntax>(type, roots, name))
                .Distinct()
                .ToList() ?? [];
        if (bases.Count == 0 && semanticModel is not null)
        {
            bases = type.BaseList?.Types
                .Select(baseType => BaseTypeName(baseType.Type))
                .Where(name => name.Length > 0)
                .SelectMany(name => FindRelatedTypes<ClassDeclarationSyntax>(type, roots, name))
                .Distinct()
                .ToList() ?? [];
        }
        if (bases.Count == 0)
            return (null, null, "override対象の基底クラス定義を同じファイル内で解決できません。");

        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var baseType in bases)
        {
            foreach (var member in baseType.Members)
            {
                if (member is EventFieldDeclarationSyntax eventFields)
                {
                    if (!IsOverridableBaseMember(eventFields)) continue;
                    foreach (var variable in eventFields.Declaration.Variables)
                    {
                        var eventKey = "event:" + variable.Identifier.ValueText;
                        if (!generatedKeys.Add(eventKey) || HasEvent(type, variable.Identifier.ValueText) ||
                            (semanticTypeSymbol is not null &&
                             HasSemanticOverride(semanticTypeSymbol, eventFields, semanticModel!,
                                 variable.Identifier.ValueText)))
                            continue;
                        generated.Add(GenerateOverrideEventStub(eventFields.Declaration.Type,
                            variable.Identifier.ValueText, AccessModifier(eventFields.Modifiers) + " override",
                            eventFields.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AbstractKeyword))));
                    }
                    continue;
                }
                if (!IsOverridableBaseMember(member)) continue;
                var key = member switch
                {
                    MethodDeclarationSyntax method => MethodKey(method.Identifier.ValueText, method.ParameterList),
                    PropertyDeclarationSyntax property => "property:" + property.Identifier.ValueText,
                    EventDeclarationSyntax @event => "event:" + @event.Identifier.ValueText,
                    _ => "",
                };
                if (key.Length == 0 || !generatedKeys.Add(key)) continue;

                switch (member)
                {
                    case MethodDeclarationSyntax method when !HasMethod(type, method)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticOverride(semanticTypeSymbol, method, semanticModel!)):
                        generated.Add(GenerateOverrideMethodStub(
                            method, AccessModifier(method.Modifiers) + " override"));
                        break;
                    case PropertyDeclarationSyntax property when !HasProperty(type, property.Identifier.ValueText)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticOverride(semanticTypeSymbol, property, semanticModel!)):
                        generated.Add(GenerateOverridePropertyStub(
                            property, AccessModifier(property.Modifiers) + " override"));
                        break;
                    case EventDeclarationSyntax @event when !HasEvent(type, @event.Identifier.ValueText)
                        && (semanticTypeSymbol is null ||
                            !HasSemanticOverride(semanticTypeSymbol, @event, semanticModel!)):
                        generated.Add(GenerateOverrideEventStub(
                            @event, AccessModifier(@event.Modifiers) + " override"));
                        break;
                }
            }
        }

        return generated.Count == 0
            ? (null, null, "override可能な未実装メンバーがありません。")
            : (string.Join("\n\n", generated), "overrideメンバーを生成", null);
    }

    /// <summary>ワークスペース内に基底classのソースが無い場合のoverride生成。
    /// BCL／NuGet型のvirtual・abstract memberも、symbolから完全修飾型を取得してstub化する。</summary>
    private static (string? Text, string? Summary, string? Error)? GenerateSemanticOverrideMembers(
        TypeDeclarationSyntax type, SemanticModel semanticModel)
    {
        var semanticType = FindEquivalentType(type, semanticModel);
        if (semanticType is null ||
            semanticModel.GetDeclaredSymbol(semanticType) is not INamedTypeSymbol typeSymbol ||
            typeSymbol.BaseType is not { } baseType)
            return null;

        if (SourceDeclarations<ClassDeclarationSyntax>(baseType).Any()) return null;

        var generated = new List<string>();
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var current = baseType; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (!IsOverridableSymbol(member) ||
                    typeSymbol.GetMembers(member.Name).Any(existing =>
                        IsSameOverridableSignature(existing, member)))
                    continue;

                var key = member.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!generatedKeys.Add(key)) continue;
                switch (member)
                {
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                        generated.Add(GenerateOverrideMethodStub(method));
                        break;
                    case IPropertySymbol property:
                        generated.Add(GenerateOverridePropertyStub(property));
                        break;
                    case IEventSymbol @event:
                        generated.Add(GenerateOverrideEventStub(@event));
                        break;
                }
            }
        }

        return generated.Count == 0
            ? (null, null, "override可能な未実装メンバーがありません。")
            : (string.Join("\n\n", generated), "overrideメンバーを生成", null);
    }

    private static bool IsOverridableSymbol(ISymbol member)
        => member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary &&
                !method.IsStatic && method.DeclaredAccessibility != RoslynAccessibility.Private && !method.IsSealed &&
                (method.IsAbstract || method.IsVirtual || method.IsOverride) &&
                method.DeclaredAccessibility is RoslynAccessibility.Public or RoslynAccessibility.Protected or
                    RoslynAccessibility.ProtectedOrInternal,
            IPropertySymbol property => !property.IsStatic && !property.IsWriteOnly && !property.IsSealed &&
                (property.IsAbstract || property.IsVirtual || property.IsOverride) &&
                property.DeclaredAccessibility is RoslynAccessibility.Public or RoslynAccessibility.Protected or
                    RoslynAccessibility.ProtectedOrInternal,
            IEventSymbol @event => !@event.IsStatic && !@event.IsSealed &&
                (@event.IsAbstract || @event.IsVirtual || @event.IsOverride) &&
                @event.DeclaredAccessibility is RoslynAccessibility.Public or RoslynAccessibility.Protected or
                    RoslynAccessibility.ProtectedOrInternal,
            _ => false,
        };

    private static bool IsSameOverridableSignature(ISymbol existing, ISymbol candidate)
    {
        if (existing.Kind != candidate.Kind || !string.Equals(existing.Name, candidate.Name, StringComparison.Ordinal))
            return false;
        return existing switch
        {
            IMethodSymbol left when candidate is IMethodSymbol right =>
                left.Arity == right.Arity && left.Parameters.Length == right.Parameters.Length &&
                left.Parameters.Select(p => p.RefKind).SequenceEqual(right.Parameters.Select(p => p.RefKind)) &&
                left.Parameters.Select(p => p.Type).SequenceEqual(right.Parameters.Select(p => p.Type),
                    SymbolEqualityComparer.Default),
            IPropertySymbol left when candidate is IPropertySymbol right =>
                left.IsIndexer == right.IsIndexer &&
                left.Parameters.Select(p => p.RefKind).SequenceEqual(right.Parameters.Select(p => p.RefKind)) &&
                left.Parameters.Select(p => p.Type).SequenceEqual(right.Parameters.Select(p => p.Type),
                    SymbolEqualityComparer.Default),
            IEventSymbol => true,
            _ => false,
        };
    }

    private static string GenerateOverrideMethodStub(IMethodSymbol method)
    {
        var accessibility = SymbolAccessibility(method.DeclaredAccessibility);
        var returnRef = method.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.RefReadOnly => "ref readonly ",
            _ => "",
        };
        var typeParameters = method.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", method.TypeParameters.Select(p => EscapeIdentifier(p.Name))) + ">";
        var constraints = string.Join(" ", method.TypeParameters
            .Select(FormatTypeParameterConstraints)
            .Where(value => value.Length > 0));
        var returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var call = $"base.{EscapeIdentifier(method.Name)}{typeParameters}(\n            {string.Join(", ", method.Parameters.Select(FormatParameterArgument))})";
        var body = method.IsAbstract
            ? "throw new global::System.NotImplementedException();"
            : method.RefKind is RefKind.Ref or RefKind.RefReadOnly
                ? $"return ref {call};"
                : method.ReturnsVoid
                    ? $"{call};"
                    : $"return {call};";
        return $"{accessibility} override {returnRef}{returnType} {EscapeIdentifier(method.Name)}{typeParameters}({string.Join(", ", method.Parameters.Select(FormatParameter))}){constraints}\n{{\n    {body}\n}}";
    }

    private static string GenerateOverridePropertyStub(IPropertySymbol property)
    {
        var accessibility = SymbolAccessibility(property.DeclaredAccessibility);
        var type = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var name = property.IsIndexer
            ? "this[" + string.Join(", ", property.Parameters.Select(FormatParameter)) + "]"
            : EscapeIdentifier(property.Name);
        var accessors = new List<string>();
        if (property.GetMethod is not null)
            accessors.Add(property.GetMethod.IsAbstract
                ? "get;"
                : $"get => {BasePropertyAccess(property)};");
        if (property.SetMethod is not null)
            accessors.Add(property.SetMethod.IsAbstract
                ? property.SetMethod.IsInitOnly
                    ? "init;"
                    : "set;"
                : property.SetMethod.IsInitOnly
                    ? $"init => {BasePropertyAccess(property)} = value;"
                    : $"set => {BasePropertyAccess(property)} = value;");
        return $"{accessibility} override {type} {name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string BasePropertyAccess(IPropertySymbol property)
        => property.IsIndexer
            ? "base[" + string.Join(", ", property.Parameters.Select(FormatParameterArgument)) + "]"
            : "base." + EscapeIdentifier(property.Name);

    private static string GenerateOverrideEventStub(IEventSymbol @event)
    {
        var accessibility = SymbolAccessibility(@event.DeclaredAccessibility);
        var type = @event.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var access = @event.IsAbstract
            ? ("add => throw new global::System.NotImplementedException();", "remove => throw new global::System.NotImplementedException();")
            : ($"add => base.{EscapeIdentifier(@event.Name)} += value;",
                $"remove => base.{EscapeIdentifier(@event.Name)} -= value;");
        return $"{accessibility} override event {type} {EscapeIdentifier(@event.Name)}\n{{\n    {access.Item1}\n    {access.Item2}\n}}";
    }

    private static string SymbolAccessibility(RoslynAccessibility accessibility)
        => accessibility switch
        {
            RoslynAccessibility.Protected => "protected",
            RoslynAccessibility.ProtectedOrInternal => "protected internal",
            RoslynAccessibility.ProtectedAndInternal => "private protected",
            RoslynAccessibility.Internal => "internal",
            _ => "public",
        };

    private static string GenerateMethodStub(MethodDeclarationSyntax method, string modifier)
    {
        var hasRef = method.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword));
        var returnRef = hasRef && method.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))
            ? "ref readonly "
            : hasRef ? "ref " : "";
        var generic = method.TypeParameterList?.ToString() ?? "";
        var constraints = method.ConstraintClauses.Count == 0
            ? ""
            : " " + string.Join(" ", method.ConstraintClauses.Select(c => c.ToString()));
        return $"{modifier} {returnRef}{method.ReturnType} {method.Identifier}{generic}({FormatParameters(method.ParameterList)}){constraints}\n{{\n    throw new global::System.NotImplementedException();\n}}";
    }

    private static string GeneratePropertyStub(PropertyDeclarationSyntax property, string modifier)
    {
        var accessors = property.AccessorList?.Accessors
            .Where(a => a.Kind() is SyntaxKind.GetAccessorDeclaration or SyntaxKind.SetAccessorDeclaration
                or SyntaxKind.InitAccessorDeclaration)
            .Select(a => a.Kind() == SyntaxKind.GetAccessorDeclaration ? "get;" :
                a.Kind() == SyntaxKind.InitAccessorDeclaration ? "init;" : "set;")
            .ToList() ?? [];
        if (accessors.Count == 0) accessors.Add("get;");
        return $"{modifier} {property.Type} {property.Identifier} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateEventStub(EventDeclarationSyntax @event, string modifier)
        => GenerateEventStub(@event.Type, @event.Identifier.ValueText, modifier);

    private static string GenerateOverrideMethodStub(MethodDeclarationSyntax method, string modifier)
    {
        var hasRef = method.Modifiers.Any(token => token.IsKind(SyntaxKind.RefKeyword));
        var returnRef = hasRef && method.Modifiers.Any(token => token.IsKind(SyntaxKind.ReadOnlyKeyword))
            ? "ref readonly "
            : hasRef ? "ref " : "";
        var generic = method.TypeParameterList?.ToString() ?? "";
        var constraints = method.ConstraintClauses.Count == 0
            ? ""
            : " " + string.Join(" ", method.ConstraintClauses.Select(clause => clause.ToString()));
        var call = $"base.{method.Identifier}{generic}({string.Join(", ",
            method.ParameterList.Parameters.Select(FormatParameterArgument))})";
        var body = method.Modifiers.Any(token => token.IsKind(SyntaxKind.AbstractKeyword))
            ? "throw new global::System.NotImplementedException();"
            : hasRef
                ? $"return ref {call};"
                : IsVoid(method.ReturnType)
                    ? $"{call};"
                    : $"return {call};";
        return $"{modifier} {returnRef}{method.ReturnType} {method.Identifier}{generic}({FormatParameters(method.ParameterList)}){constraints}\n{{\n    {body}\n}}";
    }

    private static string GenerateOverridePropertyStub(PropertyDeclarationSyntax property, string modifier)
    {
        var name = property.Identifier.ValueText;
        var receiver = "base." + property.Identifier.ValueText;
        var isAbstract = property.Modifiers.Any(token => token.IsKind(SyntaxKind.AbstractKeyword));
        var accessors = property.AccessorList?.Accessors
            .Where(accessor => accessor.Kind() is SyntaxKind.GetAccessorDeclaration
                or SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration)
            .Select(accessor => accessor.Kind() == SyntaxKind.GetAccessorDeclaration
                ? isAbstract ? "get;" : $"get => {receiver};"
                : accessor.Kind() == SyntaxKind.InitAccessorDeclaration
                    ? isAbstract ? "init;" : $"init => {receiver} = value;"
                    : isAbstract ? "set;" : $"set => {receiver} = value;")
            .ToList() ?? [];
        if (accessors.Count == 0 && property.ExpressionBody is not null)
            accessors.Add(isAbstract
                ? "get;"
                : $"get => {receiver};");
        if (accessors.Count == 0) accessors.Add("get;");
        return $"{modifier} {property.Type} {name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GenerateOverrideEventStub(
        EventDeclarationSyntax @event, string modifier)
        => GenerateOverrideEventStub(@event.Type, @event.Identifier.ValueText, modifier,
            @event.Modifiers.Any(token => token.IsKind(SyntaxKind.AbstractKeyword)));

    private static string GenerateOverrideEventStub(
        TypeSyntax type, string name, string modifier, bool isAbstract)
    {
        var add = isAbstract
            ? "add => throw new global::System.NotImplementedException();"
            : $"add => base.{EscapeIdentifier(name)} += value;";
        var remove = isAbstract
            ? "remove => throw new global::System.NotImplementedException();"
            : $"remove => base.{EscapeIdentifier(name)} -= value;";
        return $"{modifier} event {type} {EscapeIdentifier(name)}\n{{\n    {add}\n    {remove}\n}}";
    }

    private static string GenerateEventStub(TypeSyntax type, string name, string modifier)
        => $"{modifier} event {type} {name}\n{{\n    add => throw new global::System.NotImplementedException();\n    remove => throw new global::System.NotImplementedException();\n}}";

    private static string FormatParameters(ParameterListSyntax parameters)
        => string.Join(", ", parameters.Parameters.Select(parameter =>
        {
            var modifiers = string.Join(" ", parameter.Modifiers.Select(m => m.Text));
            var prefix = modifiers.Length == 0 ? "" : modifiers + " ";
            var type = parameter.Type?.ToString() ?? "object";
            return $"{prefix}{type} {parameter.Identifier.ValueText}";
        }));

    private static string BaseTypeName(TypeSyntax type)
    {
        var text = type.ToString().TrimEnd('?');
        var lastDot = text.LastIndexOf('.');
        if (lastDot >= 0) text = text[(lastDot + 1)..];
        var generic = text.IndexOf('<');
        return generic >= 0 ? text[..generic] : text;
    }

    private static bool IsImplementableContractMember(MemberDeclarationSyntax member)
    {
        if (member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) ||
                                      m.IsKind(SyntaxKind.PrivateKeyword)))
            return false;

        // Default interface members already have an implementation in the contract and
        // must not be copied into the implementing type. A syntax fallback can still
        // distinguish declaration-only members from members with a body.
        return member switch
        {
            MethodDeclarationSyntax method => method.Body is null && method.ExpressionBody is null,
            PropertyDeclarationSyntax property => property.ExpressionBody is null &&
                property.AccessorList?.Accessors.All(accessor =>
                    accessor.Body is null && accessor.ExpressionBody is null) == true,
            EventDeclarationSyntax @event => @event.AccessorList?.Accessors.All(accessor =>
                    accessor.Body is null && accessor.ExpressionBody is null) == true,
            EventFieldDeclarationSyntax => true,
            _ => false,
        };
    }

    private static bool IsOverridableBaseMember(MemberDeclarationSyntax member)
        => (member is MethodDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax
            or EventFieldDeclarationSyntax)
            && member.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)
                || m.IsKind(SyntaxKind.VirtualKeyword) || m.IsKind(SyntaxKind.OverrideKeyword))
            && !member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)
                || m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.SealedKeyword));

    private static bool HasMethod(TypeDeclarationSyntax type, MethodDeclarationSyntax candidate)
        => type.Members.OfType<MethodDeclarationSyntax>().Any(existing =>
            string.Equals(existing.Identifier.ValueText, candidate.Identifier.ValueText, StringComparison.Ordinal)
            && existing.ParameterList.Parameters.Count == candidate.ParameterList.Parameters.Count
            && string.Equals(ParameterShape(existing.ParameterList), ParameterShape(candidate.ParameterList), StringComparison.Ordinal));

    private static bool HasProperty(TypeDeclarationSyntax type, string name)
        => type.Members.OfType<PropertyDeclarationSyntax>().Any(p =>
            string.Equals(p.Identifier.ValueText, name, StringComparison.Ordinal));

    private static bool HasEvent(TypeDeclarationSyntax type, string name)
        => type.Members.OfType<EventDeclarationSyntax>().Any(e =>
            string.Equals(e.Identifier.ValueText, name, StringComparison.Ordinal))
            || type.Members.OfType<EventFieldDeclarationSyntax>().SelectMany(e => e.Declaration.Variables)
                .Any(v => string.Equals(v.Identifier.ValueText, name, StringComparison.Ordinal));

    private static string MethodKey(string name, ParameterListSyntax parameters)
        => "method:" + name + "/" + ParameterShape(parameters);

    private static string ParameterShape(ParameterListSyntax parameters)
        => string.Join(",", parameters.Parameters.Select(parameter =>
            $"{ParameterModifier(parameter)}:{parameter.Type?.ToString() ?? "object"}"));

    private static IReadOnlyList<SyntaxNode> ParseWorkspaceRoots(
        string activeFilePath,
        SyntaxNode activeRoot,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        CSharpParseOptions parseOptions,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions)
    {
        if (workspaceTexts is null || workspaceTexts.Count == 0) return [activeRoot];

        var roots = new List<SyntaxNode> { activeRoot };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(activeFilePath),
        };
        foreach (var (path, source) in workspaceTexts)
        {
            if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (!seen.Add(Path.GetFullPath(path))) continue;
                var sourceParseOptions = workspaceParseOptions is not null &&
                    workspaceParseOptions.TryGetValue(path, out var configured)
                    ? configured : parseOptions;
                roots.Add(CSharpSyntaxTree.ParseText(source, sourceParseOptions).GetRoot());
            }
            catch (ArgumentException)
            {
                // 読み取り対象の1ファイルが不正でも、他のソースから生成候補を探し続ける。
            }
        }
        return roots;
    }

    private static IEnumerable<T> FindRelatedTypes<T>(
        TypeDeclarationSyntax target,
        IEnumerable<SyntaxNode> roots,
        string name)
        where T : TypeDeclarationSyntax
    {
        var candidates = roots.SelectMany(root => root.DescendantNodes().OfType<T>())
            .Where(candidate => string.Equals(candidate.Identifier.ValueText, name, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count <= 1) return candidates;

        var targetNamespace = NamespaceName(target);
        var sameNamespace = candidates.Where(candidate =>
            string.Equals(NamespaceName(candidate), targetNamespace, StringComparison.Ordinal)).ToList();
        return sameNamespace.Count > 0 ? sameNamespace : candidates;
    }

    /// <summary>interfaceの継承階層を構文だけで辿る。Roslynの意味モデルがないfallbackでも、
    /// 子interfaceだけを実装した型へ親interfaceのメンバーを欠落させない。</summary>
    private static IEnumerable<InterfaceDeclarationSyntax> FindInterfaceHierarchy(
        TypeDeclarationSyntax target,
        IReadOnlyList<SyntaxNode> roots,
        IEnumerable<string> initialNames)
    {
        var pending = new Queue<string>(initialNames.Where(name => name.Length > 0));
        var visitedNames = new HashSet<string>(StringComparer.Ordinal);
        var visitedContracts = new HashSet<InterfaceDeclarationSyntax>();
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!visitedNames.Add(name)) continue;
            foreach (var contract in FindRelatedTypes<InterfaceDeclarationSyntax>(target, roots, name))
            {
                if (!visitedContracts.Add(contract)) continue;
                yield return contract;
                foreach (var baseType in contract.BaseList?.Types ?? [])
                    pending.Enqueue(BaseTypeName(baseType.Type));
            }
        }
    }

    /// <summary>意味モデルで解決したinterfaceだけを、元ソースの宣言へ戻す。
    /// 名前だけの検索では、別namespaceに同名interfaceがあると両方を生成してしまう。</summary>
    private static IEnumerable<InterfaceDeclarationSyntax> FindSemanticInterfaceHierarchy(
        TypeDeclarationSyntax target, SemanticModel semanticModel)
    {
        var semanticTarget = FindEquivalentType(target, semanticModel);
        if (semanticTarget is null) yield break;
        if (semanticModel.GetDeclaredSymbol(semanticTarget) is not INamedTypeSymbol symbol) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in symbol.AllInterfaces)
        {
            var key = contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!seen.Add(key)) continue;
            foreach (var syntax in SourceDeclarations<InterfaceDeclarationSyntax>(contract))
                yield return syntax;
        }
    }

    /// <summary>フィールドの静的型からinterface階層を解決する。aliasや完全修飾名を
    /// BaseTypeNameへ潰さず、Roslynのシンボルidentityを保つ。</summary>
    private static IEnumerable<InterfaceDeclarationSyntax> FindSemanticFieldInterfaceHierarchy(
        FieldDeclarationSyntax field, SemanticModel semanticModel)
    {
        var semanticField = semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == field.SpanStart);
        var semanticVariable = semanticField?.Declaration.Variables.FirstOrDefault(variable =>
            string.Equals(variable.Identifier.ValueText,
                field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
                StringComparison.Ordinal));
        if (semanticVariable is null || semanticModel.GetDeclaredSymbol(semanticVariable) is not IFieldSymbol fieldSymbol)
            yield break;
        if (fieldSymbol.Type is not INamedTypeSymbol fieldType) yield break;

        var contracts = new[] { fieldType }
            .Concat(fieldType.AllInterfaces)
            .Where(contract => contract.TypeKind == TypeKind.Interface)
            .GroupBy(contract => contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparer.Ordinal)
            .Select(group => group.First());
        foreach (var contract in contracts)
            foreach (var syntax in SourceDeclarations<InterfaceDeclarationSyntax>(contract))
                yield return syntax;
    }

    /// <summary>意味モデルで解決した直接の基底class宣言を取得する。</summary>
    private static IEnumerable<ClassDeclarationSyntax> FindSemanticBaseDeclarations(
        TypeDeclarationSyntax target, SemanticModel semanticModel)
    {
        var semanticTarget = FindEquivalentType(target, semanticModel);
        if (semanticTarget is null) yield break;
        if (semanticModel.GetDeclaredSymbol(semanticTarget) is not INamedTypeSymbol symbol ||
            symbol.BaseType is not { } baseType)
            yield break;
        foreach (var syntax in SourceDeclarations<ClassDeclarationSyntax>(baseType))
            yield return syntax;
    }

    private static TypeDeclarationSyntax? FindEquivalentType(
        TypeDeclarationSyntax target, SemanticModel semanticModel)
        => semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == target.SpanStart &&
                candidate.RawKind == target.RawKind &&
                string.Equals(candidate.Identifier.ValueText, target.Identifier.ValueText,
                    StringComparison.Ordinal));

    private static IEnumerable<TSyntax> SourceDeclarations<TSyntax>(INamedTypeSymbol symbol)
        where TSyntax : SyntaxNode
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            SyntaxNode? syntax;
            try { syntax = reference.GetSyntax(); }
            catch (InvalidOperationException) { continue; }
            if (syntax is TSyntax typed) yield return typed;
        }
    }

    private static string NamespaceName(TypeDeclarationSyntax type)
        => string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(namespaceNode => namespaceNode.Name.ToString()));

    private static string ParameterModifier(ParameterSyntax parameter)
        => parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)) ? "ref"
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)) ? "out"
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)) ? "in"
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)) ? "params"
            : "";

    private static string AccessModifier(SyntaxTokenList modifiers)
        => modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword))
            ? modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)) ? "protected internal" : "protected"
            : modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)) ? "internal" : "public";

    private static IEnumerable<FieldInfo> InstanceFields(TypeDeclarationSyntax type)
    {
        foreach (var declaration in type.Members.OfType<FieldDeclarationSyntax>())
        {
            if (declaration.Modifiers.Any(m =>
                    m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
                continue;
            foreach (var variable in declaration.Declaration.Variables)
                yield return new FieldInfo(
                    declaration.Declaration.Type, variable.Identifier, declaration.Modifiers, variable);
        }
    }

    private static LspWorkspaceEdit? InsertBeforeCloseBrace(
        string filePath,
        SourceText source,
        TypeDeclarationSyntax type,
        string generated)
    {
        var close = type.CloseBraceToken;
        if (close.IsMissing) return null;
        var line = source.Lines.GetLineFromPosition(close.SpanStart);
        var closeIndent = source.ToString(TextSpan.FromBounds(line.Start, close.SpanStart));
        if (closeIndent.Any(c => !char.IsWhiteSpace(c))) return null;

        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var memberIndent = FindMemberIndent(source, type, closeIndent);
        // 生成本文の内部インデントは4空白基準。先頭行だけ型のメンバー字下げを足し、
        // それ以降はブロック階層を1段ずつ足す。
        var indented = IndentGeneratedMember(generated, memberIndent, newline);
        var newText = indented + newline;
        var position = new LspPosition(line.LineNumber, 0);
        var range = new LspRange(position, position);
        return new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [LspUri.FromPath(Path.GetFullPath(filePath))] = [new LspTextEdit(range, newText)],
            });
    }

    private static string FindMemberIndent(SourceText source, TypeDeclarationSyntax type, string closeIndent)
    {
        var member = type.Members.FirstOrDefault();
        if (member is null) return closeIndent + "    ";
        var line = source.Lines.GetLineFromPosition(member.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, member.SpanStart));
        return prefix.All(char.IsWhiteSpace) ? prefix : closeIndent + "    ";
    }

    private static string IndentGeneratedMember(string generated, string memberIndent, string newline)
    {
        var lines = generated.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new List<string>(lines.Length);
        var depth = 0;
        foreach (var line in lines)
        {
            result.Add(memberIndent + new string(' ', depth * 4) + line);
            if (line.Contains('{', StringComparison.Ordinal)) depth++;
            if (line.Contains('}', StringComparison.Ordinal)) depth = Math.Max(0, depth - 1);
        }
        return string.Join(newline, result);
    }

    private static string MakeUniqueParameterName(
        string fieldName, HashSet<string> used, CSharpNamingStyle? style = null)
    {
        var name = fieldName.TrimStart('_');
        if (name.StartsWith("m_", StringComparison.Ordinal)) name = name[2..];
        if (name.Length == 0) name = "value";
        name = ApplyNamingCapitalization(name, style?.Capitalization ?? "camel_case");
        if (!string.IsNullOrEmpty(style?.RequiredPrefix))
            name = style.RequiredPrefix + name;
        if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None) name = "@" + name;
        var baseName = name;
        for (var i = 2; !used.Add(name); i++) name = baseName + i;
        return name;
    }

    private static string ToPropertyName(string fieldName, CSharpNamingStyle? style = null)
    {
        var name = fieldName.TrimStart('_');
        if (name.StartsWith("m_", StringComparison.Ordinal)) name = name[2..];
        if (name.Length == 0) return "Value";
        return ApplyNamingCapitalization(name, style?.Capitalization ?? "pascal_case");
    }

    private static string ApplyNamingCapitalization(string name, string capitalization)
    {
        if (name.Length == 0) return name;
        return capitalization.Trim().ToLowerInvariant() switch
        {
            "camel_case" or "first_word_lower" => char.ToLowerInvariant(name[0]) + name[1..],
            "pascal_case" or "first_word_upper" => char.ToUpperInvariant(name[0]) + name[1..],
            "all_upper" => name.ToUpperInvariant(),
            "all_lower" => name.ToLowerInvariant(),
            _ => name,
        };
    }

    private static bool IsClassOrStruct(TypeDeclarationSyntax type)
        => type is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax;

    private static bool IsReferenceLike(TypeSyntax type)
    {
        if (type is NullableTypeSyntax or ArrayTypeSyntax or FunctionPointerTypeSyntax)
            return true;
        if (type is not PredefinedTypeSyntax predefined) return true;
        return predefined.Keyword.Kind() is not (
            SyntaxKind.BoolKeyword or SyntaxKind.ByteKeyword or SyntaxKind.SByteKeyword or
            SyntaxKind.ShortKeyword or SyntaxKind.UShortKeyword or SyntaxKind.IntKeyword or
            SyntaxKind.UIntKeyword or SyntaxKind.LongKeyword or SyntaxKind.ULongKeyword or
            SyntaxKind.FloatKeyword or
            SyntaxKind.DoubleKeyword or SyntaxKind.DecimalKeyword or SyntaxKind.CharKeyword);
    }

    private static bool IsReferenceLike(IParameterSymbol? parameter)
    {
        if (parameter is null) return false;
        if (parameter.Type.IsReferenceType) return true;
        return parameter.Type is INamedTypeSymbol named &&
               named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    private static BaseMethodDeclarationSyntax? FindEquivalentMethod(
        BaseMethodDeclarationSyntax target, SemanticModel semanticModel)
        => semanticModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.SpanStart == target.SpanStart &&
                candidate.RawKind == target.RawKind);

    private static bool LooksDisposable(TypeSyntax type)
    {
        var name = BaseTypeName(type);
        return name is "IDisposable" or "Stream" or "FileStream" or "MemoryStream"
            or "TextReader" or "TextWriter" or "DbConnection" or "DbCommand"
            or "CancellationTokenSource" or "Timer";
    }

    private static int ClampToLine(SourceText source, int line, int character)
    {
        var textLine = source.Lines[line];
        return textLine.Start + Math.Clamp(character, 0, textLine.Span.Length);
    }

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);

    private sealed record FieldInfo(
        TypeSyntax Type,
        SyntaxToken Identifier,
        SyntaxTokenList Modifiers,
        VariableDeclaratorSyntax? Declarator,
        IFieldSymbol? SemanticSymbol = null);

    private sealed record ConstructorMember(string Name, string Type);

    private sealed record PropertyGenerationField(string Type, string Name, bool ReadOnly);

    private sealed record ValueMember(string Name, string Type, string Expression, bool IsField);
}
