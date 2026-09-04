using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>Dispose ／ 非同期 Dispose パターンの生成。IDisposable フィールドの検出と、
/// 基底型が既にパターンを持つ場合の override 版生成（安全に拡張できないときは中止）を担う。</summary>
internal static class CSharpDisposeGenerator
{
    internal static CSharpCodeGenerationResult GenerateDisposePattern(
        string filePath, SourceText source, TypeDeclarationSyntax type,
        SemanticModel? semanticModel)
    {
        if (type is not ClassDeclarationSyntax)
            return CSharpCodeGenerationResult.Failed("Disposeパターンはクラスでのみ生成できます。");

        var existing = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => string.Equals(m.Identifier.ValueText, "Dispose", StringComparison.Ordinal))
            .ToList();
        if (existing.Any(m => m.ParameterList.Parameters.Count == 0)
            || existing.Any(m => m.ParameterList.Parameters.Count == 1
                && string.Equals(m.ParameterList.Parameters[0].Type?.ToString(), "bool", StringComparison.Ordinal)))
            return CSharpCodeGenerationResult.Failed("Disposeメソッドが既にあります。");

        var disposableContract = semanticModel?.Compilation.GetTypeByMetadataName("System.IDisposable");
        var allFields = GenerationSyntax.InstanceFields(type).ToList();
        var semanticTypeSymbol = semanticModel is not null &&
            GenerationSyntax.FindEquivalentType(type, semanticModel) is { } semanticType
            ? semanticModel.GetDeclaredSymbol(semanticType) as INamedTypeSymbol
            : null;
        if (semanticTypeSymbol is not null)
        {
            allFields.AddRange(GenerationSyntax.GetSemanticPartialFields(semanticTypeSymbol, semanticModel!));
            if (semanticTypeSymbol.GetMembers("Dispose").OfType<IMethodSymbol>().Any(method =>
                    method.Parameters.Length == 0 ||
                    (method.Parameters.Length == 1 &&
                        method.Parameters[0].Type.SpecialType == SpecialType.System_Boolean)))
                return CSharpCodeGenerationResult.Failed("Disposeメソッドが既にあります。");
        }
        var disposableFields = allFields
            .Where(field => IsDisposableField(field, semanticModel, disposableContract))
            .ToList();
        var hasDisposableContract = HasDisposableContract(type, semanticModel, disposableContract);
        if (!hasDisposableContract && disposableFields.Count == 0)
            return CSharpCodeGenerationResult.Failed("IDisposableフィールドまたはIDisposable実装が見つかりません。");

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
                return CSharpCodeGenerationResult.Failed("基底型のIDisposableパターンを安全に拡張できません。");
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
        var memberEdit = GenerationSyntax.InsertBeforeCloseBrace(filePath, source, type, generated);
        if (memberEdit is null) return CSharpCodeGenerationResult.Failed("Disposeパターンを型の末尾へ挿入できませんでした。");

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
        GeneratedFieldInfo field,
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
            GenerationSyntax.FindEquivalentField(field, semanticModel) is { } semanticField &&
            semanticModel.GetDeclaredSymbol(semanticField) is IFieldSymbol symbol)
        {
            var fieldType = symbol.Type;
            return SymbolEqualityComparer.Default.Equals(fieldType, disposableContract) ||
                   fieldType.AllInterfaces.Any(@interface =>
                       SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition, disposableContract));
        }

        return LooksDisposable(field.Type);
    }

    internal static CSharpCodeGenerationResult GenerateAsyncDisposePattern(
        string filePath, SourceText source, TypeDeclarationSyntax type,
        SemanticModel? semanticModel)
    {
        if (type is not ClassDeclarationSyntax)
            return CSharpCodeGenerationResult.Failed("非同期Disposeパターンはクラスでのみ生成できます。");
        if (semanticModel is null)
            return CSharpCodeGenerationResult.Failed("非同期Disposeパターンは意味モデルが必要です。");

        var asyncDisposableContract = semanticModel.Compilation
            .GetTypeByMetadataName("System.IAsyncDisposable");
        if (asyncDisposableContract is null)
            return CSharpCodeGenerationResult.Failed("System.IAsyncDisposableを解決できません。");

        var existing = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => string.Equals(m.Identifier.ValueText, "DisposeAsync", StringComparison.Ordinal))
            .ToList();

        var semanticType = GenerationSyntax.FindEquivalentType(type, semanticModel);
        if (semanticType is null || semanticModel.GetDeclaredSymbol(semanticType) is not INamedTypeSymbol typeSymbol)
            return CSharpCodeGenerationResult.Failed("対象クラスの意味モデルを解決できません。");
        if (existing.Any(m => m.ParameterList.Parameters.Count == 0) ||
            typeSymbol.GetMembers("DisposeAsync").OfType<IMethodSymbol>()
                .Any(method => method.Parameters.Length == 0))
            return CSharpCodeGenerationResult.Failed("DisposeAsyncメソッドが既にあります。");

        var inheritedAsyncDisposeCore = typeSymbol.BaseType?.AllInterfaces.Any(@interface =>
                SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                    asyncDisposableContract)) == true
            ? FindOverridableDisposeAsyncCore(typeSymbol,
                semanticModel.Compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask"))
            : null;
        if (typeSymbol.BaseType?.AllInterfaces.Any(@interface =>
                SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition,
                    asyncDisposableContract)) == true && inheritedAsyncDisposeCore is null)
            return CSharpCodeGenerationResult.Failed("基底型のIAsyncDisposableパターンを安全に拡張できません。");

        var allFields = GenerationSyntax.InstanceFields(type).ToList();
        allFields.AddRange(GenerationSyntax.GetSemanticPartialFields(typeSymbol, semanticModel));
        var asyncDisposableFields = allFields
            .Where(field => IsAsyncDisposableField(field, semanticModel, asyncDisposableContract))
            .ToList();
        var hasAsyncDisposableContract = HasAsyncDisposableContract(typeSymbol, asyncDisposableContract);
        if (!hasAsyncDisposableContract && asyncDisposableFields.Count == 0)
            return CSharpCodeGenerationResult.Failed("IAsyncDisposableフィールドまたはIAsyncDisposable実装が見つかりません。");
        if (inheritedAsyncDisposeCore is not null && asyncDisposableFields.Count == 0)
            return CSharpCodeGenerationResult.Failed("追加で解放するIAsyncDisposableフィールドが見つかりません。");

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
        var memberEdit = GenerationSyntax.InsertBeforeCloseBrace(filePath, source, type, generated);
        if (memberEdit is null) return CSharpCodeGenerationResult.Failed("非同期Disposeパターンを型の末尾へ挿入できませんでした。");

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
        GeneratedFieldInfo field,
        SemanticModel semanticModel,
        INamedTypeSymbol asyncDisposableContract)
    {
        var symbol = field.SemanticSymbol;
        if (symbol is null && GenerationSyntax.FindEquivalentField(field, semanticModel) is { } semanticField)
            symbol = semanticModel.GetDeclaredSymbol(semanticField) as IFieldSymbol;
        return symbol is not null && ImplementsContract(symbol.Type, asyncDisposableContract);
    }

    private static string AsyncDisposeField(GeneratedFieldInfo field, SemanticModel semanticModel)
    {
        var symbol = field.SemanticSymbol;
        if (symbol is null && GenerationSyntax.FindEquivalentField(field, semanticModel) is { } semanticField)
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
        GeneratedFieldInfo field,
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
            GenerationSyntax.FindEquivalentField(field, semanticModel) is { } semanticField &&
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
            GenerationSyntax.FindEquivalentType(type, semanticModel) is { } semanticType &&
            semanticModel.GetDeclaredSymbol(semanticType) is INamedTypeSymbol symbol)
        {
            return symbol.AllInterfaces.Any(@interface =>
                SymbolEqualityComparer.Default.Equals(@interface.OriginalDefinition, disposableContract));
        }

        return type.BaseList?.Types
            .Select(baseType => GenerationSyntax.BaseTypeName(baseType.Type))
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

    private static bool LooksDisposable(TypeSyntax type)
    {
        var name = GenerationSyntax.BaseTypeName(type);
        return name is "IDisposable" or "Stream" or "FileStream" or "MemoryStream"
            or "TextReader" or "TextWriter" or "DbConnection" or "DbCommand"
            or "CancellationTokenSource" or "Timer";
    }
}
