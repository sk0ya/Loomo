using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Git;

/// <summary>コミットが触れたファイル1件（<c>git show --numstat</c> の1行）。</summary>
/// <param name="DisplayPath">git が書いた綴り。リネームは <c>src/{Old.cs =&gt; New.cs}</c> の形で残る。</param>
/// <param name="Path">実際に開く相対パス（リネームは変更後）。</param>
/// <param name="Added">追加行数。バイナリなら null。</param>
/// <param name="Deleted">削除行数。バイナリなら null。</param>
public readonly record struct CommitFileStat(string DisplayPath, string Path, int? Added, int? Deleted)
{
    public bool IsBinary => Added is null || Deleted is null;

    /// <summary>一覧の右端に出す増減。狭い列に収まるよう記号だけの短い綴りにする。</summary>
    public string ChurnLabel => IsBinary ? "Bin" : $"+{Added} -{Deleted}";

    public bool IsRenamed => DisplayPath != Path;
}

/// <summary>
/// <c>git show --numstat --format=</c><see cref="Format"/> の出力を、表示用の見出し
/// （<see cref="Header"/>）と変更ファイル一覧（<see cref="Files"/>）に分ける純ロジック。
///
/// <para><c>--stat</c> ではなく <c>--numstat</c> を読むのは、<c>--stat</c> が幅に合わせてパスを
/// <c>.../Views/Foo.xaml</c> と省略してしまい、フォルダ構造に組み直せないため。</para>
///
/// <para>見出しは<b>コミットコメントが先</b>で、ハッシュ・作者・日時はその下の1行にまとめる。
/// 表示場所がグラフの右の細い縦列なので、<c>--format=fuller</c> の "AuthorDate: ..." の並びは
/// 何行にも折り返して肝心のコメントを画面外へ押し出してしまう。コミッターが作者と違うとき
/// （リベース・チェリーピック）だけ、もう1行足す。</para>
/// </summary>
public sealed partial record CommitSummary(string Header, IReadOnlyList<CommitFileStat> Files)
{
    /// <summary>レコードの区切り。コミット本文には現れない制御文字（US = %x1f）を使う。</summary>
    private const char Separator = '\u001f';

    /// <summary><c>git show --format=</c> に渡す書式。並び順は <see cref="BuildHeader"/> と対で決まる。</summary>
    public const string Format = "%h%x1f%an%x1f%ad%x1f%cn%x1f%cd%x1f%s%x1f%B";

    public static CommitSummary Empty { get; } = new("", Array.Empty<CommitFileStat>());

    /// <summary>「追加行数 TAB 削除行数 TAB パス」。バイナリは数値の代わりに <c>-</c>。</summary>
    [GeneratedRegex(@"^(?<add>\d+|-)\t(?<del>\d+|-)\t(?<path>.+)$")]
    private static partial Regex NumstatRegex();

    public static CommitSummary Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Empty;

        var record = new List<string>();
        var files = new List<CommitFileStat>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var match = NumstatRegex().Match(line);
            if (match.Success)
            {
                var display = Unquote(match.Groups["path"].Value.Trim());
                files.Add(new CommitFileStat(
                    display,
                    ResolveRenameTarget(display),
                    ParseCount(match.Groups["add"].Value),
                    ParseCount(match.Groups["del"].Value)));
            }
            // 一覧が始まったあとの行（末尾の空行など）は見出しに混ぜない。本文は改行を含むので、
            // 一覧より前は行のままではなく、つなぎ直してから区切り文字で割る。
            else if (files.Count == 0)
            {
                record.Add(line);
            }
        }

        return new CommitSummary(BuildHeader(string.Join('\n', record)), files);
    }

    private static int? ParseCount(string value) => int.TryParse(value, out var n) ? n : null;

    /// <summary>
    /// 1レコード（<see cref="Format"/>）を表示用の見出しへ組み直す。区切りが揃わない出力
    /// （書式を通していない生の <c>git show</c> や、エラーメッセージ）は前後の空白だけ落として素通しする。
    /// </summary>
    private static string BuildHeader(string record)
    {
        var fields = record.Split(Separator);
        if (fields.Length < 7) return record.Trim('\n', ' ');

        var shortHash = fields[0].Trim('\n', ' ');
        var (author, authorDate) = (fields[1], fields[2]);
        var (committer, committerDate) = (fields[3], fields[4]);
        // %B（本文まるごと）は件名で始まるので、件名 %s は %B が空のときの保険としてだけ使う。
        var message = fields[6].Trim('\n', ' ');
        if (message.Length == 0) message = fields[5].Trim();

        var builder = new StringBuilder(message);
        builder.Append('\n').Append('\n');
        builder.Append(shortHash).Append("  ").Append(author).Append("  ").Append(authorDate);
        if (committer != author || committerDate != authorDate)
            builder.Append('\n').Append("コミット: ").Append(committer).Append("  ").Append(committerDate);
        return builder.ToString();
    }

    private static string Unquote(string path) =>
        path.Length >= 2 && path[0] == '"' && path[^1] == '"' ? path[1..^1] : path;

    /// <summary>リネーム表記から変更後のパスを取り出す（<c>a/{b =&gt; c}/d.cs</c> と <c>a.cs =&gt; b.cs</c> の両形）。</summary>
    public static string ResolveRenameTarget(string path)
    {
        var open = path.IndexOf('{');
        if (open >= 0)
        {
            var arrow = path.IndexOf("=>", open, StringComparison.Ordinal);
            var close = arrow >= 0 ? path.IndexOf('}', arrow) : -1;
            if (close < 0) return path;
            var newPart = path[(arrow + 2)..close].Trim();
            return (path[..open] + newPart + path[(close + 1)..]).Replace("//", "/");
        }

        var flat = path.IndexOf("=>", StringComparison.Ordinal);
        return flat >= 0 ? path[(flat + 2)..].Trim() : path;
    }
}
