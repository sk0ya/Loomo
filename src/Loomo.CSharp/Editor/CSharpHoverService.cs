using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>LSPが未接続・空応答のときに表示するRoslynベースのC# symbol hover。</summary>
public static class CSharpHoverService
{
    private static readonly SymbolDisplayFormat DisplayFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType |
                               SymbolDisplayMemberOptions.IncludeParameters)
            .WithParameterOptions(SymbolDisplayParameterOptions.IncludeName |
                                  SymbolDisplayParameterOptions.IncludeType |
                                  SymbolDisplayParameterOptions.IncludeParamsRefOut);

    public static string? Get(
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
        var safeLine = Math.Clamp(line, 0, Math.Max(0, text.Lines.Count - 1));
        var lineText = text.Lines[safeLine];
        var offset = lineText.Start + Math.Clamp(character, 0, lineText.Span.Length);
        var root = tree.GetRoot();
        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: false);
        var symbol = Refactoring.CSharpSemanticSymbolResolver.FindSymbol(
            model, root, offset, CancellationToken.None);
        if (symbol is null) return null;

        var display = symbol.ToDisplayString(DisplayFormat);
        if (string.IsNullOrWhiteSpace(display)) return null;
        var documentation = GetDocumentation(symbol);
        return string.IsNullOrWhiteSpace(documentation)
            ? display
            : $"{display}{Environment.NewLine}{documentation}";
    }

    private static string? GetDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault()?.Value.Trim();
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
