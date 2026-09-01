using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>Roslynの引数バインディングからC#のparameter name inlay hintを作る。</summary>
public static class CSharpParameterNameHintService
{
    /// <summary>指定行範囲の呼び出し引数へ、意味モデルに基づくparameter name hintを返す。</summary>
    public static IReadOnlyList<InlayHint> Get(
        SolutionModel? solution,
        string filePath,
        string source,
        int startLine,
        int endLine,
        IReadOnlyDictionary<string, string>? openTexts = null)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(source))
            return [];

        var context = CSharpWorkspaceOperationContext.Create(
            solution, filePath, source,
            includeSemanticCompilation: true,
            openTexts: openTexts);
        var compilation = context.SemanticCompilation;
        if (compilation is null) return [];

        var fullPath = Path.GetFullPath(filePath);
        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(Path.GetFullPath(candidate.FilePath ?? ""), fullPath,
                StringComparison.OrdinalIgnoreCase));
        if (tree is null) return [];

        var text = tree.GetText();
        var firstLine = Math.Clamp(startLine, 0, Math.Max(0, text.Lines.Count - 1));
        var lastLine = Math.Clamp(endLine, firstLine, Math.Max(0, text.Lines.Count - 1));
        var firstPosition = text.Lines[firstLine].Start;
        var lastPosition = text.Lines[lastLine].End;
        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
        var hints = new List<InlayHint>();

        foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetOperation(invocation) is IInvocationOperation operation)
                AddArguments(operation.Arguments, text, firstPosition, lastPosition, hints);
        }

        foreach (var creation in tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (model.GetOperation(creation) is IObjectCreationOperation operation)
                AddArguments(operation.Arguments, text, firstPosition, lastPosition, hints);
        }

        return hints
            .OrderBy(hint => hint.Position.Line)
            .ThenBy(hint => hint.Position.Character)
            .ToArray();
    }

    private static void AddArguments(
        IReadOnlyList<IArgumentOperation> arguments,
        SourceText text,
        int firstPosition,
        int lastPosition,
        ICollection<InlayHint> hints)
    {
        foreach (var argument in arguments)
        {
            if (argument.Parameter is null || argument.Syntax is not ArgumentSyntax syntax ||
                syntax.NameColon is not null)
                continue;

            var position = syntax.Expression?.SpanStart ?? syntax.SpanStart;
            if (position < firstPosition || position > lastPosition) continue;

            var linePosition = text.Lines.GetLinePosition(position);
            hints.Add(new InlayHint(
                new LspPosition(linePosition.Line, linePosition.Character),
                argument.Parameter.Name + ":",
                InlayHintKind.Parameter));
        }
    }
}
