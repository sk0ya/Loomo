using Editor.Core.Lsp;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Configuration;

/// <summary>本文中の <c>using</c> ディレクティブの位置を、<b>構文解析だけ</b>で数える。</summary>
/// <remarks>
/// <para>Roslyn の「不要な using」（IDE0005）は、<b>トークンが隣り合う</b>不要 using だけを一つの範囲へ
/// まとめる（<c>AbstractRemoveUnnecessaryImportsDiagnosticAnalyzer.GetContiguousSpans</c> は
/// <c>lastToken.GetNextToken() == node.GetFirstToken()</c> のときしか結合しない）。間に<b>必要な</b> using が
/// あればそこで範囲が切れるので、<b>グループ範囲の中にある using は全部不要</b>と言い切れる。
/// だから個別位置を出すのに意味解析は要らない。</para>
///
/// <para>これが要るのは、言語サーバーが繋がっている間は compiler フォールバック（CS8019 の個別位置を
/// 持っていた唯一の経路）を走らせないため。毎キー Compilation を作り直さずに個別表示を保つ。</para>
/// </remarks>
public static class CSharpUsingDirectiveRanges
{
    public static IReadOnlyList<LspRange> Find(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var text = tree.GetText();
        // 型やメソッドの中へは降りない。using ディレクティブは compilation unit と namespace の直下にしかない
        // （メソッド内の `using (...)` は UsingStatementSyntax で別物）。
        return root
            .DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Select(directive =>
            {
                var span = text.Lines.GetLinePositionSpan(directive.Span);
                return new LspRange(
                    new LspPosition(span.Start.Line, span.Start.Character),
                    new LspPosition(span.End.Line, span.End.Character));
            })
            .ToArray();
    }
}
