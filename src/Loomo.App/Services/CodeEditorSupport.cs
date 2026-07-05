using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Editor.Core.Lsp;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 言語サーバー（LSP）のドキュメントシンボルから、コードファイルの構造アウトライン
/// （クラス／メソッド等の折りたたみツリー・現在メンバーのハイライト・クリックでジャンプ）を
/// EditorSupport ペインへ表示するフォールバック提供者。<see cref="HexEditorSupport"/> と同じく
/// <see cref="EditorSupportRegistry"/> には登録せず、専用プロバイダ（Markdown/JSON/XML/CSV 等）を
/// 持たない拡張子だけを <see cref="CanHandle"/> で拾う。JSON/XML プレビューと同じページシェル
/// （<see cref="JsonPreviewPage"/>・折りたたみ CSS・<c>data-line</c> ジャンプ）を共有する。
/// LSP 呼び出し自体は ShellWindow が担い（コントロール束縛のため）、ここは純粋な
/// シンボル→モデル変換と HTML 整形に集中してテスト可能にしている。
/// </summary>
public sealed class CodeEditorSupport
{
    private readonly AiSettings _settings;

    /// <summary>
    /// LSP コード解析の対象拡張子（キュレーション）。専用プロバイダを持つ
    /// <c>.json/.xml/.yaml/.md/.csv/.html</c> 等は<b>含めない</b>＝そもそもフォールバックに来ない。
    /// </summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt",
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".rb", ".php", ".swift", ".scala", ".lua", ".dart",
    };

    public CodeEditorSupport(AiSettings settings) => _settings = settings;

    /// <summary>この拡張子が LSP コード解析のフォールバック対象か（＝専用プロバイダ不在の想定）。</summary>
    public bool CanHandle(string? filePath)
        => !string.IsNullOrEmpty(filePath) && CodeExtensions.Contains(Path.GetExtension(filePath));

    public string DescribeTitle(string filePath) => $"Code: {Path.GetFileName(filePath)}";

    /// <summary>
    /// コードのフル HTML ページ（構造アウトライン＋②呼び出し解析パネル）を組む。<paramref name="caret"/>
    /// （エディタのキャレット、0 始まり line/col）を含む最深メンバーには <c>current</c> が付く。
    /// </summary>
    internal string RenderCodePage(
        string filePath, IReadOnlyList<OutlineNode> roots, (int line0, int col0)? caret, CallPanels panels)
    {
        var body = LspOutlineRenderer.RenderBody(roots, caret) + CallPanelRenderer.RenderPanels(panels);
        return JsonPreviewPage.BuildPage(body, DescribeTitle(filePath), _settings.Appearance.MarkdownPreviewTheme);
    }

    /// <summary>
    /// 言語サーバー未接続／未対応時の案内ページ。<paramref name="prompt"/>（<see cref="LspManagementService.EvaluateForFile"/>
    /// の結果）が非 null なら対応サーバー名・インストールコマンド・「インストール」ボタンを、null（＝導入済みだが
    /// 接続待ち）なら穏当な待機文言を出す。ボタンはページ JS 経由で web メッセージを送る（配線は ShellWindow）。
    /// </summary>
    public string RenderNoticePage(string filePath, LspPromptInfo? prompt)
        => JsonPreviewPage.BuildPage(
            LspNoticeRenderer.RenderBody(filePath, prompt),
            DescribeTitle(filePath),
            _settings.Appearance.MarkdownPreviewTheme);

    /// <summary>
    /// LSP の <see cref="DocumentSymbol"/>（0 始まり line/character）を、コントロール／LSP のシール型に
    /// 依存しない内部モデル <see cref="OutlineNode"/> へ写す。<c>Range.Start.Line</c>＝Line0、
    /// <c>Range.End.Line</c>＝EndLine0、<c>SelectionRange.Start.Line</c>＝NameLine0。
    /// <para>
    /// このパッケージの <see cref="DocumentSymbol"/> は LSP の <c>detail</c>（シグネチャ）を保持しない
    /// （Name/Kind/Range/SelectionRange/Children のみ）ため、シグネチャ（引数・戻り型など）は
    /// <paramref name="sourceLines"/>（エディタ本文を行分割したもの）の<b>宣言行</b>から
    /// <see cref="SignatureExtractor.Extract"/> で取り出して <see cref="OutlineNode.Detail"/> へ入れる。
    /// 本文が無ければ Detail は空（従来どおり名前だけ）。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<OutlineNode> ToOutline(
        IReadOnlyList<DocumentSymbol>? symbols, IReadOnlyList<string>? sourceLines = null)
    {
        if (symbols is null || symbols.Count == 0)
            return Array.Empty<OutlineNode>();

        var list = new List<OutlineNode>(symbols.Count);
        foreach (var s in symbols)
        {
            var line0 = s.Range?.Start?.Line ?? 0;
            var endLine0 = s.Range?.End?.Line ?? line0;
            var nameLine0 = s.SelectionRange?.Start?.Line ?? line0;
            var nameCol0 = s.SelectionRange?.Start?.Character ?? 0;
            var name = s.Name ?? "";
            var detail = SignatureExtractor.Extract(sourceLines, nameLine0, nameCol0, name);
            list.Add(new OutlineNode(
                name, s.Kind, line0, endLine0, nameLine0, nameCol0, ToOutline(s.Children, sourceLines), detail));
        }
        return list;
    }

    /// <summary>
    /// LSP の <c>Uri</c>（通常は <c>file://</c> URI・パーセントエンコード込み）をローカルパスへ変換する。
    /// file URI ならデコードしたローカルパスを、絶対 URI でなければ（既にパスとみなして）そのまま返す。
    /// </summary>
    internal static string? TryUriToLocalPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return null;
        // 絶対 URI（Windows の rooted パス C:\… も file URI として解釈される）は file スキームだけ受け、
        // untitled:/http: 等の飛べないスキームは null（＝ジャンプ不可）にする。
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u))
            return u.IsFile ? u.LocalPath : null;
        return uri; // 絶対 URI でない＝相対パス等の素の文字列としてそのまま返す
    }

    /// <summary><see cref="TryUriToLocalPath"/> したパスのファイル名部分（表示用）。取れなければ空文字。</summary>
    internal static string DisplayFileName(string? uri)
    {
        var path = TryUriToLocalPath(uri);
        if (string.IsNullOrEmpty(path))
            return "";
        try { return Path.GetFileName(path); }
        catch { return path; }
    }
}

/// <summary>
/// ②呼び出し解析の 1 件（呼び出し元/呼び出し先/使用箇所の共通行）。LSP のシール型に依存しない内部モデル。
/// </summary>
/// <param name="Symbol">シンボル名（呼び出し元/先の関数名。使用箇所は空でよい）。</param>
/// <param name="Uri">対象の <c>Uri</c>（<c>file://</c> URI か、ローカルパス）。</param>
/// <param name="Line0">対象行（0 始まり。呼び出しは <c>SelectionRange.Start.Line</c>、使用箇所は <c>Range.Start.Line</c>）。</param>
internal sealed record CallReference(string Symbol, string Uri, int Line0);

/// <summary>②呼び出し解析の 3 パネル（呼び出し元 / 呼び出し先 / 使用箇所）。</summary>
/// <param name="Incoming">呼び出し元（このメンバーを呼ぶ側）。</param>
/// <param name="Outgoing">呼び出し先（このメンバーが呼ぶ側）。</param>
/// <param name="References">使用箇所（参照）。</param>
/// <param name="Target">
/// 解析対象のシンボル名（callHierarchy が解決した名前）。パネル見出しに「◯◯ の呼び出し関係」と出して、
/// キャレット直下のどのメンバーの結果かを明示する。解決できなければ null（見出しは出さない）。
/// </param>
internal sealed record CallPanels(
    IReadOnlyList<CallReference> Incoming,
    IReadOnlyList<CallReference> Outgoing,
    IReadOnlyList<CallReference> References,
    string? Target = null)
{
    public static CallPanels Empty { get; } = new(
        Array.Empty<CallReference>(), Array.Empty<CallReference>(), Array.Empty<CallReference>());
}

/// <summary>
/// <see cref="CallPanels"/> を「呼び出し元 / 呼び出し先 / 使用箇所」の HTML パネルへ整形する純ロジック。
/// 各行は別ファイルへ飛べるよう <c>class="call-row" data-path data-line</c>（1 始まり行）を持ち、
/// ページ JS が <c>openFileAt</c> メッセージを送る。空パネルは「（なし）」を出す。共有 CSS に無い
/// クラスだけ先頭へ小さな <c>&lt;style&gt;</c> を差し込んで補う（<c>.k</c>/<c>.meta</c> の色は共有 CSS を流用）。
/// </summary>
internal static class CallPanelRenderer
{
    /// <summary>各セクションで表示する最大件数。超過分は「… 他 N 件」で畳む（巨大参照で UI を固めない）。</summary>
    internal const int MaxRows = 50;

    /// <summary>フル描画用：補助 CSS（<c>&lt;style&gt;</c>）＋パネル本体（<c>.call-panels</c>）。</summary>
    public static string RenderPanels(CallPanels panels) => Style() + RenderPanelsInner(panels);

    /// <summary>
    /// 部分更新用：パネル本体（<c>&lt;div class="call-panels"&gt;…&lt;/div&gt;</c>）だけを返す。CSS は含めない
    /// （フル描画時の <c>&lt;style&gt;</c> がページに残っている前提で、<c>.call-panels</c> の差し替えに使う）。
    /// </summary>
    public static string RenderPanelsInner(CallPanels panels)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"call-panels\">");
        // 見出し：どのシンボルの呼び出し関係か（キャレット直下）を明示。解決できたときだけ。
        if (!string.IsNullOrEmpty(panels.Target))
            sb.Append("<div class=\"call-title\"><span class=\"k\">")
              .Append(MarkdownRenderer.Encode(panels.Target!))
              .Append("</span> の呼び出し関係</div>");
        AppendSection(sb, "呼び出し元", panels.Incoming);
        AppendSection(sb, "呼び出し先", panels.Outgoing);
        AppendSection(sb, "使用箇所", panels.References);
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Style()
        // 共有 CSS（JsonPreviewPage）に無いクラスだけ補う。色は .k/.meta（テーマ済み）を流用。
        => "<style>"
           + ".call-panels{margin-top:12px;padding-top:8px;white-space:normal;"
           + "border-top:1px solid color-mix(in srgb,currentColor 22%,transparent);}"
           + ".call-title{font-weight:600;margin-bottom:8px;opacity:.95;}"
           + ".call-section{margin-bottom:8px;}"
           + ".call-head{font-weight:600;opacity:.85;margin-bottom:2px;}"
           + ".call-row{cursor:pointer;padding:1px 4px;border-radius:3px;}"
           + ".call-row[data-path]:hover{background:color-mix(in srgb,currentColor 12%,transparent);}"
           + ".call-empty{opacity:.55;padding:1px 4px;}"
           + ".call-more{opacity:.55;padding:1px 4px;font-size:.9em;}"
           + "</style>";

    private static void AppendSection(StringBuilder sb, string title, IReadOnlyList<CallReference> items)
    {
        sb.Append("<div class=\"call-section\"><div class=\"call-head\">")
          .Append(MarkdownRenderer.Encode(title))
          .Append("<span class=\"meta\"> ").Append(items.Count).Append("</span></div>");

        if (items.Count == 0)
        {
            sb.Append("<div class=\"call-empty\">（なし）</div></div>");
            return;
        }

        var shown = Math.Min(items.Count, MaxRows);
        for (var i = 0; i < shown; i++)
        {
            var it = items[i];
            var line1 = it.Line0 + 1; // 0 始まり(LSP) → 1 始まり(ジャンプ)
            var path = CodeEditorSupport.TryUriToLocalPath(it.Uri);
            var fileName = CodeEditorSupport.DisplayFileName(it.Uri);

            sb.Append("<div class=\"call-row\"");
            // パスへ変換できたときだけジャンプ可能にする（data-path が無ければ JS は何もしない）。
            if (!string.IsNullOrEmpty(path))
                sb.Append(" data-path=\"").Append(MarkdownRenderer.Encode(path))
                  .Append("\" data-line=\"").Append(line1).Append('"');
            sb.Append('>');

            if (!string.IsNullOrEmpty(it.Symbol))
                sb.Append("<span class=\"k\">").Append(MarkdownRenderer.Encode(it.Symbol)).Append("</span> ");

            sb.Append("<span class=\"meta\">").Append(MarkdownRenderer.Encode(fileName))
              .Append(':').Append(line1).Append("</span></div>");
        }

        // 上限超過分は件数だけ示す（全件描画で UI を固めない）。
        if (items.Count > MaxRows)
            sb.Append("<div class=\"call-more\">… 他 ").Append(items.Count - MaxRows).Append(" 件</div>");

        sb.Append("</div>");
    }
}

/// <summary>
/// コード構造アウトラインの内部モデル（LSP のシール型・エディタコントロールから切り離した純データ）。
/// 行粒度のみ保持する（列は保持しない＝包含判定は行単位）。line は全て 0 始まり。
/// </summary>
/// <param name="Name">シンボル名。</param>
/// <param name="Kind">シンボル種別（LSP の <see cref="SymbolKind"/> を流用）。</param>
/// <param name="Line0">シンボル範囲の開始行（0 始まり／<c>Range.Start.Line</c>）。</param>
/// <param name="EndLine0">シンボル範囲の終了行（0 始まり／<c>Range.End.Line</c>）。</param>
/// <param name="NameLine0">名前の行（0 始まり／<c>SelectionRange.Start.Line</c>）。シンボル名の位置。</param>
/// <param name="NameCol0">名前の列（0 始まり／<c>SelectionRange.Start.Character</c>）。シンボル名の位置。</param>
/// <param name="Children">子シンボル。</param>
/// <param name="Detail">
/// シグネチャ等の補足（引数・戻り型など）。このパッケージの <see cref="DocumentSymbol"/> は LSP の
/// <c>detail</c> を保持しないため、<see cref="SignatureExtractor.Extract"/> がソースの宣言行から切り出す。
/// 取れなければ空文字（従来どおり名前だけ表示）。
/// </param>
internal sealed record OutlineNode(
    string Name,
    SymbolKind Kind,
    int Line0,
    int EndLine0,
    int NameLine0,
    int NameCol0,
    IReadOnlyList<OutlineNode> Children,
    string Detail = "");

/// <summary>
/// LSP の <c>detail</c> が使えない（このパッケージの <see cref="DocumentSymbol"/> は保持しない）ため、
/// エディタ本文の<b>宣言行</b>からメンバーのシグネチャ（引数・戻り型など）を切り出す純ロジック。
/// <see cref="OutlineNode.NameCol0"/>（名前の開始列）を使い、宣言行の<b>名前の直後</b>から本体の開始
/// （<c>{</c>）手前までを取る。C# のように戻り型が名前の前にある言語では戻り型は落ちるが、引数列と
/// （TS/Go/Rust/Python 等の後置戻り型）は拾える。言語非依存で軽い（1 行の文字列処理のみ）。
/// </summary>
internal static class SignatureExtractor
{
    /// <summary>切り出したシグネチャの最大長（超過は「…」で丸める。アウトラインを1行に収める）。</summary>
    internal const int MaxLength = 80;

    /// <summary>
    /// <paramref name="sourceLines"/>（0 始まり行配列）の <paramref name="nameLine0"/> 行から、名前
    /// <paramref name="name"/>（<paramref name="nameCol0"/> 列開始）の直後〜本体開始手前をシグネチャとして返す。
    /// 本文が無い／行外／中身が実質空（<c>;</c> や <c>{</c> のみ）なら空文字。
    /// </summary>
    public static string Extract(
        IReadOnlyList<string>? sourceLines, int nameLine0, int nameCol0, string name)
    {
        if (sourceLines is null || nameLine0 < 0 || nameLine0 >= sourceLines.Count)
            return "";
        var line = sourceLines[nameLine0];
        if (string.IsNullOrEmpty(line))
            return "";

        // 名前の直後の位置を決める。SelectionRange の列が信用できる（その位置に名前がある）ならそれを使い、
        // ズレていれば行内を検索してフォールバックする（サーバー差の保険）。
        var start = -1;
        if (nameCol0 >= 0 && nameCol0 + name.Length <= line.Length
            && !string.IsNullOrEmpty(name)
            && string.CompareOrdinal(line, nameCol0, name, 0, name.Length) == 0)
        {
            start = nameCol0 + name.Length;
        }
        else if (!string.IsNullOrEmpty(name))
        {
            var idx = line.IndexOf(name, StringComparison.Ordinal);
            if (idx >= 0)
                start = idx + name.Length;
        }
        if (start < 0 || start > line.Length)
            return "";

        var tail = line[start..];

        // 本体（ブロック / 式本体 / 行コメント）は落とす。宣言部だけ残す。
        tail = CutAt(tail, "{");
        tail = CutAt(tail, "=>");
        tail = CutAt(tail, "//");
        // 代入（フィールド初期化子など）は落とすが、引数リスト "(...)" 内の "=" は既定値なので切らない。
        if (!tail.Contains('('))
            tail = CutAt(tail, " =");

        // 空白を畳んで両端と装飾記号（宣言末尾の ; , : ）を落とす。
        tail = CollapseWhitespace(tail).Trim().TrimEnd(';', ',', ':', ' ');

        // 引数も戻り型注釈も無いただの記号だけなら情報ゼロ＝出さない。
        if (tail.Length == 0 || tail == "()")
            return "";

        if (tail.Length > MaxLength)
            tail = tail[..MaxLength].TrimEnd() + "…";
        return tail;
    }

    private static string CutAt(string s, string marker)
    {
        var i = s.IndexOf(marker, StringComparison.Ordinal);
        return i >= 0 ? s[..i] : s;
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var prevWs = false;
        foreach (var ch in s)
        {
            var ws = ch is ' ' or '\t' or '\r' or '\n';
            if (ws)
            {
                if (!prevWs) sb.Append(' ');
            }
            else sb.Append(ch);
            prevWs = ws;
        }
        return sb.ToString();
    }
}

/// <summary>
/// <see cref="OutlineNode"/> ツリーを、JSON/XML プレビューと同じ折りたたみ HTML（<c>#json-root</c> の中身）へ
/// 変換する純ロジック。<see cref="JsonPreviewPage.BuildPage"/> の折りたたみ JS（<c>.opening</c> クリックで開閉、
/// <c>.goto</c> クリックで <c>data-line</c> 行へ jumpToSource）がそのまま効く。共有 CSS に無い
/// <c>current</c>（現在メンバー）のハイライトだけは本文先頭へ小さな <c>&lt;style&gt;</c> を差し込んで補う。
/// </summary>
internal static class LspOutlineRenderer
{
    /// <summary>
    /// アウトラインの折りたたみ HTML（本文のみ）を返す。<paramref name="caret"/> を含む最深メンバーの
    /// 行に <c>current</c> を付ける。各ノードの <c>data-line</c> は <c>Line0 + 1</c>（1 始まり＝JSON ツリーと
    /// 同じ・<c>jumpToSource</c>／<c>FocusEditorSupportSource</c> が期待する基準）。
    /// </summary>
    public static string RenderBody(IReadOnlyList<OutlineNode> roots, (int line0, int col0)? caret)
    {
        if (roots is null || roots.Count == 0)
            return "<div class=\"empty\">（コード構造がありません）</div>";

        var current = caret is { } c ? FindEnclosing(roots, c.line0, c.col0) : null;

        var sb = new StringBuilder();
        sb.Append(OutlineStyle);

        foreach (var node in roots)
            WriteNode(sb, node, current);

        return sb.ToString();
    }

    /// <summary>
    /// コードページ専用の補助 CSS（この <c>&lt;style&gt;</c> はコードページにしか出ないので、共有 CSS を
    /// 上書きして<b>高密度化</b>してよい）。種別バッジ（<c>.sym</c>＋色分け）・シグネチャ（<c>.sig</c>）・
    /// クリック可能な名前（<c>.nav</c>）・現在メンバー（<c>.current</c>）・インデント/行間の詰めを補う。
    /// </summary>
    private const string OutlineStyle =
        "<style>"
        // 詰める：行間・子インデント・ジャンプアイコン余白（共有 CSS を上書き）。
        + "#json-root{line-height:1.34;}"
        + ".children{padding-left:0.95em;margin-left:0.2em;}"
        + ".line,.opening{white-space:pre;}"
        + ".goto{margin-left:6px;}"
        + ".lead{display:inline-block;width:1em;}"
        + ".caret{width:0.9em;}"
        // 現在メンバー：左に色バー＋淡い背景で明示。
        + ".current{background:color-mix(in srgb,currentColor 10%,transparent);border-radius:3px;"
        + "box-shadow:inset 2px 0 0 color-mix(in srgb,currentColor 55%,transparent);}"
        // 種別バッジ：等幅の1文字アイコン。色は種別ごと（下の s-* クラス）。
        + ".sym{display:inline-block;min-width:1.15em;text-align:center;font-weight:700;"
        + "font-size:0.78em;margin-right:0.28em;opacity:0.95;user-select:none;}"
        + ".s-ns{color:#9AA4B2;}.s-type{color:#4EC9B0;}.s-iface{color:#4FC1FF;}"
        + ".s-enum{color:#E5C07B;}.s-method{color:#C586C0;}.s-prop{color:#9CDCFE;}"
        + ".s-field{color:#79C0FF;}.s-func{color:#C586C0;}.s-event{color:#E5C07B;}"
        + ".s-const{color:#56D4C6;}.s-var{color:#9CDCFE;}.s-def{color:#9AA4B2;}"
        // シグネチャ：淡色で名前の後ろに添える。
        + ".sig{opacity:0.5;margin-left:0.4em;font-size:0.9em;}"
        // 名前はクリックでソースへジャンプ（キャレット追従で②も更新）。
        + ".nav{cursor:pointer;}"
        + ".line:hover>.k.nav,.opening:hover>.k.nav{text-decoration:underline;}"
        + "</style>";

    private static void WriteNode(StringBuilder sb, OutlineNode node, OutlineNode? current)
    {
        var isCurrent = ReferenceEquals(node, current);
        var dataLine = node.Line0 + 1; // 0 始まり(LSP) → 1 始まり(UI/ジャンプ)
        var name = MarkdownRenderer.Encode(node.Name);
        var (glyph, cls, title) = KindBadge(node.Kind);

        void AppendBadgeAndName()
        {
            sb.Append("<span class=\"sym ").Append(cls).Append("\" title=\"").Append(title).Append("\">")
              .Append(glyph).Append("</span>")
              .Append("<span class=\"k nav\">").Append(name).Append("</span>");
            if (node.Detail.Length > 0)
                sb.Append("<span class=\"sig\">").Append(MarkdownRenderer.Encode(node.Detail)).Append("</span>");
            sb.Append("<span class=\"goto\" title=\"エディタで開く\">↦</span>");
        }

        if (node.Children.Count > 0)
        {
            sb.Append("<div class=\"node\"><div class=\"line opening")
              .Append(isCurrent ? " current" : "")
              .Append("\" data-line=\"").Append(dataLine).Append("\">")
              .Append("<span class=\"caret\"></span>");
            AppendBadgeAndName();
            sb.Append("</div><div class=\"children\">");

            foreach (var child in node.Children)
                WriteNode(sb, child, current);

            sb.Append("</div></div>");
        }
        else
        {
            sb.Append("<div class=\"line")
              .Append(isCurrent ? " current" : "")
              .Append("\" data-line=\"").Append(dataLine).Append("\">")
              .Append("<span class=\"lead\"></span>");
            AppendBadgeAndName();
            sb.Append("</div>");
        }
    }

    /// <summary>
    /// <paramref name="line0"/>（0 始まり）を含む最深ノードを返す。兄弟の範囲は重ならない前提で、包含した
    /// 枝だけを潜って一番深いノードへ辿る。<see cref="OutlineNode"/> は行粒度しか持たないため
    /// 包含判定は行のみで行い、<paramref name="col0"/> は使わない（引数は整合／将来用に受ける）。
    /// </summary>
    public static OutlineNode? FindEnclosing(IReadOnlyList<OutlineNode> roots, int line0, int col0)
    {
        if (roots is null)
            return null;

        foreach (var node in roots)
        {
            if (line0 < node.Line0 || line0 > node.EndLine0)
                continue;

            return FindEnclosing(node.Children, line0, col0) ?? node;
        }
        return null;
    }

    /// <summary>
    /// シンボル種別 → 表示バッジ（1 文字グリフ・色クラス <c>s-*</c>・ツールチップ）。文字ラベルを
    /// やめて等幅の色付きアイコンにし、走査性を上げつつ横幅を詰める（<c>title</c> に正式名を出す）。
    /// </summary>
    private static (string Glyph, string Cls, string Title) KindBadge(SymbolKind kind) => kind switch
    {
        SymbolKind.Namespace => ("N", "s-ns", "namespace"),
        SymbolKind.Module => ("N", "s-ns", "module"),
        SymbolKind.Package => ("N", "s-ns", "package"),
        SymbolKind.Class => ("C", "s-type", "class"),
        SymbolKind.Struct => ("S", "s-type", "struct"),
        SymbolKind.Interface => ("I", "s-iface", "interface"),
        SymbolKind.Enum => ("E", "s-enum", "enum"),
        SymbolKind.EnumMember => ("e", "s-enum", "enum member"),
        SymbolKind.Method => ("M", "s-method", "method"),
        SymbolKind.Constructor => ("c", "s-method", "constructor"),
        SymbolKind.Function => ("ƒ", "s-func", "function"),
        SymbolKind.Property => ("P", "s-prop", "property"),
        SymbolKind.Field => ("F", "s-field", "field"),
        SymbolKind.Event => ("⚡", "s-event", "event"),
        SymbolKind.Constant => ("K", "s-const", "constant"),
        SymbolKind.Variable => ("V", "s-var", "variable"),
        _ => ("•", "s-def", kind.ToString().ToLowerInvariant()),
    };
}

/// <summary>
/// 言語サーバー未接続／未対応時の案内本文（<c>#json-root</c> の中身）を作る純ロジック。
/// <see cref="LspPromptInfo"/> が非 null（未導入／未設定）なら対応サーバー名・インストールコマンド・
/// 「インストール」ボタン（<c>.lsp-install-btn</c>）を、<c>InstallCommand</c> が無ければ導入手順リンク
/// （<c>.lsp-docs-btn</c>）を出す。常に「LSP 設定を開く」（<c>.lsp-settings-btn</c>）を添える。null（＝導入済みだが
/// 接続待ち）なら待機文言のみ。ボタンのクリック→web メッセージ配線は共有ページ JS（<see cref="JsonPreviewPage"/>）が担う。
/// </summary>
internal static class LspNoticeRenderer
{
    public static string RenderBody(string filePath, LspPromptInfo? prompt)
    {
        var sb = new StringBuilder();
        // 共有 CSS に無いクラスだけ補う（色は .k/.meta を流用）。
        sb.Append("<style>")
          .Append(".notice{white-space:normal;padding:6px 2px;max-width:60em;}")
          .Append(".notice h2{font-size:1.05em;margin:0 0 8px;}")
          .Append(".notice p{margin:0 0 8px;}")
          .Append(".notice pre.cmd{white-space:pre-wrap;padding:6px 10px;border-radius:4px;")
          .Append("background:color-mix(in srgb,currentColor 8%,transparent);margin:0 0 10px;}")
          .Append(".notice .btns{display:flex;gap:8px;flex-wrap:wrap;margin-top:6px;}")
          .Append(".notice button{font:inherit;cursor:pointer;padding:4px 12px;border-radius:5px;")
          .Append("border:1px solid color-mix(in srgb,currentColor 30%,transparent);")
          .Append("background:color-mix(in srgb,currentColor 8%,transparent);color:inherit;}")
          .Append(".notice button:hover{background:color-mix(in srgb,currentColor 16%,transparent);}")
          .Append("</style>");

        sb.Append("<div class=\"notice\"><h2>コード構造</h2>");

        if (prompt is null)
        {
            // 対応サーバーは導入済み・設定済み（EvaluateForFile は null）。あとは接続／解析待ち。
            sb.Append("<p>言語サーバーへの接続待ちです。解析が完了すると、クラス・メソッド等の構造")
              .Append("アウトラインと呼び出し解析（呼び出し元／呼び出し先／使用箇所）を表示します。</p>")
              .Append("</div>");
            return sb.ToString();
        }

        sb.Append("<p>").Append(Enc(prompt.Message)).Append("</p>");

        if (!string.IsNullOrEmpty(prompt.DisplayName))
            sb.Append("<p class=\"meta\">対応サーバー: ").Append(Enc(prompt.DisplayName!)).Append("</p>");

        if (!string.IsNullOrEmpty(prompt.InstallCommand))
            sb.Append("<pre class=\"cmd\">").Append(Enc(prompt.InstallCommand!)).Append("</pre>");
        else if (!string.IsNullOrEmpty(prompt.DocsUrl))
            sb.Append("<p class=\"meta\">導入手順: ").Append(Enc(prompt.DocsUrl!)).Append("</p>");

        sb.Append("<div class=\"btns\">");
        if (!string.IsNullOrEmpty(prompt.InstallCommand))
            // ext はクリック時の再判定ヒント（実際の対象は ShellWindow が現在ファイルから再評価する）。
            sb.Append("<button class=\"lsp-install-btn\" data-ext=\"").Append(Enc(prompt.Extension))
              .Append("\">インストール</button>");
        else if (!string.IsNullOrEmpty(prompt.DocsUrl))
            sb.Append("<button class=\"lsp-docs-btn\" data-url=\"").Append(Enc(prompt.DocsUrl!))
              .Append("\">導入手順を開く</button>");
        sb.Append("<button class=\"lsp-settings-btn\">LSP 設定を開く</button>");
        sb.Append("</div>");

        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Enc(string s) => MarkdownRenderer.Encode(s);
}
