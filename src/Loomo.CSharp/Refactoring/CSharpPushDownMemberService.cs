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

/// <summary>基底クラスの公開／protectedメンバーを直接の派生クラスへ移す。
/// 構文だけで安全に対象を一意に決められる場合に限定し、派生クラスが複数ある場合や
/// 基底クラスの別メンバーへ依存する場合は、部分適用を防ぐため編集を返さない。</summary>
public static class CSharpPushDownMemberService
{
    public static CSharpCodeGenerationResult PushDown(
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? workspaceTexts = null,
        string? destinationPath = null,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions = null)
        => PushDownCore(filePath, sourceText, selection, workspaceTexts,
            destinationPath, workspaceParseOptions, semanticCompilation: null);

    internal static CSharpCodeGenerationResult PushDown(
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        string? destinationPath,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions,
        CSharpCompilation semanticCompilation)
        => PushDownCore(filePath, sourceText, selection, workspaceTexts,
            destinationPath, workspaceParseOptions, semanticCompilation);

    private static CSharpCodeGenerationResult PushDownCore(
        string filePath,
        string sourceText,
        LspRange selection,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        string? destinationPath,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみメンバーを派生クラスへ移動できます。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var activeRoot = CSharpSyntaxTree.ParseText(source,
            ParseOptionsFor(filePath, workspaceParseOptions)).GetCompilationUnitRoot();
        var candidates = FindMembers(activeRoot, selectedSpan);
        if (candidates.Count != 1)
            return Failed("派生クラスへ移すメンバー名全体を選択してください。");

        var (member, name) = candidates[0];
        if (member.Parent is not ClassDeclarationSyntax baseType)
            return Failed("クラス直下のメンバーだけを移動できます。");
        if (baseType.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword))
            || baseType.TypeParameterList is not null)
            return Failed("partial／generic基底クラスは対象外です。");
        if (member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PrivateKeyword)
                || modifier.IsKind(SyntaxKind.StaticKeyword)
                || modifier.IsKind(SyntaxKind.OverrideKeyword)
                || modifier.IsKind(SyntaxKind.AbstractKeyword)))
            return Failed("private／static／override／abstractメンバーは派生クラスへ移動できません。");
        if (!member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)
                || modifier.IsKind(SyntaxKind.ProtectedKeyword)))
            return Failed("publicまたはprotectedメンバーだけを移動できます。");
        if (member is not MethodDeclarationSyntax
                and not PropertyDeclarationSyntax
                and not IndexerDeclarationSyntax
                and not EventDeclarationSyntax
                and not EventFieldDeclarationSyntax
                and not FieldDeclarationSyntax)
            return Failed("この種類のメンバーは派生クラスへ移動できません。");
        if (member is FieldDeclarationSyntax field && field.Declaration.Variables.Count != 1)
            return Failed("複数宣言を含むフィールドは対象外です。");
        if (member is EventFieldDeclarationSyntax eventField
            && eventField.Declaration.Variables.Count != 1)
            return Failed("複数宣言を含むイベントは対象外です。");
        if (member is MemberDeclarationSyntax declaration
            && declaration.GetLeadingTrivia().Any(trivia => trivia.GetStructure() is not null))
            return Failed("構造化コメント付きメンバーは対象外です。");

        var files = ParseWorkspaceRoots(filePath, activeRoot, workspaceTexts, workspaceParseOptions);
        var derivedCandidates = files.SelectMany(file => file.Root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(candidate => !ReferenceEquals(candidate, baseType)
                    && BaseTypeNames(candidate).Contains(baseType.Identifier.ValueText,
                        StringComparer.Ordinal)
                    && SameNamespace(baseType, candidate)
                    && candidate.Parent is not TypeDeclarationSyntax)
                .Select(candidate => (file, candidate)))
            .ToList();
        if (destinationPath is not null)
        {
            var fullDestination = Path.GetFullPath(destinationPath);
            derivedCandidates = derivedCandidates
                .Where(candidate => string.Equals(candidate.file.Path, fullDestination,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        if (derivedCandidates.Count == 0)
            return Failed("直接の派生クラスをワークスペース内で解決できません。");
        if (derivedCandidates.Count != 1)
            return Failed("直接の派生クラスが複数あるため、構文だけでは移動先を一意に決められません。");

        var destinationFile = derivedCandidates[0].file;
        var destinationType = derivedCandidates[0].candidate;
        if (destinationType.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword))
            || destinationType.TypeParameterList is not null)
            return Failed("partial／generic派生クラスは対象外です。");
        if (HasSameMember(destinationType, member, name))
            return Failed("派生クラスに同じメンバーが既にあります。");

        var dependencies = semanticCompilation is { } compilation
            ? FindPrivateSemanticDependencies(
                member, baseType, destinationType, filePath, compilation)
            : FindSyntaxDependencies(member, baseType);
        if (dependencies.Count > 0)
            return Failed("基底クラス固有メンバー（" + string.Join("、", dependencies) + "）に依存しています。");

        if (!TryGetMemberText(source, member, out var memberText, out var sourceIndent))
            return Failed("移動元メンバーを行単位で安全に読み取れませんでした。");
        if (!TryGetInsertion(destinationType, out var insertion, out var destinationIndent))
            return Failed("派生クラスへメンバーを挿入できませんでした。");

        var normalized = Reindent(memberText, sourceIndent, destinationIndent);
        var sourceUri = LspUri.FromPath(Path.GetFullPath(filePath));
        var destinationUri = LspUri.FromPath(Path.GetFullPath(destinationFile.Path));
        var removal = new LspTextEdit(RemovalRange(source, member), "");
        var insertionEdit = new LspTextEdit(insertion, normalized + NewlineOf(source));
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase);
        if (string.Equals(sourceUri, destinationUri, StringComparison.OrdinalIgnoreCase))
            changes[sourceUri] = [removal, insertionEdit];
        else
        {
            changes[sourceUri] = [removal];
            changes[destinationUri] = [insertionEdit];
        }
        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(changes), $"メンバー「{name}」を派生クラスへ移動");
    }

    private static List<string> FindSyntaxDependencies(
        MemberDeclarationSyntax member, ClassDeclarationSyntax baseType)
    {
        var baseNames = new HashSet<string>(baseType.Members
            .Where(candidate => !ReferenceEquals(candidate, member))
            .SelectMany(MemberNames), StringComparer.Ordinal);
        return member.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(identifier => baseNames.Contains(identifier.Identifier.ValueText))
            .Select(identifier => identifier.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> FindPrivateSemanticDependencies(
        MemberDeclarationSyntax member,
        ClassDeclarationSyntax baseType,
        ClassDeclarationSyntax destinationType,
        string activeFilePath,
        CSharpCompilation compilation)
    {
        var baseModel = CSharpSemanticCompilation.ForFile(
            compilation, string.IsNullOrWhiteSpace(baseType.SyntaxTree.FilePath)
                ? activeFilePath : baseType.SyntaxTree.FilePath);
        var destinationModel = CSharpSemanticCompilation.ForFile(
            compilation, destinationType.SyntaxTree.FilePath ?? "");
        if (baseModel is null || destinationModel is null)
            return ["意味モデルを解決できないメンバー"];

        var semanticBase = FindEquivalent(baseType, baseModel);
        var semanticDestination = FindEquivalent(destinationType, destinationModel);
        if (semanticBase is null || semanticDestination is null ||
            baseModel.GetDeclaredSymbol(semanticBase) is not INamedTypeSymbol baseSymbol ||
            destinationModel.GetDeclaredSymbol(semanticDestination) is not INamedTypeSymbol destinationSymbol ||
            !SymbolEqualityComparer.Default.Equals(destinationSymbol.BaseType, baseSymbol))
            return ["基底／派生クラスを意味モデルから一意に解決できません"];

        var semanticMember = FindEquivalent(member, baseModel);
        var memberSymbol = semanticMember is null
            ? null
            : GetDeclaredMemberSymbol(semanticMember, baseModel);
        if (memberSymbol is null)
            return ["移動対象メンバーを意味モデルから解決できません"];

        return member.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(identifier => FindEquivalent(identifier, baseModel) is { } equivalent
                ? baseModel.GetSymbolInfo(equivalent).Symbol
                : null)
            .OfType<ISymbol>()
            .Where(symbol => symbol is IFieldSymbol or IPropertySymbol or
                IEventSymbol or IMethodSymbol)
            .Where(symbol => SymbolEqualityComparer.Default.Equals(
                symbol.ContainingType, baseSymbol))
            .Where(symbol => !SymbolEqualityComparer.Default.Equals(symbol, memberSymbol))
            .Where(symbol => symbol.DeclaredAccessibility == RoslynAccessibility.Private)
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
        ClassDeclarationSyntax type, out LspRange range, out string indent)
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

    private static IReadOnlyList<(string Path, CompilationUnitSyntax Root)> ParseWorkspaceRoots(
        string activePath, CompilationUnitSyntax activeRoot,
        IReadOnlyDictionary<string, string>? workspaceTexts,
        IReadOnlyDictionary<string, CSharpParseOptions>? workspaceParseOptions)
    {
        var activeFullPath = Path.GetFullPath(activePath);
        var result = new List<(string, CompilationUnitSyntax)> { (activeFullPath, activeRoot) };
        if (workspaceTexts is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { activeFullPath };
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

    private static IEnumerable<string> BaseTypeNames(ClassDeclarationSyntax type)
        => type.BaseList?.Types.Select(baseType => BaseTypeName(baseType.Type))
            .Where(name => name.Length > 0) ?? [];

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
