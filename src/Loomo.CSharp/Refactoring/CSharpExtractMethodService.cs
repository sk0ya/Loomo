using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>Roslyn LSP が抽出候補を返さない場合にも使える、保守的な C# のメソッド抽出。
/// 同じブロックに属する文の連続範囲だけを対象にし、意味モデルが渡された場合は
/// `var`の型、shadowing、外側変数への書き込みをsymbol identityで検証する。編集結果は
/// 通常の WorkspaceEdit と同じ preview／rollback／Undo 経路へ渡す。</summary>
public static class CSharpExtractMethodService
{
    public static CSharpCodeGenerationResult Extract(
        string filePath,
        string sourceText,
        LspRange selection,
        string methodName)
        => ExtractCore(filePath, sourceText, selection, methodName,
            semanticCompilation: null);

    internal static CSharpCodeGenerationResult Extract(
        string filePath,
        string sourceText,
        LspRange selection,
        string methodName,
        CSharpCompilation semanticCompilation)
        => ExtractCore(filePath, sourceText, selection, methodName, semanticCompilation);

    private static CSharpCodeGenerationResult ExtractCore(
        string filePath,
        string sourceText,
        LspRange selection,
        string methodName,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみメソッド抽出を実行できます。");
        if (!SyntaxFacts.IsValidIdentifier(methodName)
            || SyntaxFacts.GetKeywordKind(methodName) != SyntaxKind.None)
            return Failed("メソッド名がC#の識別子として正しくありません。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectionSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var selected = FindStatementSelection(root, selectionSpan);
        if (selected is null)
            return Failed("同じブロック内の連続した文を選択してください。");

        var (block, firstIndex, lastIndex, statements) = selected.Value;
        var methodLike = block.Ancestors().FirstOrDefault(IsMethodLike);
        if (methodLike is null)
            return Failed("メソッドまたはローカル関数の本文から抽出してください。");

        if (methodLike is not MethodDeclarationSyntax and not LocalFunctionStatementSyntax)
            return Failed("通常のメソッドまたはローカル関数から抽出してください。");

        if (methodLike is MethodDeclarationSyntax enclosingMethod &&
            (enclosingMethod.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)) ||
             enclosingMethod.DescendantNodes().OfType<YieldStatementSyntax>().Any()))
            return Failed("async／iteratorメソッドからの抽出は、await／yieldの意味を壊さないため対象外です。");

        var type = methodLike.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null)
            return Failed("型の中にあるメソッドから抽出してください。");
        if (type.Members.OfType<MethodDeclarationSyntax>().Any(m =>
                string.Equals(m.Identifier.ValueText, methodName, StringComparison.Ordinal)))
            return Failed("同名のメソッドが既にあります。");

        var selectedStatements = statements.Skip(firstIndex).Take(lastIndex - firstIndex + 1).ToList();
        if (selectedStatements.Any(ContainsUnsupportedControlFlow))
            return Failed("break／continue／gotoを含む範囲は安全に抽出できません。");

        var returnStatement = selectedStatements.Count == 1
            ? selectedStatements[0] as ReturnStatementSyntax
            : null;
        if (selectedStatements.OfType<ReturnStatementSyntax>().Any() && returnStatement is null)
            return Failed("return文を含む場合は、return文だけを選択してください。");

        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        var parameters = FindInputParameters(
            methodLike, block, selectedStatements, selectionSpan.Start, semanticModel);
        if (parameters.Error is not null)
            return Failed(parameters.Error);

        if (DeclaresVariableUsedAfterSelection(
                block, lastIndex, selectedStatements, semanticModel))
        {
            return Failed("抽出範囲内で宣言したローカル変数が範囲外で使われています。");
        }

        var invocation = BuildInvocation(methodName, parameters.Items, returnStatement is not null);
        var replacement = new LspTextEdit(ToLspRange(source, new TextSpan(
            selectedStatements[0].SpanStart,
            selectedStatements[^1].Span.End - selectedStatements[0].SpanStart)), invocation);

        var generated = BuildGeneratedMethod(
            source,
            type,
            methodLike,
            selectedStatements,
            methodName,
            parameters.Items,
            returnStatement is not null);
        if (generated is null)
            return Failed("抽出した本文を新しいメソッドへ移せませんでした。");

        var close = type.CloseBraceToken;
        if (close.IsMissing)
            return Failed("型の閉じ括弧が見つかりません。");
        var closeLine = source.Lines.GetLineFromPosition(close.SpanStart);
        var closeIndent = source.ToString(TextSpan.FromBounds(closeLine.Start, close.SpanStart));
        if (closeIndent.Any(c => !char.IsWhiteSpace(c)))
            return Failed("型の閉じ括弧を含む行の字下げを解釈できません。");

        var memberIndent = FindMemberIndent(source, type, closeIndent);
        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = IndentMember(generated, memberIndent, newline) + newline;
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
        {
            [uri] =
            [
                replacement,
                new LspTextEdit(
                    new LspRange(
                        new LspPosition(closeLine.LineNumber, 0),
                        new LspPosition(closeLine.LineNumber, 0)),
                    insertion),
            ],
        };
        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(changes),
            $"メソッド「{methodName}」を抽出");
    }

    private static (BlockSyntax Block, int First, int Last, IReadOnlyList<StatementSyntax> Statements)?
        FindStatementSelection(SyntaxNode root, TextSpan selection)
    {
        var candidates = root.DescendantNodes().OfType<BlockSyntax>()
            .Select(block =>
            {
                var statements = block.Statements.ToList();
                var first = statements.FindIndex(statement => statement.SpanStart >= selection.Start);
                var last = statements.FindLastIndex(statement => statement.Span.End <= selection.End);
                return (block, statements, first, last);
            })
            .Where(candidate => candidate.first >= 0 && candidate.last >= candidate.first)
            .Where(candidate => candidate.statements.All(statement =>
                !statement.Span.OverlapsWith(selection)
                || (statement.SpanStart >= selection.Start && statement.Span.End <= selection.End)))
            .Where(candidate => candidate.statements[candidate.first].SpanStart >= selection.Start
                && candidate.statements[candidate.last].Span.End <= selection.End)
            .OrderBy(candidate => candidate.statements[candidate.last].Span.End
                - candidate.statements[candidate.first].SpanStart)
            .FirstOrDefault();

        if (candidates.block is null) return null;
        return (candidates.block, candidates.first, candidates.last, candidates.statements);
    }

    private static (IReadOnlyList<InputParameter> Items, string? Error) FindInputParameters(
        SyntaxNode methodLike,
        BlockSyntax block,
        IReadOnlyList<StatementSyntax> selectedStatements,
        int selectionStart,
        SemanticModel? semanticModel)
    {
        var declaredBefore = new Dictionary<string, InputParameter>(StringComparer.Ordinal);
        if (methodLike is BaseMethodDeclarationSyntax baseMethod)
        {
            foreach (var parameter in baseMethod.ParameterList.Parameters)
            {
                if (parameter.Type is null) return ([], "型を推定できない引数があるため抽出できません。");
                var semanticParameter = semanticModel is null
                    ? null
                    : FindEquivalent(parameter, semanticModel);
                var semanticSymbol = semanticParameter is null
                    ? null
                    : semanticModel!.GetDeclaredSymbol(semanticParameter);
                var type = semanticSymbol is IParameterSymbol parameterSymbol
                    ? parameterSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    : parameter.Type.ToString();
                declaredBefore[parameter.Identifier.ValueText] = new InputParameter(
                    parameter.Identifier.ValueText,
                    type,
                    ParameterModifier(parameter),
                    semanticSymbol);
            }
        }
        else if (methodLike is LocalFunctionStatementSyntax localFunction)
        {
            foreach (var parameter in localFunction.ParameterList.Parameters)
            {
                if (parameter.Type is null) return ([], "型を推定できない引数があるため抽出できません。");
                var semanticParameter = semanticModel is null
                    ? null
                    : FindEquivalent(parameter, semanticModel);
                var semanticSymbol = semanticParameter is null
                    ? null
                    : semanticModel!.GetDeclaredSymbol(semanticParameter);
                var type = semanticSymbol is IParameterSymbol parameterSymbol
                    ? parameterSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    : parameter.Type.ToString();
                declaredBefore[parameter.Identifier.ValueText] = new InputParameter(
                    parameter.Identifier.ValueText,
                    type,
                    ParameterModifier(parameter),
                    semanticSymbol);
            }
        }

        // 変数は選択対象のblockだけでなく、そこから外側のblockにも存在する。
        // 外側から内側の順に登録することで、同名のshadowingは内側のsymbolで上書きする。
        var visibleLocalDeclarations = block.AncestorsAndSelf()
            .OfType<BlockSyntax>()
            .Reverse()
            .SelectMany(scope => scope.Statements
                .Where(statement => statement.Span.End <= selectionStart)
                .OfType<LocalDeclarationStatementSyntax>());
        foreach (var local in visibleLocalDeclarations)
        {
            foreach (var variable in local.Declaration.Variables)
            {
                var semanticVariable = semanticModel is null
                    ? null
                    : FindEquivalent(variable, semanticModel);
                var semanticSymbol = semanticVariable is null
                    ? null
                    : semanticModel!.GetDeclaredSymbol(semanticVariable);
                if (local.Declaration.Type.IsVar && semanticSymbol is null)
                    return ([], $"ローカル変数「{variable.Identifier.ValueText}」のvar型を安全に推定できません。");

                var type = semanticSymbol is ILocalSymbol localSymbol
                    ? localSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    : local.Declaration.Type.ToString();
                declaredBefore[variable.Identifier.ValueText] = new InputParameter(
                    variable.Identifier.ValueText,
                    type,
                    "",
                    semanticSymbol);
            }
        }

        var identifiers = selectedStatements
            .SelectMany(statement => statement.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>())
            .ToArray();
        var result = new List<InputParameter>();
        foreach (var parameter in declaredBefore.Values)
        {
            var references = identifiers.Where(identifier =>
                MatchesInputParameter(identifier, parameter, semanticModel)).ToArray();
            if (references.Length == 0) continue;

            var modifier = parameter.Modifier;
            if (references.Any(IsWriteContext))
            {
                if (modifier.StartsWith("in ", StringComparison.Ordinal))
                    return ([], $"引数「{parameter.Name}」はinのため、書き込みを含む範囲へ抽出できません。");
                if (!modifier.Contains("ref ", StringComparison.Ordinal) &&
                    !modifier.Contains("out ", StringComparison.Ordinal))
                    modifier = "ref ";
            }

            result.Add(parameter with { Modifier = modifier });
        }

        if (semanticModel is not null && IsStaticMethod(methodLike))
        {
            var instanceMember = identifiers
                .Select(identifier => FindEquivalent(identifier, semanticModel) is { } equivalent
                    ? semanticModel.GetSymbolInfo(equivalent).Symbol
                    : null)
                .OfType<ISymbol>()
                .FirstOrDefault(IsUnusableInstanceMemberFromStaticMethod);
            if (instanceMember is not null)
                return ([], $"staticメソッドからインスタンスメンバー「{instanceMember.Name}」を安全に抽出できません。");
        }

        return (result, null);
    }

    private static bool DeclaresVariableUsedAfterSelection(
        BlockSyntax block,
        int lastIndex,
        IReadOnlyList<StatementSyntax> selectedStatements,
        SemanticModel? semanticModel)
    {
        var declarations = selectedStatements
            .SelectMany(statement => statement.DescendantNodesAndSelf()
                .OfType<VariableDeclaratorSyntax>())
            .ToArray();
        if (declarations.Length == 0) return false;

        var after = block.Statements.Skip(lastIndex + 1)
            .SelectMany(statement => statement.DescendantNodes()
                .OfType<IdentifierNameSyntax>());
        if (semanticModel is null)
        {
            var names = declarations.Select(variable => variable.Identifier.ValueText)
                .ToHashSet(StringComparer.Ordinal);
            return after.Any(identifier => names.Contains(identifier.Identifier.ValueText));
        }

        var symbols = declarations
            .Select(variable => FindEquivalent(variable, semanticModel))
            .Select(variable => variable is null
                ? null
                : semanticModel.GetDeclaredSymbol(variable))
            .Where(symbol => symbol is not null)
            .ToArray();
        return after.Any(identifier =>
        {
            var symbol = FindEquivalent(identifier, semanticModel) is { } equivalent
                ? semanticModel.GetSymbolInfo(equivalent).Symbol
                : null;
            return symbol is not null && symbols.Any(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate, symbol));
        });
    }

    private static bool MatchesInputParameter(
        IdentifierNameSyntax identifier,
        InputParameter parameter,
        SemanticModel? semanticModel)
    {
        if (semanticModel is not null && parameter.Symbol is not null)
        {
            var symbol = FindEquivalent(identifier, semanticModel) is { } equivalent
                ? semanticModel.GetSymbolInfo(equivalent).Symbol
                : null;
            return symbol is not null && SymbolEqualityComparer.Default.Equals(
                symbol, parameter.Symbol);
        }
        return string.Equals(identifier.Identifier.ValueText, parameter.Name,
            StringComparison.Ordinal);
    }

    private static bool IsWriteContext(IdentifierNameSyntax identifier)
    {
        for (SyntaxNode? node = identifier; node is not null; node = node.Parent)
        {
            if (node is AssignmentExpressionSyntax assignment)
                return assignment.Left.Span.Contains(identifier.Span);
            if (node is PrefixUnaryExpressionSyntax prefix)
                return prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                    prefix.IsKind(SyntaxKind.PreDecrementExpression);
            if (node is PostfixUnaryExpressionSyntax postfix)
                return postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                    postfix.IsKind(SyntaxKind.PostDecrementExpression);
            if (node is ArgumentSyntax argument)
            {
                return argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
                    argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword);
            }
            if (node is StatementSyntax or BlockSyntax or MethodDeclarationSyntax)
                break;
        }
        return false;
    }

    private static bool IsStaticMethod(SyntaxNode methodLike)
        => methodLike switch
        {
            MethodDeclarationSyntax method => method.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.StaticKeyword)),
            LocalFunctionStatementSyntax local => local.Modifiers.Any(modifier =>
                modifier.IsKind(SyntaxKind.StaticKeyword)),
            _ => false,
        };

    private static bool IsUnusableInstanceMemberFromStaticMethod(ISymbol symbol)
        => symbol switch
        {
            IFieldSymbol field => !field.IsStatic,
            IPropertySymbol property => !property.IsStatic,
            IEventSymbol @event => !@event.IsStatic,
            IMethodSymbol method => !method.IsStatic &&
                method.MethodKind is not MethodKind.LocalFunction,
            _ => false,
        };

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static string BuildInvocation(
        string methodName,
        IReadOnlyList<InputParameter> parameters,
        bool returnsValue)
    {
        var arguments = string.Join(", ", parameters.Select(parameter => parameter.Modifier + parameter.Name));
        var call = $"{methodName}({arguments})";
        return returnsValue ? $"return {call};" : $"{call};";
    }

    private static string? BuildGeneratedMethod(
        SourceText source,
        TypeDeclarationSyntax type,
        SyntaxNode methodLike,
        IReadOnlyList<StatementSyntax> selectedStatements,
        string methodName,
        IReadOnlyList<InputParameter> parameters,
        bool returnsValue)
    {
        var first = selectedStatements[0];
        var last = selectedStatements[^1];
        var firstLine = source.Lines.GetLineFromPosition(first.SpanStart);
        var firstIndent = source.ToString(TextSpan.FromBounds(firstLine.Start, first.SpanStart));
        if (firstIndent.Any(c => !char.IsWhiteSpace(c))) return null;

        var body = source.ToString(TextSpan.FromBounds(first.SpanStart, last.Span.End));
        var bodyLines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var normalized = bodyLines.Select((line, index) =>
        {
            if (index == 0) return line;
            return line.StartsWith(firstIndent, StringComparison.Ordinal)
                ? line[firstIndent.Length..]
                : line.TrimStart();
        });
        var bodyText = string.Join("\n", normalized);
        var returnType = returnsValue ? ReturnType(methodLike) : "void";
        if (returnType is null) return null;

        var modifiers = methodLike switch
        {
            BaseMethodDeclarationSyntax method when method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                => "private static",
            LocalFunctionStatementSyntax local when local.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                => "private static",
            _ => "private",
        };
        var parameterText = string.Join(", ", parameters.Select(parameter =>
            $"{parameter.Modifier}{parameter.Type} {parameter.Name}"));
        return $"{modifiers} {returnType} {methodName}({parameterText})\n{{\n    {bodyText.Replace("\n", "\n    ", StringComparison.Ordinal)}\n}}";
    }

    private static string? ReturnType(SyntaxNode methodLike)
        => methodLike switch
        {
            MethodDeclarationSyntax method => method.ReturnType.ToString(),
            LocalFunctionStatementSyntax local => local.ReturnType.ToString(),
            _ => null,
        };

    private static bool ContainsUnsupportedControlFlow(StatementSyntax statement)
        => statement.DescendantNodesAndSelf().Any(node => node is BreakStatementSyntax
            or ContinueStatementSyntax
            or GotoStatementSyntax);

    private static bool IsMethodLike(SyntaxNode node)
        => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax;

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
        return new LspRange(
            new LspPosition(start.Line, start.Character),
            new LspPosition(end.Line, end.Character));
    }

    private static string FindMemberIndent(SourceText source, TypeDeclarationSyntax type, string closeIndent)
    {
        var member = type.Members.FirstOrDefault();
        if (member is null) return closeIndent + "    ";
        var line = source.Lines.GetLineFromPosition(member.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, member.SpanStart));
        return prefix.All(char.IsWhiteSpace) ? prefix : closeIndent + "    ";
    }

    private static string IndentMember(string generated, string memberIndent, string newline)
        => string.Join(newline, generated.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').Select(line => memberIndent + line));

    private static string ParameterModifier(ParameterSyntax parameter)
        => parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)) ? "ref "
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)) ? "out "
            : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)) ? "in "
            : "";

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);

    private sealed record InputParameter(
        string Name,
        string Type,
        string Modifier,
        ISymbol? Symbol = null);
}
