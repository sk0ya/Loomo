using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>
/// reflog の1件が「何をした記録か」。git は種類を構造化しては持たず、メッセージ（<c>%gs</c>）の
/// 先頭の動詞（<c>commit (amend):</c>／<c>checkout:</c>／<c>reset:</c>…）で書き分けているだけなので、
/// それを読み分けた結果（<see cref="GitReflogParser.Classify"/>）。
/// </summary>
public enum GitReflogKind
{
    Commit,
    Amend,
    Merge,
    Checkout,
    Reset,
    Rebase,
    Pull,
    CherryPick,
    Revert,
    Branch,
    Clone,
    Sync,
    Stash,
    Other,
}

/// <summary>
/// reflog の1件（<c>git log -g</c> の1レコード）。<see cref="Hash"/> はその操作の<b>あと</b>に ref が
/// 指したコミット、<see cref="PreviousHash"/> は操作の<b>前</b>（＝1つ古い記録の <see cref="Hash"/>）。
/// 前後が揃って初めて「この操作で何が変わったか」を差分で見せられる。
/// </summary>
/// <param name="RefName">どの ref の記録か（<c>HEAD</c>・<c>main</c> など）。</param>
/// <param name="Index">新しい順の番号（<c>HEAD@{3}</c> の 3）。</param>
/// <param name="Time">記録された時刻（コミットの日時ではない）。読めなければ null。</param>
/// <param name="Message">git が書いたままのメッセージ（<c>%gs</c>）。</param>
/// <param name="Description">人が読む説明（「main → feature に切り替え」など）。</param>
/// <param name="Subject">そのコミットの件名（<c>%s</c>）。</param>
public sealed record GitReflogEntry(
    string RefName,
    int Index,
    string Hash,
    string ShortHash,
    DateTimeOffset? Time,
    GitReflogKind Kind,
    string Message,
    string Description,
    string Subject,
    string Author)
{
    /// <summary>この操作の前に ref が指していたコミット。最古の記録（＝前が無い）と、読み込んだ範囲の
    /// 外にあって分からないときは null。</summary>
    public string? PreviousHash { get; init; }

    /// <summary>git に渡せる位置指定（<c>HEAD@{3}</c>）。ハッシュと違い「その時点の ref」を指す。</summary>
    public string Selector => $"{RefName}@{{{Index}}}";

    /// <summary>操作の前後で指すコミットが変わったか（同じ場所へのチェックアウトなどは変わらない）。</summary>
    public bool MovedCommit => PreviousHash is not null &&
        !string.Equals(PreviousHash, Hash, StringComparison.OrdinalIgnoreCase);
}

/// <summary>reflog の1ページ。<see cref="HasMore"/> は「これより古い記録がまだある」。</summary>
public sealed record GitReflogPage(IReadOnlyList<GitReflogEntry> Entries, bool HasMore, string? Error)
{
    public static GitReflogPage Empty { get; } = new(Array.Empty<GitReflogEntry>(), false, null);
}
