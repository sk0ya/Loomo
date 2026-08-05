using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services.Refactoring;

/// <summary>リファクタリング項目の分類。Rider の「Refactor This」の並びに合わせてある。</summary>
public enum RefactoringGroup
{
    /// <summary>抽出（メソッド・変数・インターフェース・基底クラス・ファイル）。</summary>
    Extract,
    /// <summary>インライン化。</summary>
    Inline,
    /// <summary>移動。</summary>
    Move,
    /// <summary>書き換え（式の変換・カプセル化など）。</summary>
    Rewrite,
    /// <summary>kind を申告しないサーバーの項目。落とさずここへ集める。</summary>
    Other,
}

/// <summary>メニューに出す1項目。<paramref name="ServerTitle"/> は言語サーバーが返した原文で、
/// 日本語化した <paramref name="Title"/> が実物とずれていないかをその場で確かめられるよう
/// ツールチップに出す（訳を外して原文を隠さない）。</summary>
public sealed record RefactoringItem(
    LspCodeAction Action,
    string Title,
    string ServerTitle,
    RefactoringGroup Group);

/// <summary>言語サーバーが返した code action を、右クリックメニューの並びへ組み替える。
///
/// <para>UI から切り離してあるのは、ここが**サーバーごとに文言も kind もばらつく**唯一の箇所だから。
/// Roslyn は「Extract method」、typescript-language-server は「Extract to function in module scope」、
/// rust-analyzer は「Extract into function」と、同じ操作を別の文字列で返す。分類は kind を第一の
/// 根拠にし、kind が無いサーバーのために原文の照合を第二の根拠として持つ。</para></summary>
public static class RefactoringMenu
{
    /// <summary>code action 要求に載せる <c>context.only</c>。quick fix を混ぜないためにこれで絞る。</summary>
    public static readonly IReadOnlyList<string> RequestKinds = [LspCodeActionKinds.Refactor];

    private static readonly (RefactoringGroup Group, string Header)[] Order =
    [
        (RefactoringGroup.Extract, "抽出"),
        (RefactoringGroup.Inline, "インライン化"),
        (RefactoringGroup.Move, "移動"),
        (RefactoringGroup.Rewrite, "書き換え"),
        (RefactoringGroup.Other, "その他"),
    ];

    /// <summary>並び順つきのグループ見出し。</summary>
    public static IReadOnlyList<(RefactoringGroup Group, string Header)> Groups => Order;

    /// <summary>
    /// 取得した code action を、表示順に並べたグループへ分ける。
    /// <c>disabled</c> のアクション（サーバーが「この選択では使えない」と言っているもの）は落とす——
    /// Rider のメニューにも出ない類のもので、出すと押せない項目でメニューが埋まる。
    /// </summary>
    public static IReadOnlyList<(RefactoringGroup Group, string Header, IReadOnlyList<RefactoringItem> Items)>
        Build(IReadOnlyList<LspCodeAction> actions)
    {
        var items = actions
            .Where(a => a.DisabledReason is null && !string.IsNullOrWhiteSpace(a.Title))
            .Where(IsRefactoring)
            .Select(ToItem)
            .ToList();

        var result = new List<(RefactoringGroup, string, IReadOnlyList<RefactoringItem>)>();
        foreach (var (group, header) in Order)
        {
            var groupItems = items.Where(i => i.Group == group).ToList();
            if (groupItems.Count > 0) result.Add((group, header, groupItems));
        }
        return result;
    }

    /// <summary><c>only</c> を無視して quick fix まで返すサーバーがあるので、こちら側でも落とす。
    /// kind を一切申告しないサーバーは除外できないので通す（Other へ入る）。</summary>
    internal static bool IsRefactoring(LspCodeAction action) =>
        action.Kind is null ||
        (!LspCodeActionKinds.Matches(action.Kind, LspCodeActionKinds.QuickFix) &&
         !LspCodeActionKinds.Matches(action.Kind, LspCodeActionKinds.Source));

    internal static RefactoringItem ToItem(LspCodeAction action)
    {
        var serverTitle = action.Title.Trim();
        return new RefactoringItem(action, Localize(serverTitle), serverTitle, Classify(action));
    }

    /// <summary>kind を第一の根拠に分類する。kind の無いサーバー向けに原文でも判定する。</summary>
    internal static RefactoringGroup Classify(LspCodeAction action)
    {
        if (LspCodeActionKinds.Matches(action.Kind, LspCodeActionKinds.RefactorExtract))
            return RefactoringGroup.Extract;
        if (LspCodeActionKinds.Matches(action.Kind, LspCodeActionKinds.RefactorInline))
            return RefactoringGroup.Inline;
        if (LspCodeActionKinds.Matches(action.Kind, LspCodeActionKinds.RefactorMove))
            return RefactoringGroup.Move;
        if (LspCodeActionKinds.Matches(action.Kind, LspCodeActionKinds.RefactorRewrite))
            return RefactoringGroup.Rewrite;

        var title = action.Title;
        if (StartsWithAny(title, "Extract", "Introduce")) return RefactoringGroup.Extract;
        if (StartsWithAny(title, "Inline")) return RefactoringGroup.Inline;
        if (StartsWithAny(title, "Move", "Pull", "Push")) return RefactoringGroup.Move;
        return RefactoringGroup.Other;
    }

    private static bool StartsWithAny(string title, params string[] prefixes) =>
        prefixes.Any(p => title.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // 日本語化の対象は「どの言語サーバーでも語彙が安定している定番のリファクタリング」だけに絞る。
    // 前方一致で見て、残りは括弧で添える（"Introduce local for 'a + b'" → ローカル変数の導入（'a + b'））。
    // 一致しなければ**原文をそのまま出す**——当てずっぽうの訳より原文のほうが誤解が少ない。
    private static readonly (string English, string Japanese)[] TitleRules =
    [
        // Roslyn (C#)
        ("Extract base class", "基底クラスの抽出"),
        ("Extract interface", "インターフェースの抽出"),
        ("Extract local function", "ローカル関数の抽出"),
        ("Extract method", "メソッドの抽出"),
        ("Introduce constant for", "定数の導入"),
        ("Introduce field for", "フィールドの導入"),
        ("Introduce local for", "ローカル変数の導入"),
        ("Introduce parameter for", "パラメーターの導入"),
        ("Introduce query variable for", "クエリ変数の導入"),
        ("Inline temporary variable", "一時変数のインライン化"),
        ("Inline method", "メソッドのインライン化"),
        ("Inline call", "呼び出しのインライン化"),
        ("Move type to", "型を別ファイルへ移動"),
        ("Move to namespace", "名前空間へ移動"),
        ("Encapsulate field", "フィールドのカプセル化"),
        ("Pull", "基底型へ引き上げ"),
        ("Convert to", "変換"),
        // typescript-language-server / rust-analyzer
        ("Extract to function", "関数へ抽出"),
        ("Extract to constant", "定数へ抽出"),
        ("Extract to type alias", "型エイリアスへ抽出"),
        ("Extract to interface", "インターフェースへ抽出"),
        ("Extract into function", "関数へ抽出"),
        ("Extract into variable", "変数へ抽出"),
        ("Extract function", "関数の抽出"),
        ("Extract variable", "変数の抽出"),
        ("Extract constant", "定数の抽出"),
        ("Move to a new file", "新しいファイルへ移動"),
        ("Move to file", "ファイルへ移動"),
        ("Inline variable", "変数のインライン化"),
        ("Inline function", "関数のインライン化"),
    ];

    /// <summary>言語サーバーの原題を、定番のものだけ日本語へ寄せる。未知の題は原文のまま。</summary>
    internal static string Localize(string serverTitle)
    {
        var (core, ellipsis) = SplitEllipsis(serverTitle.Trim());

        foreach (var (english, japanese) in TitleRules)
        {
            if (!core.StartsWith(english, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = core[english.Length..].Trim();
            return rest.Length == 0
                ? japanese + ellipsis
                : $"{japanese}（{rest}）{ellipsis}";
        }
        return serverTitle.Trim();
    }

    /// <summary>末尾の「…」「...」は「この先にダイアログがある」という合図なので、訳のあとへ付け直す。</summary>
    private static (string Core, string Ellipsis) SplitEllipsis(string title)
    {
        if (title.EndsWith('…')) return (title[..^1].TrimEnd(), "…");
        if (title.EndsWith("...", StringComparison.Ordinal)) return (title[..^3].TrimEnd(), "…");
        return (title, "");
    }
}
