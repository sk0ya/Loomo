using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.CSharp.Refactoring;

internal static class CSharpSemanticSymbolResolver
{
    public static ISymbol? FindSymbol(
        SemanticModel model, SyntaxNode root, int offset, CancellationToken cancellationToken)
    {
        var node = root.FindToken(Math.Clamp(offset, 0, root.FullSpan.End)).Parent;
        for (var current = node; current is not null; current = current.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var declared = model.GetDeclaredSymbol(current, cancellationToken);
            if (declared is not null) return declared;
            var info = model.GetSymbolInfo(current, cancellationToken).Symbol;
            if (info is not null) return info;
            var type = model.GetTypeInfo(current, cancellationToken).Type;
            if (type is not null && type.TypeKind != TypeKind.Error) return type;
            var alias = model.GetAliasInfo(current, cancellationToken);
            if (alias is not null) return alias;
        }
        return null;
    }

    public static bool TryGetOffset(SourceText text, LspPosition position, out int offset)
    {
        offset = 0;
        if (position.Line < 0 || position.Line >= text.Lines.Count || position.Character < 0)
            return false;
        var line = text.Lines[position.Line];
        if (position.Character > line.Span.Length) return false;
        offset = line.Start + position.Character;
        return true;
    }
}
