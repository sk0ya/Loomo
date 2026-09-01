using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>単一のprivateメソッドを、その唯一の呼び出し箇所へインライン化する。
/// 意味モデルが渡された場合はoverloadと動的dispatchをsymbol identityで解決し、単一メソッド・
/// 単一呼び出し・単純な1文本体に限定する。失敗時はWorkspaceEditを返さない。</summary>
public static class CSharpInlineMethodService
{
    public static CSharpCodeGenerationResult Inline(
        string filePath,
        string sourceText,
        LspRange selection)
        => InlineCore(filePath, sourceText, selection, semanticCompilation: null);

    internal static CSharpCodeGenerationResult Inline(
        string filePath,
        string sourceText,
        LspRange selection,
        CSharpCompilation semanticCompilation)
        => InlineCore(filePath, sourceText, selection, semanticCompilation);

    private static CSharpCodeGenerationResult InlineCore(
        string filePath,
        string sourceText,
        LspRange selection,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみメソッドのインライン化を実行できます。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var selectedMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.Span == selectedSpan);
        var selectedInvocation = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation => NameSpan(invocation.Expression) == selectedSpan);
        var anchor = (SyntaxNode?)selectedMethod ?? selectedInvocation;
        if (anchor is null)
            return Failed("インライン化するメソッド名全体を選択してください。");

        var type = anchor.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null)
            return Failed("型の中にあるメソッドを選択してください。");

        var semanticModel = semanticCompilation is { } compilation
            ? CSharpSemanticCompilation.ForFile(compilation, filePath)
            : null;
        var selectedInvocationSymbol = selectedInvocation is not null && semanticModel is not null
            ? FindEquivalent(selectedInvocation, semanticModel) is { } equivalentInvocation
                ? semanticModel.GetSymbolInfo(equivalentInvocation).Symbol as IMethodSymbol
                : null
            : null;
        var selectedMethodSymbol = selectedMethod is not null && semanticModel is not null
            ? FindEquivalent(selectedMethod, semanticModel) is { } equivalentMethod
                ? semanticModel.GetDeclaredSymbol(equivalentMethod) as IMethodSymbol
                : null
            : null;

        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => string.Equals(
                method.Identifier.ValueText,
                selectedMethod?.Identifier.ValueText ?? InvocationName(selectedInvocation!),
                StringComparison.Ordinal))
            .ToList();
        if (methods.Count == 0)
            return Failed("インライン化するメソッドを型の中で解決できません。");
        var selectedSymbol = selectedInvocationSymbol ?? selectedMethodSymbol;
        if (semanticModel is not null && selectedSymbol is not null)
        {
            methods = methods.Where(method =>
            {
                var equivalent = FindEquivalent(method, semanticModel);
                return equivalent is not null &&
                    semanticModel.GetDeclaredSymbol(equivalent) is IMethodSymbol methodSymbol &&
                    IsSameMethod(methodSymbol, selectedSymbol);
            }).ToList();
        }
        if (methods.Count != 1)
            return Failed("overloadがあるため、インライン化するメソッドを一意に解決できません。");
        MethodDeclarationSyntax target;
        if (selectedMethod is not null)
        {
            target = selectedMethod;
        }
        else
        {
            target = methods[0];
        }

        var targetSymbol = semanticModel is not null &&
            FindEquivalent(target, semanticModel) is { } equivalentTarget
            ? semanticModel.GetDeclaredSymbol(equivalentTarget) as IMethodSymbol
            : null;
        if (semanticCompilation is not null && targetSymbol is null)
            return Failed("対象メソッドをC#の意味モデルから解決できません。");

        if (target.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)
                || modifier.IsKind(SyntaxKind.ProtectedKeyword)
                || modifier.IsKind(SyntaxKind.InternalKeyword)))
            return Failed("privateメソッドだけをインライン化できます。");
        if (target.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)
                || modifier.IsKind(SyntaxKind.RefKeyword)
                || modifier.IsKind(SyntaxKind.PartialKeyword))
            || target.TypeParameterList is not null
            || target.AttributeLists.Count > 0)
            return Failed("async・ref・generic・属性付きメソッドは安全にインライン化できません。");
        if (target.ParameterList.Parameters.Any(parameter =>
                parameter.Modifiers.Any(modifier => modifier.Kind() is
                    SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword
                    or SyntaxKind.ParamsKeyword)))
            return Failed("ref・out・in・params引数を持つメソッドはインライン化できません。");

        var body = ReadBody(target);
        if (body.Expression is null)
            return Failed(body.Error ?? "本体が単一のreturnまたは式文ではありません。");
        if (body.Expression.DescendantNodesAndSelf().OfType<AnonymousFunctionExpressionSyntax>().Any())
            return Failed("ラムダ式を含むメソッドはインライン化できません。");

        var calls = type.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => ReferenceEquals(
                invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault(), type))
            .Where(invocation => string.Equals(
                InvocationName(invocation), target.Identifier.ValueText, StringComparison.Ordinal))
            .Where(invocation => semanticModel is null ||
                targetSymbol is not null &&
                FindEquivalent(invocation, semanticModel) is { } equivalentInvocation &&
                IsSameMethod(semanticModel.GetSymbolInfo(equivalentInvocation), targetSymbol))
            .ToList();
        if (calls.Count != 1)
            return Failed("呼び出しが1箇所だけのprivateメソッドに限定しています。");
        var call = calls[0];
        if (selectedInvocation is not null && !ReferenceEquals(selectedInvocation, call))
            return Failed("選択した呼び出しを対象メソッドとして解決できません。");
        if (call.Expression is MemberAccessExpressionSyntax member
            && member.Expression is not ThisExpressionSyntax)
            return Failed("別インスタンス経由の呼び出しはインライン化できません。");

        var otherReferences = type.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(identifier => semanticModel is null
                ? string.Equals(identifier.Identifier.ValueText,
                    target.Identifier.ValueText, StringComparison.Ordinal)
                : FindEquivalent(identifier, semanticModel) is { } equivalentIdentifier &&
                  targetSymbol is not null &&
                  IsSameMethod(semanticModel.GetSymbolInfo(equivalentIdentifier), targetSymbol))
            .Where(identifier => !IsInvocationName(identifier))
            .ToList();
        if (otherReferences.Count > 0)
            return Failed("メソッドグループやnameof等の参照があるため削除できません。");

        if (call.ArgumentList.Arguments.Any(argument =>
                argument.NameColon is not null || argument.RefKindKeyword.RawKind != 0))
            return Failed("名前付き・ref・out引数を持つ呼び出しはインライン化できません。");
        if (!TryBindArguments(target, call, out var arguments, out var argumentError))
            return Failed(argumentError!);

        var parameterNames = target.ParameterList.Parameters
            .Select(parameter => parameter.Identifier.ValueText)
            .ToArray();
        var occurrences = parameterNames.ToDictionary(
            name => name, _ => 0, StringComparer.Ordinal);
        foreach (var identifier in body.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (occurrences.ContainsKey(identifier.Identifier.ValueText)
                && IsSimpleReference(identifier))
                occurrences[identifier.Identifier.ValueText]++;
        }
        for (var i = 0; i < arguments.Count; i++)
        {
            if (occurrences[parameterNames[i]] > 1 && !IsRepeatable(arguments[i]))
                return Failed("引数が複数回評価されるため、安全にインライン化できません。");
        }

        var substituted = Substitute(body.Expression, parameterNames, arguments, out var substituteError);
        if (substituteError is not null)
            return Failed(substituteError);
        var edits = new List<LspTextEdit>
        {
            new(ToLspRange(source, call.Span), substituted!),
            new(RemovalRange(source, target), ""),
        };
        var uri = LspUri.FromPath(Path.GetFullPath(filePath));
        return new CSharpCodeGenerationResult(
            new LspWorkspaceEdit(new Dictionary<string, IReadOnlyList<LspTextEdit>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [uri] = edits,
            }),
            "メソッド「" + target.Identifier.ValueText + "」をインライン化");
    }

    private static bool IsSameMethod(SymbolInfo info, IMethodSymbol target)
        => info.Symbol is IMethodSymbol method && IsSameMethod(method, target) ||
            info.CandidateSymbols.OfType<IMethodSymbol>().Any(method =>
                IsSameMethod(method, target));

    private static bool IsSameMethod(IMethodSymbol left, IMethodSymbol right)
        => SymbolEqualityComparer.Default.Equals(left, right) ||
            SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);

    private static T? FindEquivalent<T>(T node, SemanticModel semanticModel)
        where T : SyntaxNode
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static (ExpressionSyntax? Expression, string? Error) ReadBody(
        MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody is { Expression: { } expression })
            return (expression, null);
        if (method.Body is not { Statements.Count: 1 } body)
            return (null, null);
        return body.Statements[0] switch
        {
            ReturnStatementSyntax { Expression: { } returnExpression } => (returnExpression, null),
            ExpressionStatementSyntax expressionStatement => (expressionStatement.Expression, null),
            _ => (null, null),
        };
    }

    private static bool TryBindArguments(
        MethodDeclarationSyntax method,
        InvocationExpressionSyntax call,
        out IReadOnlyList<string> arguments,
        out string? error)
    {
        var values = call.ArgumentList.Arguments
            .Select(argument => argument.Expression.ToString())
            .ToList();
        if (values.Count > method.ParameterList.Parameters.Count)
        {
            arguments = [];
            error = "呼び出し側の引数が多すぎます。";
            return false;
        }
        for (var i = values.Count; i < method.ParameterList.Parameters.Count; i++)
        {
            var parameter = method.ParameterList.Parameters[i];
            if (parameter.Default?.Value is not { } defaultValue)
            {
                arguments = [];
                error = "省略された引数に既定値がありません。";
                return false;
            }
            values.Add(defaultValue.ToString());
        }
        arguments = values;
        error = null;
        return true;
    }

    private static string? Substitute(
        ExpressionSyntax expression,
        IReadOnlyList<string> parameterNames,
        IReadOnlyList<string> arguments,
        out string? error)
    {
        var text = expression.ToString();
        var replacements = new List<(int Start, int Length, string Text)>();
        foreach (var identifier in expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var index = -1;
            for (var i = 0; i < parameterNames.Count; i++)
            {
                if (string.Equals(parameterNames[i], identifier.Identifier.ValueText, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0 || !IsSimpleReference(identifier)) continue;
            if (identifier.Ancestors().OfType<InvocationExpressionSyntax>().Any(invocation =>
                    invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }))
            {
                error = "nameof内の引数はインライン化できません。";
                return null;
            }
            replacements.Add((
                identifier.SpanStart - expression.SpanStart,
                identifier.Span.Length,
                "(" + arguments[index] + ")"));
        }
        foreach (var replacement in replacements.OrderByDescending(item => item.Start))
            text = text[..replacement.Start] + replacement.Text
                + text[(replacement.Start + replacement.Length)..];
        error = null;
        return text;
    }

    private static bool IsSimpleReference(IdentifierNameSyntax identifier)
        => !(identifier.Parent is MemberAccessExpressionSyntax member
            && ReferenceEquals(member.Name, identifier));

    private static bool IsInvocationName(IdentifierNameSyntax identifier)
        => (identifier.Parent is InvocationExpressionSyntax invocation
            && ReferenceEquals(invocation.Expression, identifier))
            || (identifier.Parent is MemberAccessExpressionSyntax member
            && ReferenceEquals(member.Name, identifier)
            && member.Parent is InvocationExpressionSyntax);

    private static bool IsRepeatable(string expression)
    {
        var root = CSharpSyntaxTree.ParseText("class C { void M() { var x = "
            + expression + "; } }").GetRoot();
        var value = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault()?.Initializer?.Value;
        return value is LiteralExpressionSyntax
            or IdentifierNameSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax;
    }

    private static TextSpan NameSpan(ExpressionSyntax expression)
        => expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Span,
            IdentifierNameSyntax identifier => identifier.Span,
            _ => expression.Span,
        };

    private static string InvocationName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => "",
        };

    private static LspRange RemovalRange(SourceText source, MethodDeclarationSyntax method)
    {
        var firstLine = source.Lines.GetLineFromPosition(method.SpanStart);
        var lastLine = source.Lines.GetLineFromPosition(method.Span.End);
        var prefix = source.ToString(TextSpan.FromBounds(firstLine.Start, method.SpanStart));
        var suffix = source.ToString(TextSpan.FromBounds(method.Span.End, lastLine.End));
        if (prefix.All(char.IsWhiteSpace) && suffix.All(char.IsWhiteSpace))
        {
            var end = lastLine.EndIncludingLineBreak;
            return end > lastLine.End
                ? new LspRange(
                    new LspPosition(firstLine.LineNumber, 0),
                    new LspPosition(lastLine.LineNumber + 1, 0))
                : new LspRange(
                    new LspPosition(firstLine.LineNumber, 0),
                    new LspPosition(lastLine.LineNumber, lastLine.Span.Length));
        }
        return ToLspRange(source, method.Span);
    }

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

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);
}
