using System.Text;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Claude Code の会話を「読み物」として組む（§39.4）。ターミナルでは返事は流れていく文字で、表は崩れ、コードは素の
/// 文字、図（mermaid）は描かれない。同じ中身を Markdown として描き直し、頼みごとごとに見出しの立った1本の文書にする
/// ——EditorSupport が .md をプレビューにするのと同じ「見せ方を変える」。
/// <para>出すのは本文（body の中身）だけで、ページの体裁は <see cref="MarkdownPage"/>（EditorSupport と同じ）。
/// ターンごとに 1 つの <c>section</c> にしてあるので、ページの本文差し替え（<c>setBody</c>）は書き足されたターンだけを
/// 入れ替える——読み返している古いターンは触らない。</para>
/// <para>組むのも<b>変わったターンだけ</b>（§39.8）。ターンの HTML は中身の指紋ごとに <see cref="RenderCache"/> に控え、
/// 作業中に数秒おきに来る組み直しでは、書き足された最新のターンだけを Markdown から描く。</para>
/// </summary>
internal static class ClaudeConversationDocument
{
    /// <summary>ターンごとの描画結果の控え。組み直しは裏のスレッドで重なりうるので内部で直列にする。
    /// 直近の組み立てで使われなかったものは捨てる（会話の外へ出た古いターン・描き直す前の版）。</summary>
    internal sealed class RenderCache
    {
        private readonly object _gate = new();
        private Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public int Count { get { lock (_gate) return _entries.Count; } }

        internal string GetOrRender(string key, Func<string> render, Dictionary<string, string> used)
        {
            string? html;
            lock (_gate)
                _entries.TryGetValue(key, out html);
            html ??= render();
            used[key] = html;
            return html;
        }

        internal void Keep(Dictionary<string, string> used)
        {
            lock (_gate)
                _entries = used;
        }
    }

    /// <summary>見出しにする頼みごとの長さ。全文は見出しの下に添える。</summary>
    private const int HeadingChars = 80;

    /// <summary>本文（body の中身）を組む。</summary>
    /// <param name="inWorkspace">ワークスペースの中か。外で書き換えたファイル（一時ファイル等）は数だけ。</param>
    /// <param name="displayPath">ファイルの見せ方（マルチルートの表示パス）。</param>
    /// <param name="working">Claude がいま作業中か（最後のターンの最後の本文を「途中」と扱う）。</param>
    /// <param name="cache">ターンごとの描画の控え（無ければ毎回全ターンを描く）。</param>
    public static string BuildBody(
        ClaudeSessionDigest digest, Func<string, bool> inWorkspace, Func<string, string> displayPath, bool working,
        RenderCache? cache = null)
    {
        var html = new StringBuilder();
        html.Append(Style);
        if (digest.Turns.Count == 0)
        {
            html.Append("<p class=\"cc-empty\">まだ頼みごとがありません。</p>");
            return html.ToString();
        }
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < digest.Turns.Count; i++)
        {
            var turn = digest.Turns[i];
            var inProgress = i == digest.Turns.Count - 1 && working;
            html.Append(cache is null
                ? RenderTurn(turn, inProgress, inWorkspace, displayPath)
                : cache.GetOrRender(TurnKey(turn, inProgress), () => RenderTurn(turn, inProgress, inWorkspace, displayPath), used));
        }
        cache?.Keep(used);
        return html.ToString();
    }

    /// <summary>ターンの描画を変えうるものの指紋。本文・操作は書き足されるだけで書き換わらないので、数と最後の長さで足りる。</summary>
    internal static string TurnKey(ClaudeTurn turn, bool inProgress)
        => string.Join('|', turn.PromptLine, turn.At?.ToUnixTimeSeconds(), turn.Replies.Count, turn.Reply?.Length,
            turn.Calls.Count, turn.Calls.Count(c => c.HasResult), turn.Failures.Count(), turn.Files.Count,
            string.Join(',', turn.Files.Select(f => f.Path)), inProgress);

    private static string RenderTurn(ClaudeTurn turn, bool inProgress, Func<string, bool> inWorkspace, Func<string, string> displayPath)
    {
        var html = new StringBuilder();
        AppendTurn(html, turn, inProgress, inWorkspace, displayPath);
        return html.ToString();
    }

    private static void AppendTurn(
        StringBuilder html, ClaudeTurn turn, bool inProgress,
        Func<string, bool> inWorkspace, Func<string, string> displayPath)
    {
        // 返事の中の見出し・脚注の id はターンごとに頭を付けて一意にする（同じ題の節が二つのターンにあると、
        // 「#まとめ」へのリンクやパーマリンクが先のターンへ飛んでいた）。
        var scope = "t" + turn.PromptLine + "-";
        html.Append("<section class=\"cc-turn\">");

        // 頼みごと：見出しは1行目（長ければ詰める）。全文が見出しに収まらなければ下に添える。
        var prompt = turn.Prompt.Trim();
        var firstLine = prompt.Split('\n')[0].Trim();
        var heading = firstLine.Length <= HeadingChars ? firstLine : firstLine[..(HeadingChars - 1)] + "…";
        html.Append("<div class=\"cc-ask\">");
        html.Append("<div class=\"cc-who\">あなた").Append(turn.At is { } at ? " · " + Encode(LocalTime(at)) : "").Append("</div>");
        html.Append("<h2 id=\"cc-turn-").Append(turn.PromptLine).Append("\">").Append(Encode(heading)).Append("</h2>");
        if (prompt != heading)
            html.Append("<div class=\"cc-ask-full\">").Append(Encode(prompt)).Append("</div>");
        html.Append("</div>");

        // このターンで変えたもの（読み物の脇に添える1行。ファイルは押せば開く）。
        var meta = Meta(turn, inWorkspace, displayPath);
        if (meta.Length > 0)
            html.Append("<div class=\"cc-meta\">").Append(meta).Append("</div>");

        // 本文。最後の1つが返事、それより前は作業の途中に書いたメモなので畳む。
        var replies = turn.Replies;
        var notes = replies.Count > 1 ? replies.Take(replies.Count - 1).ToList() : [];
        if (notes.Count > 0)
        {
            html.Append("<details class=\"cc-notes\"><summary>途中のメモ ").Append(notes.Count).Append(" 件</summary>");
            foreach (var note in notes)
                html.Append("<div class=\"cc-note\">").Append(RenderReply(note, scope)).Append("</div>");
            html.Append("</details>");
        }
        if (turn.Reply is { } reply)
        {
            html.Append(inProgress ? "<div class=\"cc-reply cc-progress\"><div class=\"cc-who\">Claude · 作業中</div>"
                                   : "<div class=\"cc-reply\"><div class=\"cc-who\">Claude</div>");
            html.Append(RenderReply(reply, scope));
            html.Append("</div>");
        }
        else if (inProgress)
        {
            html.Append("<div class=\"cc-reply cc-progress\"><div class=\"cc-who\">Claude · 作業中</div></div>");
        }
        html.Append("</section>");
    }

    private static string RenderReply(string markdown, string scope)
        => ScopeIds(MarkdownRenderer.RenderToBody(DemoteHeadings(markdown)), scope);

    /// <summary>描画した HTML の id と、それを指す同じ文書内のリンク（<c>href="#…"</c>）に頭を付ける。</summary>
    internal static string ScopeIds(string html, string scope)
        => ScopedReference.Replace(html, m => m.Groups[1].Value + scope + m.Groups[2].Value + "\"");

    private static readonly Regex ScopedReference = new(@"(\sid=""|\shref=""#)([^""]+)""", RegexOptions.Compiled);

    private static string Meta(ClaudeTurn turn, Func<string, bool> inWorkspace, Func<string, string> displayPath)
    {
        var parts = new List<string>();
        var inside = turn.Files.Where(f => inWorkspace(f.Path)).ToList();
        var outside = turn.Files.Count - inside.Count;
        if (inside.Count > 0)
        {
            var links = inside.Select(f =>
                $"<a href=\"{EncodeAttribute(new Uri(f.Path).AbsoluteUri)}\" title=\"{EncodeAttribute(displayPath(f.Path))}\">"
                + Encode(Path.GetFileName(f.Path)) + "</a>");
            parts.Add("✎ " + string.Join(", ", links));
        }
        if (outside > 0)
            parts.Add($"ワークスペースの外 {outside} ファイル");
        if (turn.Commands > 0)
            parts.Add($"コマンド {turn.Commands} 回");
        if (turn.Subagents > 0)
            parts.Add($"サブエージェント {turn.Subagents}");
        var failures = turn.Failures.Count();
        if (failures > 0)
            parts.Add($"<span class=\"cc-fail\">✗ 失敗 {failures}</span>");
        return string.Join(" · ", parts);
    }

    /// <summary>返事の見出しを2段下げる。頼みごとが h2 なので、返事の節（# や ##）はその下（h3〜）に入れる——
    /// ページのアウトライン欄が「頼みごと → 返事の節」の目次になる。コードブロックの中の # は触らない。</summary>
    internal static string DemoteHeadings(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var fence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = !fence;
                continue;
            }
            if (fence)
                continue;
            if (HeadingLine.Match(lines[i]) is { Success: true } m)
            {
                var level = Math.Min(6, m.Groups[1].Value.Length + 2);
                lines[i] = new string('#', level) + lines[i][m.Groups[1].Length..];
            }
        }
        return string.Join("\n", lines);
    }

    private static readonly Regex HeadingLine = new(@"^(#{1,6})(?=\s)", RegexOptions.Compiled);

    private static string LocalTime(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("M/d HH:mm");
    }

    private static string Encode(string s) => MarkdownRenderer.Encode(s);
    private static string EncodeAttribute(string s) => MarkdownRenderer.EncodeAttribute(s);

    /// <summary>会話の体裁。色はページのテーマの変数に乗せず、文字色からの透過で作る（どのテーマでも馴染む）。</summary>
    private const string Style = """
        <style>
        .cc-turn { margin: 0 0 2.2em; }
        .cc-who { font-size: .78em; opacity: .6; margin-bottom: .2em; letter-spacing: .02em; }
        .cc-ask { border-left: 3px solid currentColor; padding: .3em 0 .3em .8em; margin-bottom: .6em;
                  border-left-color: color-mix(in srgb, currentColor 35%, transparent); }
        .cc-ask h2 { margin: 0; padding: 0; border: none; font-size: 1.15em; }
        .cc-ask-full { white-space: pre-wrap; opacity: .8; margin-top: .4em; font-size: .92em; }
        .cc-meta { font-size: .82em; opacity: .75; margin: 0 0 .8em; }
        .cc-meta a { text-decoration: none; }
        .cc-fail { color: #e5534b; }
        .cc-notes { margin: 0 0 .8em; font-size: .9em; }
        .cc-notes summary { cursor: pointer; opacity: .65; font-size: .9em; }
        .cc-note { opacity: .75; border-left: 2px dotted color-mix(in srgb, currentColor 30%, transparent);
                   padding-left: .8em; margin: .5em 0; }
        .cc-progress { opacity: .85; }
        .cc-empty { opacity: .6; }
        </style>
        """;
}
