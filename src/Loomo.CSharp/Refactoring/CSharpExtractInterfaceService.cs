using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>公開インスタンスメンバーからinterfaceを抽出し、元クラスへ実装を追加する。
/// generic型引数を扱う場合はRoslynの意味モデルで型と制約を確認し、interfaceファイルの作成と
/// 元ファイルの変更を1つのWorkspaceEditにまとめる。</summary>
public static class CSharpExtractInterfaceService
{
    public static CSharpCodeGenerationResult Extract(
        string filePath,
        string sourceText,
        LspRange selection,
        string interfaceName,
        string destinationFilePath)
        => ExtractCore(filePath, sourceText, selection, interfaceName,
            destinationFilePath, semanticCompilation: null);

    internal static CSharpCodeGenerationResult Extract(
        string filePath,
        string sourceText,
        LspRange selection,
        string interfaceName,
        string destinationFilePath,
        CSharpCompilation semanticCompilation)
        => ExtractCore(filePath, sourceText, selection, interfaceName,
            destinationFilePath, semanticCompilation);

    private static CSharpCodeGenerationResult ExtractCore(
        string filePath,
        string sourceText,
        LspRange selection,
        string interfaceName,
        string destinationFilePath,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみinterface抽出を実行できます。");
        if (!string.Equals(Path.GetExtension(destinationFilePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("interfaceの移動先は .cs ファイルにしてください。");

        var sourcePath = Path.GetFullPath(filePath);
        var destinationPath = Path.GetFullPath(destinationFilePath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            return Failed("interfaceの移動先は元ファイルと別にしてください。");
        if (File.Exists(destinationPath))
            return Failed("interfaceの移動先ファイルが既に存在します。");
        if (Path.GetDirectoryName(destinationPath) is not { } directory
            || !Directory.Exists(directory))
            return Failed("interfaceの移動先フォルダーが存在しません。");

        var name = interfaceName.Trim();
        if (!SyntaxFacts.IsValidIdentifier(name)
            || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
            return Failed("interface名がC#識別子として不正です。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.Span == selectedSpan);
        if (type is null)
            return Failed("interfaceを抽出するクラス名全体を選択してください。");
        if (type.Parent is not CompilationUnitSyntax
            and not BaseNamespaceDeclarationSyntax)
            return Failed("入れ子クラスからのinterface抽出は対象外です。");
        if (type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.FileKeyword)))
            return Failed("file-local classから別ファイルへinterfaceを抽出できません。");
        var isPartial = type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));
        if (isPartial && semanticCompilation is null)
            return Failed("partialクラスは意味モデルで全パーツを確認してから抽出してください。");
        if (type.TypeParameterList is not null && semanticCompilation is null)
            return Failed("genericクラスは意味モデルで型引数を確認してから抽出してください。");
        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, sourcePath)
            : null;
        if (semanticCompilation is not null && semanticModel is null)
            return Failed("対象クラスをC#の意味モデルから解決できません。");
        var semanticType = semanticModel is not null
            ? FindEquivalent(type, semanticModel)
            : null;
        var semanticTypeSymbol = semanticType is not null
            ? semanticModel!.GetDeclaredSymbol(semanticType) as INamedTypeSymbol
            : null;
        if (semanticCompilation is not null && semanticTypeSymbol is null)
            return Failed("対象クラスのsymbolをC#の意味モデルから解決できません。");
        if (semanticCompilation is not null && semanticTypeSymbol is not null && semanticCompilation
                .GetSymbolsWithName(name, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .Any(symbol => symbol.TypeKind == TypeKind.Interface
                    && SymbolEqualityComparer.Default.Equals(
                        symbol.ContainingNamespace, semanticTypeSymbol.ContainingNamespace)))
            return Failed("同名のinterfaceがワークスペースに既にあります。");
        if (type.BaseList?.Types.Any(baseType =>
                string.Equals(BaseTypeName(baseType.Type), name, StringComparison.Ordinal)) == true)
            return Failed("クラスはそのinterfaceを既に実装しています。");
        if (root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Any(existing =>
                string.Equals(existing.Identifier.ValueText, name, StringComparison.Ordinal)))
            return Failed("同名のinterfaceが既に同じファイルにあります。");

        var partialDeclarations = semanticTypeSymbol is not null && isPartial
            ? semanticTypeSymbol.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax())
                .OfType<ClassDeclarationSyntax>()
                .ToArray()
            : [type];
        if (partialDeclarations.Length == 0)
            return Failed("partialクラスの宣言を意味モデルから取得できません。");

        if (semanticTypeSymbol is not null && isPartial &&
            HasConflictingUsingAliases(partialDeclarations))
            return Failed("partialクラスのファイル間で同名のusing aliasが異なるため、安全にinterfaceを抽出できません。");

        var members = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var membersToInspect = partialDeclarations.SelectMany(declaration => declaration.Members);
        foreach (var member in membersToInspect)
        {
            if (!(semanticModel is not null
                    ? IsSemanticPublicInstance(member, semanticTypeSymbol!)
                    : IsPublicInstance(member)))
                continue;
            switch (member)
            {
                case MethodDeclarationSyntax method
                    when keys.Add(MethodKey(method.Identifier.ValueText, method.ParameterList)):
                    members.Add(InterfaceMethod(method));
                    break;
                case PropertyDeclarationSyntax property
                    when (property.AccessorList is not null
                        && PublicAccessors(property.AccessorList).Count > 0
                        || property.ExpressionBody is not null)
                        && keys.Add("property:" + property.Identifier.ValueText):
                    members.Add(InterfaceProperty(property));
                    break;
                case IndexerDeclarationSyntax indexer
                    when indexer.AccessorList is { } indexerAccessors
                        && PublicAccessors(indexerAccessors).Count > 0
                        && keys.Add("indexer:" + indexer.ParameterList.ToString()):
                    members.Add(InterfaceIndexer(indexer));
                    break;
                case EventDeclarationSyntax @event
                    when keys.Add("event:" + @event.Identifier.ValueText):
                    members.Add($"event {@event.Type} {@event.Identifier};");
                    break;
                case EventFieldDeclarationSyntax eventFields:
                    foreach (var variable in eventFields.Declaration.Variables)
                    {
                        if (!keys.Add("event:" + variable.Identifier.ValueText)) continue;
                        members.Add($"event {eventFields.Declaration.Type} {variable.Identifier};");
                    }
                    break;
            }
        }

        if (members.Count == 0)
            return Failed("抽出できるpublicインスタンスメンバーがありません。");

        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var namespaceName = string.Join(".", type.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(namespaceNode => namespaceNode.Name.ToString()));
        var interfaceAccessibility = InterfaceAccessibility(type, semanticTypeSymbol);
        var importRoots = partialDeclarations
            .Select(declaration => declaration.SyntaxTree.GetRoot())
            .OfType<CompilationUnitSyntax>()
            .ToArray();
        if (importRoots.Length == 0) importRoots = [root];
        var interfaceText = BuildInterfaceText(importRoots, namespaceName, name,
            type.TypeParameterList, type.ConstraintClauses,
            interfaceAccessibility, members, newline);
        var sourceUri = LspUri.FromPath(sourcePath);
        var destinationUri = LspUri.FromPath(destinationPath);
        // base listが無いgeneric型では、識別子の直後へ挿入すると
        // `Type : IType<T><T>`になってしまう。型引数の後、constraintの前へ置く。
        var anchor = type.BaseList?.Span.End
            ?? type.TypeParameterList?.Span.End
            ?? type.Identifier.Span.End;
        var line = source.Lines.GetLineFromPosition(anchor);
        var column = anchor - line.Start;
        var interfaceTypeArguments = type.TypeParameterList is { Parameters.Count: > 0 }
            ? "<" + string.Join(", ", type.TypeParameterList.Parameters.Select(parameter =>
                parameter.Identifier.ValueText)) + ">"
            : "";
        var implementedInterface = name + interfaceTypeArguments;
        var classEditText = type.BaseList is null ? " : " + implementedInterface : ", " + implementedInterface;
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [sourceUri] =
                [
                    new LspTextEdit(
                        new LspRange(
                            new LspPosition(line.LineNumber, column),
                            new LspPosition(line.LineNumber, column)),
                        classEditText),
                ],
                [destinationUri] =
                [
                    new LspTextEdit(
                        new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        interfaceText),
                ],
            },
            FileOperations: [new LspFileOperation(LspFileOperationKind.Create, destinationUri)]);
        return new CSharpCodeGenerationResult(
            edit, $"クラス「{type.Identifier.ValueText}」からinterface「{name}」を抽出");
    }

    private static string BuildInterfaceText(
        IReadOnlyList<CompilationUnitSyntax> importRoots,
        string namespaceName,
        string name,
        TypeParameterListSyntax? typeParameters,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraints,
        string accessibility,
        IReadOnlyList<string> members,
        string newline)
    {
        var parts = new List<string>();
        var externs = string.Join(newline, importRoots
            .SelectMany(importRoot => importRoot.Externs)
            .Select(externDirective => externDirective.ToFullString().Trim())
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal));
        var usings = string.Join(newline, importRoots
            .SelectMany(importRoot => importRoot.Usings)
            .Select(usingDirective => usingDirective.ToFullString().Trim())
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal));
        if (externs.Length > 0) parts.Add(externs);
        if (usings.Length > 0) parts.Add(usings);
        if (namespaceName.Length > 0) parts.Add("namespace " + namespaceName + ";");

        var body = string.Join(newline + newline,
            members.Select(member => Indent(member, newline)));
        var generic = typeParameters?.ToString() ?? "";
        var constraintText = constraints.Count == 0
            ? ""
            : " " + string.Join(" ", constraints.Select(constraint => constraint.ToString()));
        parts.Add(accessibility + " interface " + name + generic + constraintText + newline + "{" + newline
            + body + newline + "}");
        return string.Join(newline + newline, parts) + newline;
    }

    /// <summary>別partial宣言からusing aliasを集める。interfaceファイルへ全aliasを移すと
    /// 同名aliasの異なる対象が衝突するため、その場合だけ安全側で拒否する。</summary>
    private static bool HasConflictingUsingAliases(
        IReadOnlyList<ClassDeclarationSyntax> declarations)
        => declarations
            .SelectMany(declaration => (declaration.SyntaxTree.GetRoot() as CompilationUnitSyntax)?.Usings
                ?? [])
            .Where(usingDirective => usingDirective.Alias is not null)
            .GroupBy(usingDirective => usingDirective.Alias!.Name.Identifier.ValueText,
                StringComparer.Ordinal)
            .Any(group => group.Select(usingDirective => usingDirective.Name?.ToString() ?? "")
                .Distinct(StringComparer.Ordinal).Count() > 1);

    private static string InterfaceAccessibility(
        ClassDeclarationSyntax type, INamedTypeSymbol? semanticTypeSymbol)
    {
        if (semanticTypeSymbol is not null)
            return semanticTypeSymbol.DeclaredAccessibility switch
            {
                RoslynAccessibility.Public => "public",
                _ => "internal",
            };

        // 構文fallbackの既存契約はpublic interface。可視性を正確に引き継ぐのは
        // semantic compilationが利用できる経路だけに限定する。
        return "public";
    }

    private static string Indent(string text, string newline)
        => string.Join(newline, text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').Select(line => "    " + line));

    private static string InterfaceMethod(MethodDeclarationSyntax method)
    {
        var returnModifier = method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword))
            ? method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ReadOnlyKeyword))
                ? "ref readonly " : "ref "
            : "";
        var constraints = method.ConstraintClauses.Count == 0
            ? ""
            : " " + string.Join(" ", method.ConstraintClauses.Select(clause => clause.ToString()));
        return $"{returnModifier}{method.ReturnType} {method.Identifier}{method.TypeParameterList}"
            + $"({FormatParameters(method.ParameterList)}){constraints};";
    }

    private static string InterfaceProperty(PropertyDeclarationSyntax property)
        => property.AccessorList is null
            ? $"{property.Type} {property.Identifier} {{ get; }}"
            : $"{property.Type} {property.Identifier} {{ {string.Join(" ",
                PublicAccessors(property.AccessorList).Select(accessor => AccessorText(accessor))) } }}";

    private static string InterfaceIndexer(IndexerDeclarationSyntax indexer)
        => $"{indexer.Type} this[{FormatParameters(indexer.ParameterList)}] {{ {string.Join(" ",
            PublicAccessors(indexer.AccessorList!).Select(accessor => AccessorText(accessor))) } }}";

    private static string AccessorText(AccessorDeclarationSyntax accessor)
        => accessor.IsKind(SyntaxKind.InitAccessorDeclaration) ? "init;"
            : accessor.IsKind(SyntaxKind.SetAccessorDeclaration) ? "set;" : "get;";

    private static List<AccessorDeclarationSyntax> PublicAccessors(AccessorListSyntax accessors)
        => accessors.Accessors
            .Where(accessor => !accessor.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.PrivateKeyword)
                || modifier.IsKind(SyntaxKind.ProtectedKeyword)
                || modifier.IsKind(SyntaxKind.InternalKeyword)))
            .Where(accessor => accessor.Kind() is SyntaxKind.GetAccessorDeclaration
                or SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration)
            .ToList();

    private static bool IsSemanticPublicInstance(
        MemberDeclarationSyntax member, INamedTypeSymbol containingType)
    {
        if (!member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)))
            return false;
        if (member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)))
            return false;

        return member switch
        {
            MethodDeclarationSyntax method => containingType.GetMembers(method.Identifier.ValueText)
                .OfType<IMethodSymbol>().Any(methodSymbol => !methodSymbol.IsStatic),
            PropertyDeclarationSyntax property => containingType.GetMembers(property.Identifier.ValueText)
                .OfType<IPropertySymbol>().Any(propertySymbol => !propertySymbol.IsStatic),
            IndexerDeclarationSyntax => containingType.GetMembers()
                .OfType<IPropertySymbol>().Any(propertySymbol => propertySymbol.IsIndexer &&
                    !propertySymbol.IsStatic),
            EventDeclarationSyntax @event => containingType.GetMembers(@event.Identifier.ValueText)
                .OfType<IEventSymbol>().Any(eventSymbol => !eventSymbol.IsStatic),
            EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables
                .SelectMany(variable => containingType.GetMembers(variable.Identifier.ValueText))
                .OfType<IEventSymbol>().Any(eventSymbol => !eventSymbol.IsStatic),
            _ => false,
        };
    }

    private static bool IsPublicInstance(MemberDeclarationSyntax member)
        => member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword))
            && !member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)
                || modifier.IsKind(SyntaxKind.PrivateKeyword)
                || modifier.IsKind(SyntaxKind.ProtectedKeyword)
                || modifier.IsKind(SyntaxKind.InternalKeyword));

    private static string FormatParameters(BaseParameterListSyntax parameters)
        => string.Join(", ", parameters.Parameters.Select(parameter =>
        {
            var modifiers = string.Join(" ", parameter.Modifiers.Select(modifier => modifier.Text));
            var prefix = modifiers.Length == 0 ? "" : modifiers + " ";
            var defaultValue = parameter.Default?.ToString() ?? "";
            return $"{prefix}{parameter.Type?.ToString() ?? "object"} {parameter.Identifier.ValueText}{defaultValue}";
        }));

    private static string MethodKey(string name, ParameterListSyntax parameters)
        => name + "/" + parameters.Parameters.Count + "/" + string.Join(",", parameters.Parameters.Select(parameter =>
            (parameter.Type?.ToString() ?? "object") + ":" + string.Join(" ", parameter.Modifiers.Select(modifier => modifier.Text))));

    private static string BaseTypeName(TypeSyntax type)
    {
        var text = type.ToString().TrimEnd('?');
        var lastDot = text.LastIndexOf('.');
        if (lastDot >= 0) text = text[(lastDot + 1)..];
        var generic = text.IndexOf('<');
        return generic >= 0 ? text[..generic] : text;
    }

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static bool TryGetSelectionSpan(SourceText source, LspRange range, out TextSpan span)
    {
        span = default;
        if (range.Start.Line < 0 || range.End.Line < 0
            || range.Start.Line >= source.Lines.Count || range.End.Line >= source.Lines.Count)
            return false;
        var start = Position(source, range.Start);
        var end = Position(source, range.End);
        if (start > end) (start, end) = (end, start);
        if (start == end) return false;
        span = TextSpan.FromBounds(start, end);
        return true;
    }

    private static int Position(SourceText source, LspPosition position)
    {
        var line = source.Lines[position.Line];
        return line.Start + Math.Clamp(position.Character, 0, line.Span.Length);
    }

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);
}
