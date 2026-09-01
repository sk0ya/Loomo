using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>単一のトップレベル型を新しい .cs ファイルへ移動する。
/// using と名前空間は移動先へ再構成し、元ファイルの型本文削除と新規ファイル作成を
/// 1つのWorkspaceEditにまとめる。入れ子型・partial・既存移動先は安全側で拒否する。</summary>
public static class CSharpMoveTypeToFileService
{
    public static CSharpCodeGenerationResult Move(
        string filePath,
        string sourceText,
        LspRange selection,
        string destinationFilePath)
        => MoveCore(filePath, sourceText, selection, destinationFilePath,
            semanticCompilation: null);

    internal static CSharpCodeGenerationResult Move(
        string filePath,
        string sourceText,
        LspRange selection,
        string destinationFilePath,
        CSharpCompilation semanticCompilation)
        => MoveCore(filePath, sourceText, selection, destinationFilePath,
            semanticCompilation);

    private static CSharpCodeGenerationResult MoveCore(
        string filePath,
        string sourceText,
        LspRange selection,
        string destinationFilePath,
        CSharpCompilation? semanticCompilation)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("C# ファイルでのみ型の移動を実行できます。");
        if (!string.Equals(Path.GetExtension(destinationFilePath), ".cs", StringComparison.OrdinalIgnoreCase))
            return Failed("移動先は .cs ファイルにしてください。");

        var sourcePath = Path.GetFullPath(filePath);
        var destinationPath = Path.GetFullPath(destinationFilePath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            return Failed("移動元と移動先が同じファイルです。");
        if (File.Exists(destinationPath))
            return Failed("移動先ファイルが既に存在します。");
        if (Path.GetDirectoryName(destinationPath) is not { } directory
            || !Directory.Exists(directory))
            return Failed("移動先フォルダーが存在しません。");

        var source = SourceText.From(sourceText);
        if (!TryGetSelectionSpan(source, selection, out var selectedSpan))
            return Failed("選択範囲が文書の範囲外です。");

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.Span == selectedSpan);
        if (type is null)
            return Failed("移動する型名全体を選択してください。");
        if (type.Parent is not CompilationUnitSyntax
            and not BaseNamespaceDeclarationSyntax)
            return Failed("入れ子型はこの操作では移動できません。");
        if (type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
            return Failed("partial型は全パーツをまとめて移動してください。");
        if (type.CloseBraceToken.IsMissing)
            return Failed("型の本文が完了していません。");

        if (semanticCompilation is { } compilation)
        {
            var semanticModel = CSharpSemanticCompilation.ForFile(compilation, sourcePath);
            var semanticType = semanticModel is null
                ? null
                : FindEquivalentType(type, semanticModel);
            var semanticSymbol = semanticModel is null || semanticType is null
                ? null
                : semanticModel.GetDeclaredSymbol(semanticType) as INamedTypeSymbol;
            if (semanticModel is null || semanticSymbol is null)
                return Failed("移動する型をC#の意味モデルから解決できません。");
            if (semanticSymbol.ContainingType is not null || semanticSymbol.IsImplicitlyDeclared)
                return Failed("入れ子または暗黙的に生成された型は移動できません。");
            if (!semanticSymbol.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == semanticModel.SyntaxTree &&
                    reference.Span == type.Span))
                return Failed("移動する型の宣言位置をC#の意味モデルで確認できません。");
        }

        var namespaceName = string.Join(".", type.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(namespaceNode => namespaceNode.Name.ToString()));
        var newline = source.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var movedText = BuildMovedText(root, type, namespaceName, newline);
        var sourceRange = RemovalRange(source, type);
        var sourceUri = LspUri.FromPath(sourcePath);
        var destinationUri = LspUri.FromPath(destinationPath);
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase)
            {
                [sourceUri] = [new LspTextEdit(sourceRange, "")],
                [destinationUri] =
                [
                    new LspTextEdit(
                        new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                        movedText),
                ],
            },
            FileOperations:
            [
                new LspFileOperation(LspFileOperationKind.Create, destinationUri),
            ]);
        return new CSharpCodeGenerationResult(
            edit, $"型「{type.Identifier.ValueText}」を別ファイルへ移動");
    }

    private static string BuildMovedText(
        CompilationUnitSyntax root,
        TypeDeclarationSyntax type,
        string namespaceName,
        string newline)
    {
        var parts = new List<string>();
        var externs = root.Externs.ToFullString().TrimEnd();
        var usings = root.Usings.ToFullString().TrimEnd();
        if (externs.Length > 0) parts.Add(externs);
        if (usings.Length > 0) parts.Add(usings);
        if (namespaceName.Length > 0) parts.Add("namespace " + namespaceName + ";");
        parts.Add(type.ToFullString().Trim());
        return string.Join(newline + newline, parts) + newline;
    }

    private static LspRange RemovalRange(SourceText source, TypeDeclarationSyntax type)
    {
        var line = source.Lines.GetLineFromPosition(type.SpanStart);
        var prefix = source.ToString(TextSpan.FromBounds(line.Start, type.SpanStart));
        var singleLine = type.Span.End <= line.End;
        var suffix = singleLine
            ? source.ToString(TextSpan.FromBounds(type.Span.End, line.End))
            : "x";
        if (singleLine && prefix.All(char.IsWhiteSpace) && suffix.All(char.IsWhiteSpace))
        {
            var end = line.EndIncludingLineBreak;
            return end > line.End
                ? new LspRange(
                    new LspPosition(line.LineNumber, 0),
                    new LspPosition(line.LineNumber + 1, 0))
                : new LspRange(
                    new LspPosition(line.LineNumber, 0),
                    new LspPosition(line.LineNumber, line.Span.Length));
        }

        return ToLspRange(source, type.Span);
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

    private static T? FindEquivalentType<T>(T node, SemanticModel semanticModel)
        where T : TypeDeclarationSyntax
        => semanticModel.SyntaxTree.GetRoot().DescendantNodesAndSelf()
            .OfType<T>()
            .FirstOrDefault(candidate => candidate.RawKind == node.RawKind &&
                candidate.Span == node.Span);

    private static CSharpCodeGenerationResult Failed(string error)
        => new(null, "", error);
}
