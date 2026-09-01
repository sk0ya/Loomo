using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>
/// 「メソッドの抽出」で言語サーバーが**勝手に付けた名前**（Roslyn なら <c>NewMethod</c>）を見つけ、
/// 適用前にユーザーの決めた名前へ差し替える。
///
/// <para>抽出は「切り出して名前を付ける」操作であって、<c>NewMethod</c> のまま置いていくのは
/// 操作の半分でしかない。Rider も VS も名前を先に訊く。ここは**適用前の編集テキストだけ**を
/// 書き換える——サーバーへ rename を投げ直すより速く、既存コードに同名の識別子があっても
/// 巻き込まない（触るのはこれから挿入する文字列だけ）。</para>
///
/// <para>名前の取り出しは C# の構文解析による。他言語では null を返し、呼び出し側は
/// 名前を訊かずにそのまま適用する（誤爆するくらいなら訊かない）。</para>
/// </summary>
public static class ExtractedSymbolName
{
    /// <summary>編集の中に現れる「新しく宣言されたメソッド／ローカル関数」の名前。見つからなければ null。</summary>
    public static string? Find(IEnumerable<LspTextEdit> edits)
    {
        foreach (var edit in edits)
        {
            var text = edit.NewText;
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (FindInFragment(text) is { Length: > 0 } name) return name;
        }
        return null;
    }

    /// <summary>workspace edit 全体から探す。</summary>
    public static string? Find(IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> changes)
        => Find(changes.SelectMany(pair => pair.Value));

    /// <summary>断片テキストから宣言名を取り出す。断片は本文の途中を差し替えるものなので、
    /// メンバー宣言としてもステートメントとしても解釈を試みる。</summary>
    internal static string? FindInFragment(string fragment)
    {
        var trimmed = fragment.Trim();
        if (trimmed.Length == 0) return null;

        // 「呼び出し側＋宣言」がひと続きで来ることがある（… } private void NewMethod() { … }）。
        // その場合は丸ごとをコンパイル単位として読むと拾える。
        foreach (var candidate in Candidates(trimmed))
        {
            if (candidate is MethodDeclarationSyntax method) return method.Identifier.ValueText;
            if (candidate is LocalFunctionStatementSyntax local) return local.Identifier.ValueText;
        }
        return null;
    }

    private static IEnumerable<Microsoft.CodeAnalysis.SyntaxNode> Candidates(string trimmed)
    {
        var member = SyntaxFactory.ParseMemberDeclaration(trimmed);
        if (member is not null) yield return member;

        var statement = SyntaxFactory.ParseStatement(trimmed);
        if (statement is not null) yield return statement;

        // 途中から始まる断片は上の2つでは読めない。前後を補ってクラス本体として読ませる。
        var wrapped = CSharpSyntaxTree.ParseText("class __Probe__ {" + trimmed + "}").GetRoot();
        foreach (var node in wrapped.DescendantNodes())
            if (node is MethodDeclarationSyntax or LocalFunctionStatementSyntax)
                yield return node;
    }

    /// <summary>編集テキスト中の <paramref name="oldName"/> を単語単位で <paramref name="newName"/> へ置き換える。
    /// 対象は**これから挿入する文字列だけ**なので、既存コードには一切触れない。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> Rename(
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> changes, string oldName, string newName)
    {
        if (oldName == newName || oldName.Length == 0 || newName.Length == 0) return changes;

        var pattern = new Regex($@"\b{Regex.Escape(oldName)}\b", RegexOptions.CultureInvariant);
        var result = new Dictionary<string, IReadOnlyList<LspTextEdit>>(LspUri.Comparer);
        foreach (var (uri, edits) in changes)
            result[uri] = [.. edits.Select(e => e with { NewText = pattern.Replace(e.NewText, newName) })];
        return result;
    }

    /// <summary>C# の識別子として使える名前か。ダイアログの手前で弾くため。</summary>
    public static bool IsValidIdentifier(string? name) =>
        name is { Length: > 0 } &&
        SyntaxFacts.IsValidIdentifier(name) &&
        !SyntaxFacts.IsReservedKeyword(SyntaxFacts.GetKeywordKind(name));
}
