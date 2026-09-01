using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Xml;
using System.Xml.Linq;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>LSPの署名応答が空／未接続のときに使うRoslynベースのC#署名支援。</summary>
public static class CSharpSignatureHelpService
{
    private static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithParameterOptions(SymbolDisplayParameterOptions.IncludeName |
                                  SymbolDisplayParameterOptions.IncludeType |
                                  SymbolDisplayParameterOptions.IncludeParamsRefOut);

    /// <summary>呼び出し位置のoverload、引数、documentationをLSP popup用モデルへ変換する。</summary>
    public static LspSignatureHelp? Get(
        SolutionModel? solution,
        string filePath,
        string source,
        int line,
        int character,
        IReadOnlyDictionary<string, string>? openTexts = null)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(source))
            return null;

        var context = CSharpWorkspaceOperationContext.Create(
            solution, filePath, source,
            includeSemanticCompilation: true,
            openTexts: openTexts);
        var compilation = context.SemanticCompilation;
        if (compilation is null) return null;

        var fullPath = Path.GetFullPath(filePath);
        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FilePath ?? ""), fullPath,
                StringComparison.OrdinalIgnoreCase));
        if (tree is null) return null;

        var text = tree.GetText();
        var position = ToPosition(text, line, character);
        var call = FindCallSite(tree.GetRoot(), position);
        if (call is null) return null;

        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
        var methods = ResolveMethods(model, call.Value).ToArray();
        if (methods.Length == 0) return null;

        var activeParameter = ResolveActiveParameter(call.Value.Arguments, position, methods[0]);
        var signatures = methods.Select(ToSignature).ToArray();
        return new LspSignatureHelp(signatures, 0, activeParameter);
    }

    private static IEnumerable<IMethodSymbol> ResolveMethods(
        SemanticModel model, CallSite call)
    {
        var symbols = new List<IMethodSymbol>();
        Add(model.GetSymbolInfo(call.Target).Symbol);
        foreach (var candidate in model.GetSymbolInfo(call.Target).CandidateSymbols)
            Add(candidate);

        if (call.Expression is not null)
        {
            foreach (var member in model.GetMemberGroup(call.Expression).OfType<IMethodSymbol>())
                Add(member);
        }

        return symbols;

        void Add(ISymbol? symbol)
        {
            if (symbol is IMethodSymbol method &&
                !symbols.Contains(method, SymbolEqualityComparer.Default))
                symbols.Add(method);
        }
    }

    private static LspSignatureInfo ToSignature(IMethodSymbol method)
    {
        var label = method.ToDisplayString(SignatureFormat);
        var parameters = method.Parameters
            .Select(parameter => new LspParameterInfo(parameter.ToDisplayString(SignatureFormat)))
            .ToArray();
        var documentation = GetDocumentation(method);
        return new LspSignatureInfo(label,
            string.IsNullOrWhiteSpace(documentation) ? null : documentation,
            parameters);
    }

    private static string? GetDocumentation(IMethodSymbol method)
    {
        var xml = method.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault()?.Value.Trim();
            return string.IsNullOrWhiteSpace(summary) ? xml : summary;
        }
        catch (XmlException)
        {
            return xml;
        }
    }

    private static int ResolveActiveParameter(
        ArgumentListSyntax arguments, int position, IMethodSymbol method)
    {
        var argumentIndex = arguments.Arguments
            .Select((argument, index) => (argument, index))
            .FirstOrDefault(item => item.argument.Span.Contains(position) ||
                                    item.argument.Span.End == position).index;
        if (argumentIndex == 0 && arguments.Arguments.Count > 0 &&
            !arguments.Arguments[0].Span.Contains(position) &&
            arguments.Arguments[0].Span.End < position)
        {
            argumentIndex = arguments.Arguments.Count(argument => argument.Span.End <= position);
        }

        if (arguments.Arguments.Count == 0)
            return 0;

        var argument = arguments.Arguments[Math.Clamp(argumentIndex,
            0, arguments.Arguments.Count - 1)];
        if (argument.NameColon?.Name.Identifier.ValueText is { Length: > 0 } name)
        {
            var named = -1;
            for (var i = 0; i < method.Parameters.Length; i++)
            {
                if (string.Equals(method.Parameters[i].Name, name, StringComparison.Ordinal))
                {
                    named = i;
                    break;
                }
            }
            if (named >= 0) return named;
        }

        return Math.Clamp(argumentIndex, 0, Math.Max(0, method.Parameters.Length - 1));
    }

    private static int ToPosition(SourceText text, int line, int character)
    {
        if (text.Lines.Count == 0) return 0;
        var safeLine = Math.Clamp(line, 0, text.Lines.Count - 1);
        var lineInfo = text.Lines[safeLine];
        return Math.Clamp(lineInfo.Start + Math.Max(0, character),
            lineInfo.Start, lineInfo.End);
    }

    private static CallSite? FindCallSite(SyntaxNode root, int position)
    {
        var candidates = root.DescendantNodes()
            .SelectMany(node => node switch
            {
                InvocationExpressionSyntax invocation =>
                    [new CallSite(invocation, invocation.ArgumentList, invocation.Expression)],
                ObjectCreationExpressionSyntax creation when creation.ArgumentList is not null =>
                    [new CallSite(creation, creation.ArgumentList, null)],
                ConstructorInitializerSyntax initializer =>
                    [new CallSite(initializer, initializer.ArgumentList, null)],
                _ => Array.Empty<CallSite>(),
            })
            .Where(call => call.Arguments.SpanStart <= position &&
                          position <= call.Arguments.Span.End)
            .OrderBy(call => call.Arguments.Span.Length)
            .ToArray();
        return candidates.FirstOrDefault();
    }

    private readonly record struct CallSite(
        SyntaxNode Target, ArgumentListSyntax Arguments, ExpressionSyntax? Expression);
}
