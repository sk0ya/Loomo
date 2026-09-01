using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>派生クラスの公開／protectedメンバーを直接の基底クラスへ移す。
/// 複数ファイルの構文だけで安全に扱える範囲へ限定し、派生固有メンバーへの依存・重複・partial・
/// genericを検出した場合は削除と挿入のどちらも作らない。</summary>
public static class CSharpPullUpMemberService
{
    public static CSharpCodeGenerationResult PullUp(
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? workspaceTexts = null,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions = null)
        => PullUpCore(filePath, sourceText, selection, workspaceTexts,
            workspaceParseOptions, semanticCompilation: null);

    internal static CSharpCodeGenerationResult PullUp(
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions,
        CSharpCompilation semanticCompilation)
        => PullUpCore(filePath, sourceText, selection, workspaceTexts,
            workspaceParseOptions, semanticCompilation);

    private static CSharpCodeGenerationResult PullUpCore(
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみメンバーを基底クラスへ移動できます。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var activeRoot = CSharpSyntaxTree.ParseText(source,
            ParseOptionsFor(filePath, workspaceParseOptions)).GetCompilationUnitRoot();
        var candidates = FindMembers(activeRoot, selectedSpan);
        if (candidates.Count != 1)
            return Failed("基底クラスへ移すメンバー名全体を選択してください。");

        var (member, name) = candidates[0];
        if (member.Parent is not ClassDeclarationSyntax derived)
            return Failed("クラス直下のメンバーだけを移動できます。");
        if (derived.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword))
            || derived.TypeParameterList is not null)
            return Failed("partial／genericクラスは対象外です。");
        if (member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PrivateKeyword)
                || modifier.IsKind(SyntaxKind.StaticKeyword)))
            return Failed("private／staticメンバーは基底クラスへ移動できません。");
        if (!member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)
                || modifier.IsKind(SyntaxKind.ProtectedKeyword)))
            return Failed("publicまたはprotectedメンバーだけを移動できます。");
        if (member is not MethodDeclarationSyntax
                and not PropertyDeclarationSyntax
                and not IndexerDeclarationSyntax
                and not EventDeclarationSyntax
                and not EventFieldDeclarationSyntax
                and not FieldDeclarationSyntax)
            return Failed("この種類のメンバーは基底クラスへ移動できません。");
        if (member is FieldDeclarationSyntax field && field.Declaration.Variables.Count != 1)
            return Failed("複数宣言を含むフィールドは対象外です。");
        if (member is MethodDeclarationSyntax method
            && method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.OverrideKeyword)))
            return Failed("overrideメンバーは基底クラスへ移動できません。");
        if (member is MemberDeclarationSyntax declaration
            && declaration.GetLeadingTrivia().Any(trivia => trivia.GetStructure() is not null))
            return Failed("構造化コメント付きメンバーは対象外です。");

        var files = ParseWorkspaceRoots(filePath, activeRoot, workspaceTexts, workspaceParseOptions);
        var baseTypeNames = derived.BaseList?.Types
            .Select(baseType => BaseTypeName(baseType.Type))
            .Where(baseType => baseType.Length > 0)
            .ToList() ?? [];
        var baseCandidates = files.SelectMany(file => file.Root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(candidate => baseTypeNames.Contains(
                    candidate.Identifier.ValueText, StringComparer.Ordinal)
                    && !ReferenceEquals(candidate, derived))
                .Select(candidate => (file, candidate)))
            .ToList();
        if (baseCandidates.Count != 1)
            return Failed("直接の基底クラスをワークスペース内で一意に解決できません。");

        var baseFile = baseCandidates[0].file;
        var baseType = baseCandidates[0].candidate;
        if (baseType.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.SealedKeyword)
                || modifier.IsKind(SyntaxKind.PartialKeyword))
            || baseType.TypeParameterList is not null)
            return Failed("sealed／partial／generic基底クラスは対象外です。");
        if (!SameNamespace(derived, baseType))
            return Failed("名前空間が異なる基底クラスは構文だけでは解決できません。");

        if (HasSameMember(baseType, member, name))
            return Failed("基底クラスに同じメンバーが既にあります。");

        var derivedNames = new HashSet<string>(derived.Members
            .Where(candidate => !ReferenceEquals(candidate, member))
            .SelectMany(MemberNames), StringComparer.Ordinal);
        var dependencies = semanticCompilation is { } compilation
            ? FindSemanticDependencies(
                member, derived, baseType, filePath, baseFile.Path, compilation)
            : FindSyntaxDependencies(member, derivedNames);
        if (dependencies.Count > 0)
            return Failed("派生クラス固有メンバー（" + string.Join("、", dependencies) + "）に依存しています。");

        if (!TryGetMemberText(source, member, out var memberText, out var sourceIndent))
            return Failed("移動元メンバーを行単位で安全に読み取れませんでした。");
        if (!TryGetInsertion(baseFile.Root, baseType, out var insertion, out var destinationIndent))
            return Failed("基底クラスへメンバーを挿入できませんでした。");

        var normalized = Reindent(memberText, sourceIndent, destinationIndent);
        var sourceUri = LspUri.FromPath(Path.GetFullPath(filePath));
        var baseUri = LspUri.FromPath(Path.GetFullPath(baseFile.Path));
        var removal = new LspTextEdit(RemovalRange(source, member), "");
        var insertionEdit = new LspTextEdit(insertion, normalized + NewlineOf(source));
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase);
        if (string.Equals(sourceUri, baseUri, StringComparison.OrdinalIgnoreCase))
            changes[sourceUri] = [removal, insertionEdit];
        else
        {
            changes[sourceUri] = [removal];
            changes[baseUri] = [insertionEdit];
        }
        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(changes), $"メンバー「{name}」を基底クラスへ移動");
    }

    private static List<string> FindSyntaxDependencies(
        MemberDeclarationSyntax member, ISet<string> derivedNames)
        => member.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(identifier => derivedNames.Contains(identifier.Identifier.ValueText))
            .Select(identifier => identifier.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static List<string> FindSemanticDependencies(
        MemberDeclarationSyntax member,
        ClassDeclarationSyntax derivedType,
        ClassDeclarationSyntax baseType,
        string activeFilePath,
        string baseFilePath,
        CSharpCompilation compilation)
    {
        var derivedModel = CSharpSemanticCompilation.ForFile(
            compilation, string.IsNullOrWhiteSpace(derivedType.SyntaxTree.FilePath)
                ? activeFilePath : derivedType.SyntaxTree.FilePath);
        var baseModel = CSharpSemanticCompilation.ForFile(compilation, baseFilePath);
        if (derivedModel is null || baseModel is null)
            return ["意味モデルを解決できないメンバー"];

        var semanticDerived = FindEquivalent(derivedType, derivedModel);
        var semanticBase = FindEquivalent(baseType, baseModel);
        if (semanticDerived is null || semanticBase is null ||
            derivedModel.GetDeclaredSymbol(semanticDerived) is not INamedTypeSymbol derivedSymbol ||
            baseModel.GetDeclaredSymbol(semanticBase) is not INamedTypeSymbol baseSymbol ||
            !SymbolEqualityComparer.Default.Equals(derivedSymbol.BaseType, baseSymbol))
            return ["基底／派生クラスを意味モデルから一意に解決できません"];

        var semanticMember = FindEquivalent(member, derivedModel);
        var memberSymbol = semanticMember is null
            ? null
            : GetDeclaredMemberSymbol(semanticMember, derivedModel);
        if (memberSymbol is null)
            return ["移動対象メンバーを意味モデルから解決できません"];

        return member.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(identifier => FindEquivalent(identifier, derivedModel) is { } equivalent
                ? derivedModel.GetSymbolInfo(equivalent).Symbol
                : null)
            .OfType<ISymbol>()
            .Where(symbol => symbol is IFieldSymbol or IPropertySymbol or
                IEventSymbol or IMethodSymbol)
            .Where(symbol => SymbolEqualityComparer.Default.Equals(
                symbol.ContainingType, derivedSymbol))
            .Where(symbol => !SymbolEqualityComparer.Default.Equals(symbol, memberSymbol))
            .Select(symbol => symbol.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static ISymbol? GetDeclaredMemberSymbol(
        MemberDeclarationSyntax member, SemanticModel semanticModel)
        => member switch
        {
            FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1
                => semanticModel.GetDeclaredSymbol(field.Declaration.Variables[0]),
            EventFieldDeclarationSyntax eventField when eventField.Declaration.Variables.Count == 1
                => semanticModel.GetDeclaredSymbol(eventField.Declaration.Variables[0]),
            _ => semanticModel.GetDeclaredSymbol(member),
        };

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static List<(MemberDeclarationSyntax Member, string Name)> FindMembers(
        CompilationUnitSyntax root, TextSpan selection)
    {
        var result = new List<(MemberDeclarationSyntax, string)>();
        result.AddRange(root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(member => member.Identifier.Span == selection)
            .Select(member => ((MemberDeclarationSyntax)member, member.Identifier.ValueText)));
        result.AddRange(root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(member => member.Identifier.Span == selection)
            .Select(member => ((MemberDeclarationSyntax)member, member.Identifier.ValueText)));
        result.AddRange(root.DescendantNodes().OfType<IndexerDeclarationSyntax>()
            .Where(member => member.ThisKeyword.Span == selection)
            .Select(member => ((MemberDeclarationSyntax)member, "this")));
        result.AddRange(root.DescendantNodes().OfType<EventDeclarationSyntax>()
            .Where(member => member.Identifier.Span == selection)
            .Select(member => ((MemberDeclarationSyntax)member, member.Identifier.ValueText)));
        result.AddRange(root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Declaration.Variables.Any(variable => variable.Identifier.Span == selection))
            .Select(field => ((MemberDeclarationSyntax)field,
                field.Declaration.Variables.First(variable => variable.Identifier.Span == selection)
                    .Identifier.ValueText)));
        result.AddRange(root.DescendantNodes().OfType<EventFieldDeclarationSyntax>()
            .Where(field => field.Declaration.Variables.Any(variable => variable.Identifier.Span == selection))
            .Select(field => ((MemberDeclarationSyntax)field,
                field.Declaration.Variables.First(variable => variable.Identifier.Span == selection)
                    .Identifier.ValueText)));
        return result;
    }

    private static IEnumerable<string> MemberNames(MemberDeclarationSyntax member)
    {
        yield return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            EventDeclarationSyntax @event => @event.Identifier.ValueText,
            FieldDeclarationSyntax field => field.Declaration.Variables.First().Identifier.ValueText,
            EventFieldDeclarationSyntax field => field.Declaration.Variables.First().Identifier.ValueText,
            _ => "this",
        };
    }

    private static bool HasSameMember(
        ClassDeclarationSyntax type, MemberDeclarationSyntax member, string name)
        => type.Members.Any(existing => existing switch
        {
            MethodDeclarationSyntax method when member is MethodDeclarationSyntax targetMethod
                => string.Equals(method.Identifier.ValueText, name, StringComparison.Ordinal)
                    && method.ParameterList.Parameters.Count == targetMethod.ParameterList.Parameters.Count,
            PropertyDeclarationSyntax property when member is PropertyDeclarationSyntax
                => string.Equals(property.Identifier.ValueText, name, StringComparison.Ordinal),
            EventDeclarationSyntax @event when member is EventDeclarationSyntax
                => string.Equals(@event.Identifier.ValueText, name, StringComparison.Ordinal),
            FieldDeclarationSyntax field when member is FieldDeclarationSyntax
                => field.Declaration.Variables.Any(variable =>
                    string.Equals(variable.Identifier.ValueText, name, StringComparison.Ordinal)),
            EventFieldDeclarationSyntax field when member is EventFieldDeclarationSyntax
                => field.Declaration.Variables.Any(variable =>
                    string.Equals(variable.Identifier.ValueText, name, StringComparison.Ordinal)),
            IndexerDeclarationSyntax when member is IndexerDeclarationSyntax => true,
            _ => false,
        });

    private static bool TryGetMemberText(
        SourceText source, MemberDeclarationSyntax member, out string text, out int indent)
    {
        text = "";
        indent = 0;
        var line = source.Lines.GetLineFromPosition(member.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, member.SpanStart));
        if (prefix.Any(character => !char.IsWhiteSpace(character))) return false;
        indent = prefix.Length;
        var raw = member.ToString().TrimEnd();
        if (raw.Length == 0) return false;
        text = raw;
        return true;
    }

    private static bool TryGetInsertion(
        SyntaxNode root, ClassDeclarationSyntax type, out LspRange range, out string indent)
    {
        range = new LspRange(new LspPosition(0, 0), new LspPosition(0, 0));
        indent = "";
        if (type.CloseBraceToken.IsMissing) return false;
        var source = type.SyntaxTree.GetText();
        var closeLine = source.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart);
        var closePrefix = source.ToString(TextSpan.FromBounds(closeLine.Start, type.CloseBraceToken.SpanStart));
        if (closePrefix.Any(character => !char.IsWhiteSpace(character))) return false;
        var first = type.Members.FirstOrDefault();
        if (first is null)
        {
            indent = closePrefix + "    ";
        }
        else
        {
            var memberLine = source.Lines.GetLineFromPosition(first.SpanStart);
            var memberPrefix = source.ToString(TextSpan.FromBounds(memberLine.Start, first.SpanStart));
            if (memberPrefix.Any(character => !char.IsWhiteSpace(character))) return false;
            indent = memberPrefix;
        }
        range = new LspRange(
            new LspPosition(closeLine.LineNumber, 0),
            new LspPosition(closeLine.LineNumber, 0));
        return true;
    }

    private static string Reindent(string text, int sourceIndent, string destinationIndent)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join("\n", lines.Select(line =>
        {
            var remove = Math.Min(sourceIndent, line.TakeWhile(char.IsWhiteSpace).Count());
            return destinationIndent + line[remove..];
        }));
    }

    private static IReadOnlyList<(string Path, SyntaxNode Root)> ParseWorkspaceRoots(
        string activePath, SyntaxNode activeRoot,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions)
    {
        var result = new List<(string, SyntaxNode)> { (Path.GetFullPath(activePath), activeRoot) };
        if (workspaceTexts is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(activePath),
        };
        foreach (var (path, text) in workspaceTexts)
        {
            if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var fullPath = Path.GetFullPath(path);
            if (!seen.Add(fullPath)) continue;
            result.Add((fullPath, CSharpSyntaxTree.ParseText(text,
                ParseOptionsFor(fullPath, workspaceParseOptions), fullPath).GetCompilationUnitRoot()));
        }
        return result;
    }

    private static CSharpParseOptions ParseOptionsFor(
        string path, IReadOnlyDictionary<string, CSharpParseOptions>? options)
        => options is not null && options.TryGetValue(Path.GetFullPath(path), out var configured)
            ? configured : CSharpParseOptions.Default;

    private static bool SameNamespace(TypeDeclarationSyntax left, TypeDeclarationSyntax right)
        => string.Equals(NamespaceName(left), NamespaceName(right), StringComparison.Ordinal);

    private static string NamespaceName(TypeDeclarationSyntax type)
        => string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(namespaceNode => namespaceNode.Name.ToString()));

    private static string BaseTypeName(TypeSyntax type)
    {
        var text = type.ToString().TrimEnd('?');
        var dot = text.LastIndexOf('.');
        if (dot >= 0) text = text[(dot + 1)..];
        var generic = text.IndexOf('<');
        return generic >= 0 ? text[..generic] : text;
    }

    private static LspRange RemovalRange(SourceText source, SyntaxNode node)
    {
        var firstLine = source.Lines.GetLineFromPosition(node.SpanStart);
        var lastLine = source.Lines.GetLineFromPosition(node.Span.End);
        var prefix = source.ToString(TextSpan.FromBounds(firstLine.Start, node.SpanStart));
        var suffix = source.ToString(TextSpan.FromBounds(node.Span.End, lastLine.End));
        if (prefix.All(char.IsWhiteSpace) && suffix.All(char.IsWhiteSpace))
        {
            var end = lastLine.EndIncludingLineBreak;
            return end > lastLine.End
                ? new LspRange(new LspPosition(firstLine.LineNumber, 0),
                    new LspPosition(lastLine.LineNumber + 1, 0))
                : new LspRange(new LspPosition(firstLine.LineNumber, 0),
                    new LspPosition(lastLine.LineNumber, lastLine.Span.Length));
        }
        return ToLspRange(source, node.Span);
    }

    private static string NewlineOf(SourceText source)
        => source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

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

    private static LspRange ToLspRange(SourceText source, TextSpan span)
    {
        var start = source.Lines.GetLinePosition(span.Start);
        var end = source.Lines.GetLinePosition(span.End);
        return new LspRange(new LspPosition(start.Line, start.Character),
            new LspPosition(end.Line, end.Character));
    }

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);
}
