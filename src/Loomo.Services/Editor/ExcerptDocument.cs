using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Buffer;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services;

/// <summary>抜粋にしたい箇所（ファイルと 0 始まりの行）。参照・grep・診断の一覧の1行に当たる。</summary>
public sealed record ExcerptRequest(string Path, int Line);

/// <summary>1つの抜粋の範囲（0 始まり、<see cref="EndLine"/> を含む）。</summary>
public sealed record ExcerptSpan(string Path, int StartLine, int EndLine);

/// <summary>抜粋タブの行の種類。見出しは守られ、本文は元ファイルの行に対応する。</summary>
public enum ExcerptLineKind { Header, Content }

/// <summary>抜粋タブのある行が何か。<see cref="SourceLine"/> は本文の行なら元ファイルの行（0 始まり）、見出しなら -1。</summary>
public readonly record struct ExcerptLine(ExcerptLineKind Kind, int Excerpt, int SourceLine);

/// <summary>抜粋タブでの編集を元ファイルへ入れる編集（<see cref="Edit"/> は元ファイルの座標）。
/// 元ファイルへ入れられたら <see cref="ExcerptDocument.Commit"/> でモデルへ確定する。</summary>
public sealed record ExcerptSourceEdit(string Path, LspTextEdit Edit)
{
    internal int Owner { get; init; }
    internal (int Line, int Column) LocalStart { get; init; }
    internal (int Line, int Column) LocalEnd { get; init; }
    internal TextBufferChange SourceChange { get; init; } = null!;
}

/// <summary>元ファイルの変更を抜粋タブへ写す編集（<see cref="Edit"/> は抜粋タブの座標）。</summary>
public sealed record ExcerptViewEdit(LspTextEdit Edit);

/// <summary>
/// 抜粋の集め方（設計書 §35.3）。行ごとの要求を前後 <c>context</c> 行で広げ、同じファイルで重なる・
/// 隣り合うものは1つにまとめる。ファイルの並びは最初に現れた順、ファイル内は行順。
/// </summary>
public static class ExcerptPlanner
{
    public static IReadOnlyList<ExcerptSpan> Plan(
        IEnumerable<ExcerptRequest> requests, Func<string, int> lineCount, int context = 2)
    {
        var spans = new List<ExcerptSpan>();
        foreach (var group in requests.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var count = Math.Max(1, lineCount(group.Key));
            ExcerptSpan? current = null;
            foreach (var line in group.Select(r => Math.Clamp(r.Line, 0, count - 1)).Distinct().Order())
            {
                var start = Math.Max(0, line - context);
                var end = Math.Min(count - 1, line + context);
                if (current is not null && start <= current.EndLine + 1)
                {
                    current = current with { EndLine = Math.Max(current.EndLine, end) };
                    continue;
                }
                if (current is not null) spans.Add(current);
                current = new ExcerptSpan(group.Key, start, end);
            }
            if (current is not null) spans.Add(current);
        }
        return spans;
    }
}

/// <summary>抜粋1つ。元ファイルでの位置は行の半開区間 [Start.Line, End.Line) のアンカーで持つ。</summary>
public sealed class Excerpt
{
    internal Excerpt(string path, int startLine, IReadOnlyList<string> lines)
    {
        Path = path;
        Lines = [.. lines];
        Source = new AnchoredRange(new TextPoint(startLine, 0), new TextPoint(startLine + lines.Count, 0), AnchorBias.Before);
    }

    public string Path { get; }

    /// <summary>元ファイルでの範囲（終わりは次の行の行頭＝前寄せ。直後の行への挿入は抜粋の外）。</summary>
    public AnchoredRange Source { get; }

    /// <summary>いま抜粋タブに出している本文の行。</summary>
    public List<string> Lines { get; private set; }

    internal void ReplaceLines(List<string> lines) => Lines = lines;
}

/// <summary>
/// 抜粋タブの文書（設計書 §35.3）。複数ファイルの抜粋を「見出し行＋本文の行」で1枚に並べ、
/// その行の対応・編集の守り（見出し行は書かせない）・抜粋タブ⇔元ファイルの編集の写し替えを受け持つ。
/// エディタにも WPF にも触れない純粋なモデルで、抜粋タブのエディタと元ファイルのタブを結ぶのはホスト。
/// <para>編集の写し方：抜粋タブの変更は、正確な差分（<see cref="TextBufferChange"/>）のまま元ファイルの座標へ
/// 写し、同じファイルの抜粋のアンカーも<b>その正確な編集で</b>動かす。元ファイル側がホストの編集を
/// 行単位の差分として通知し直したもの（<c>TryApplyLspTextEdits</c> は全文置換を行の差分で報告する）で動かすと、
/// 抜粋の境界の行がどちらに属するか曖昧になるため、自分で入れた編集の通知はホストが捨てる。</para>
/// </summary>
public sealed class ExcerptDocument
{
    private readonly List<Excerpt> _excerpts = [];
    private readonly Func<string, string> _header;

    // 各抜粋の見出し行の位置（本文の行数が変わるたびに捨てる）。ガターは描画のたびに全可視行で
    // Describe を呼ぶので、抜粋を頭から数え直さず二分探索で引く。
    private int[]? _headers;

    private int[] Headers
    {
        get
        {
            if (_headers is not null) return _headers;
            var headers = new int[_excerpts.Count];
            var line = 0;
            for (var i = 0; i < _excerpts.Count; i++)
            {
                headers[i] = line;
                line += 1 + _excerpts[i].Lines.Count;
            }
            return _headers = headers;
        }
    }

    /// <param name="spans">並べる抜粋（<see cref="ExcerptPlanner.Plan"/> の結果）。</param>
    /// <param name="readLines">ファイルの全行（開いているタブがあればその本文、無ければディスク）。</param>
    /// <param name="header">見出し行の文字列。行番号は入れない——ずれるたびに見出しを書き換えずに済むよう、行番号はガターで出す。</param>
    public ExcerptDocument(
        IEnumerable<ExcerptSpan> spans, Func<string, IReadOnlyList<string>> readLines, Func<string, string> header)
    {
        _header = header;
        foreach (var span in spans)
        {
            var lines = readLines(span.Path);
            var start = Math.Clamp(span.StartLine, 0, Math.Max(0, lines.Count - 1));
            var end = Math.Clamp(span.EndLine, start, Math.Max(0, lines.Count - 1));
            _excerpts.Add(new Excerpt(span.Path, start,
                lines.Count == 0 ? [""] : lines.Skip(start).Take(end - start + 1).ToList()));
        }
    }

    public IReadOnlyList<Excerpt> Excerpts => _excerpts;

    public IEnumerable<string> Paths => _excerpts.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>抜粋タブに出す本文（見出し行＋本文の行。"\n" 区切り）。</summary>
    public string Text => string.Join("\n", AllLines());

    public int LineCount => _excerpts.Sum(e => 1 + e.Lines.Count);

    /// <summary>抜粋 <paramref name="excerpt"/> の見出し行の位置。</summary>
    public int HeaderLine(int excerpt)
        => excerpt < _excerpts.Count ? Headers[excerpt] : LineCount;

    public ExcerptLine Describe(int line)
    {
        var headers = Headers;
        if (headers.Length == 0 || line < 0) return new ExcerptLine(ExcerptLineKind.Header, _excerpts.Count, -1);
        var index = Array.BinarySearch(headers, line);
        if (index >= 0) return new ExcerptLine(ExcerptLineKind.Header, index, -1);
        var owner = ~index - 1;
        var local = line - headers[owner] - 1;
        return local < _excerpts[owner].Lines.Count
            ? new ExcerptLine(ExcerptLineKind.Content, owner, _excerpts[owner].Source.Start.Line + local)
            : new ExcerptLine(ExcerptLineKind.Header, _excerpts.Count, -1);
    }

    private void ReplaceLines(Excerpt excerpt, List<string> lines)
    {
        excerpt.ReplaceLines(lines);
        _headers = null;
    }

    /// <summary>ガターに出す文字列：本文の行は元ファイルの行番号（1 始まり）、見出しは空欄。</summary>
    public string? LineLabel(int line)
        => Describe(line) is { Kind: ExcerptLineKind.Content } content ? (content.SourceLine + 1).ToString() : null;

    /// <summary>ガターの桁数（最も大きい元ファイルの行番号の桁数）。</summary>
    public int LabelWidth
        => _excerpts.Count == 0 ? 1 : _excerpts.Max(e => e.Source.End.Line).ToString().Length;

    /// <summary>
    /// エディタの編集ガード。見出し行に触れる編集と、2つの抜粋にまたがる編集を断る。判定は行の差分
    /// （前後で一致する行を除いた範囲）で行う——断ったものはエディタが巻き戻し、差分として外へは流れない。
    /// </summary>
    public string? Guard(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var min = Math.Min(before.Count, after.Count);
        var prefix = 0;
        while (prefix < min && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < min - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        var end = before.Count - suffix;

        if (prefix == end)
        {
            // 行を挿入しただけ：前後どちらかが本文なら、その抜粋への追記として通す。
            if (prefix > 0 && Describe(prefix - 1).Kind == ExcerptLineKind.Content) return null;
            if (prefix < before.Count && Describe(prefix).Kind == ExcerptLineKind.Content) return null;
            return ProtectedMessage;
        }

        var owner = -1;
        for (var line = prefix; line < end; line++)
        {
            var info = Describe(line);
            if (info.Kind != ExcerptLineKind.Content) return ProtectedMessage;
            if (owner >= 0 && info.Excerpt != owner) return "2つの抜粋にまたがる編集はできません。";
            owner = info.Excerpt;
        }
        return null;
    }

    public const string ProtectedMessage = "抜粋の見出し行は編集できません（本文の行だけが元のファイルに入ります）。";

    /// <summary>
    /// 抜粋タブの変更（抜粋タブの座標）を元ファイルへの編集に写す。<b>モデルはまだ変えない</b>——元ファイルへ
    /// 入れられたら <see cref="Commit"/> で確定する（入れられなかったのにモデルだけ進むと、抜粋タブと元ファイルが
    /// 食い違ったまま、以後の編集がずれた行へ入る）。写せない変更（見出しにかかる・2つの抜粋にまたがる）は
    /// null——ホストは <see cref="Text"/> で抜粋タブを作り直す。
    /// </summary>
    public ExcerptSourceEdit? TranslateEdit(TextBufferChange change)
    {
        if (change.Kind == TextBufferChangeKind.Reload) return null;
        var start = Describe(change.StartLine);
        int owner;
        var newText = change.NewText;
        var startLine = change.StartLine;
        var startColumn = change.StartColumn;
        if (start.Kind == ExcerptLineKind.Content)
        {
            owner = start.Excerpt;
        }
        else if (start.Excerpt < _excerpts.Count && newText.Length == 0 &&
                 change.StartColumn == _header(_excerpts[start.Excerpt].Path).Length &&
                 _excerpts[start.Excerpt].Lines.Count > 0 &&
                 change.EndLine == change.StartLine + _excerpts[start.Excerpt].Lines.Count &&
                 change.EndColumn == _excerpts[start.Excerpt].Lines[^1].Length)
        {
            // 文書の末尾にある抜粋の本文を全部消した（dd）：エディタは「前の行（＝見出し）の行末から」消したと
            // 報告する。元ファイルでは抜粋の行を改行ごと消す。
            var whole = _excerpts[start.Excerpt];
            var removed = new TextBufferChange(
                whole.Source.Start.Line, 0, whole.Source.End.Line, 0, "", change.Version);
            return new ExcerptSourceEdit(whole.Path, ToLsp(removed, ""))
            {
                Owner = start.Excerpt, LocalStart = (0, 0), LocalEnd = (whole.Lines.Count, 0), SourceChange = removed,
            };
        }
        else
        {
            // 見出し行の行頭への「行の挿入」（見出しの上に行を足す O など）は、前の抜粋の末尾への追記と同じ。
            // 元ファイルでは前の抜粋の最終行の行末に "\n…" を足す形に直す——そうしないと、抜粋の終わり
            // （次の行の行頭・前寄せ）ちょうどへの挿入になり、足した行が抜粋の外に出てしまう。
            if (change.StartColumn != 0 || change.EndLine != change.StartLine || change.EndColumn != 0 ||
                !newText.EndsWith('\n') || start.Excerpt == 0)
                return null;
            owner = start.Excerpt - 1;
            if (_excerpts[owner].Lines.Count == 0) return null;
            startLine = change.StartLine - 1;
            startColumn = _excerpts[owner].Lines[^1].Length;
            newText = "\n" + newText[..^1];
        }

        var excerpt = _excerpts[owner];
        var header = HeaderLine(owner);
        var localStart = (Line: startLine - header - 1, Column: startColumn);
        (int Line, int Column) localEnd;
        if (change.StartLine == change.EndLine && change.StartColumn == change.EndColumn && start.Kind == ExcerptLineKind.Header)
        {
            localEnd = localStart;
        }
        else
        {
            var end = Describe(change.EndLine);
            if (end.Kind == ExcerptLineKind.Content && end.Excerpt == owner)
                localEnd = (change.EndLine - header - 1, change.EndColumn);
            else if (end.Kind == ExcerptLineKind.Header && end.Excerpt == owner + 1 && change.EndColumn == 0)
                localEnd = (excerpt.Lines.Count, 0);   // 最終行を改行ごと消す（dd）：抜粋の終わりまで
            else
                return null;
        }

        var sourceStartLine = excerpt.Source.Start.Line;
        var sourceEdit = new TextBufferChange(
            sourceStartLine + localStart.Line, localStart.Column,
            sourceStartLine + localEnd.Line, localEnd.Column,
            newText, change.Version);

        return new ExcerptSourceEdit(excerpt.Path, ToLsp(sourceEdit, newText))
        {
            Owner = owner, LocalStart = localStart, LocalEnd = localEnd, SourceChange = sourceEdit,
        };
    }

    /// <summary><see cref="TranslateEdit"/> の編集が元ファイルへ入った：抜粋の本文と、同じファイルの抜粋の
    /// アンカーを<b>その正確な編集で</b>進める。</summary>
    public void Commit(ExcerptSourceEdit edit)
    {
        var excerpt = _excerpts[edit.Owner];
        ReplaceLines(excerpt, ApplyToLines(excerpt.Lines, edit.LocalStart, edit.LocalEnd, edit.SourceChange.NewText));
        foreach (var other in _excerpts.Where(e => SamePath(e.Path, excerpt.Path)))
            other.Source.Apply(edit.SourceChange);
    }

    private static LspTextEdit ToLsp(TextBufferChange change, string text)
        => new(new LspRange(new LspPosition(change.StartLine, change.StartColumn),
            new LspPosition(change.EndLine, change.EndColumn)), text);

    /// <summary>
    /// 元ファイルが（抜粋タブ以外から）変わった。アンカーを動かし、本文が変わった抜粋があれば抜粋タブへ
    /// 写す編集を返す（本文の読み直しは変わったときだけなので、元ファイルの打鍵ごとに全文を読むことはない）。
    /// </summary>
    public IReadOnlyList<ExcerptViewEdit> SourceChanged(
        string path, TextBufferChange change, Func<IReadOnlyList<string>> sourceLines)
    {
        var affected = new List<int>();
        for (var i = 0; i < _excerpts.Count; i++)
        {
            var excerpt = _excerpts[i];
            if (!SamePath(excerpt.Path, path)) continue;
            if (change.Kind == TextBufferChangeKind.Reload || Affects(excerpt.Source, change))
                affected.Add(i);
            if (change.Kind != TextBufferChangeKind.Reload)
                excerpt.Source.Apply(change);
        }
        if (affected.Count == 0) return [];
        return Resync(affected, sourceLines());
    }

    /// <summary>元ファイルの本文と見比べて、ずれている抜粋を写し直す（タブを結び付けた直後など）。</summary>
    public IReadOnlyList<ExcerptViewEdit> Resync(string path, IReadOnlyList<string> sourceLines)
        => Resync(Enumerable.Range(0, _excerpts.Count).Where(i => SamePath(_excerpts[i].Path, path)).ToList(),
            sourceLines);

    private List<ExcerptViewEdit> Resync(List<int> indices, IReadOnlyList<string> sourceLines)
    {
        // 後ろの抜粋から直す：前を直すと後ろの見出しの位置が動くので、返す編集は全て「直す前」の座標にする。
        var edits = new List<ExcerptViewEdit>();
        foreach (var i in indices.OrderByDescending(i => i))
        {
            var excerpt = _excerpts[i];
            var start = Math.Clamp(excerpt.Source.Start.Line, 0, sourceLines.Count);
            var end = Math.Clamp(excerpt.Source.End.Line, start, sourceLines.Count);
            // 抜粋の行だけを引く（元ファイル全体を辿らない。ホストは行を O(1) で引ける一覧を渡す）。
            var fresh = new List<string>(end - start);
            for (var line = start; line < end; line++) fresh.Add(sourceLines[line]);
            if (fresh.SequenceEqual(excerpt.Lines)) continue;

            var header = HeaderLine(i);
            var old = excerpt.Lines;
            var range = old.Count == 0
                ? new LspRange(new LspPosition(header, _header(excerpt.Path).Length), new LspPosition(header, _header(excerpt.Path).Length))
                : new LspRange(new LspPosition(header + 1, 0), new LspPosition(header + old.Count, old[^1].Length));
            var text = old.Count == 0
                ? (fresh.Count == 0 ? "" : "\n" + string.Join("\n", fresh))
                : string.Join("\n", fresh);
            if (old.Count > 0 && fresh.Count == 0)
            {
                // 本文が空になった：見出しの行末から消す（改行ごと）。
                var headerText = _header(excerpt.Path);
                range = new LspRange(new LspPosition(header, headerText.Length), range.End);
                text = "";
            }
            edits.Add(new ExcerptViewEdit(new LspTextEdit(range, text)));
            ReplaceLines(excerpt, fresh);
        }
        return edits;
    }

    /// <summary>行の半開区間 [Start, End) の本文を変える変更か。終わりちょうど（次の行の行頭）への挿入と、
    /// 始まりちょうどで終わる変更（前の行を改行ごと置き換える等）は含めない。</summary>
    private static bool Affects(AnchoredRange range, TextBufferChange change)
    {
        var start = new TextPoint(change.StartLine, change.StartColumn);
        var end = new TextPoint(change.EndLine, change.EndColumn);
        return start < range.End && (end > range.Start || start == range.Start);
    }

    private IEnumerable<string> AllLines()
    {
        foreach (var excerpt in _excerpts)
        {
            yield return _header(excerpt.Path);
            foreach (var line in excerpt.Lines) yield return line;
        }
    }

    /// <summary>行の並び（各行が改行で終わるとみなす）に、局所座標の置き換えを入れる。</summary>
    private static List<string> ApplyToLines(
        List<string> lines, (int Line, int Column) start, (int Line, int Column) end, string text)
    {
        var joined = string.Concat(lines.Select(l => l + "\n"));
        int Offset((int Line, int Column) p)
        {
            var offset = 0;
            for (var i = 0; i < p.Line && i < lines.Count; i++) offset += lines[i].Length + 1;
            return Math.Min(joined.Length, offset + p.Column);
        }
        var a = Offset(start);
        var b = Math.Max(a, Offset(end));
        var updated = joined[..a] + text + joined[b..];
        // 各行が改行で終わる表現なので、空文字列は 0 行、"\n" は空行 1 行。
        if (updated.Length == 0) return [];
        if (updated.EndsWith('\n')) updated = updated[..^1];
        return [.. updated.Split('\n')];
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
