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
    /// </summary>
    internal static IReadOnlyList<OutlineNode> ToOutline(IReadOnlyList<DocumentSymbol>? symbols)
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
            list.Add(new OutlineNode(s.Name ?? "", s.Kind, line0, endLine0, nameLine0, nameCol0, ToOutline(s.Children)));
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
internal sealed record CallPanels(
    IReadOnlyList<CallReference> Incoming,
    IReadOnlyList<CallReference> Outgoing,
    IReadOnlyList<CallReference> References)
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
        AppendSection(sb, "呼び出し元", panels.Incoming);
        AppendSection(sb, "呼び出し先", panels.Outgoing);
        AppendSection(sb, "使用箇所", panels.References);
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Style()
        // 共有 CSS（JsonPreviewPage）に無いクラスだけ補う。色は .k/.meta（テーマ済み）を流用。
        => "<style>"
           + ".call-panels{margin-top:14px;padding-top:8px;white-space:normal;"
           + "border-top:1px solid color-mix(in srgb,currentColor 22%,transparent);}"
           + ".call-section{margin-bottom:10px;}"
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
/// <param name="NameLine0">名前の行（0 始まり／<c>SelectionRange.Start.Line</c>）。②の問い合わせ位置に使う。</param>
/// <param name="NameCol0">名前の列（0 始まり／<c>SelectionRange.Start.Character</c>）。②の問い合わせ位置に使う。</param>
/// <param name="Children">子シンボル。</param>
internal sealed record OutlineNode(
    string Name,
    SymbolKind Kind,
    int Line0,
    int EndLine0,
    int NameLine0,
    int NameCol0,
    IReadOnlyList<OutlineNode> Children);

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
        // 共有 CSS（JsonPreviewPage）には .current / .lead が無いので、ここで最小限だけ補う。
        sb.Append("<style>")
          .Append(".current{background:color-mix(in srgb,currentColor 12%,transparent);border-radius:3px;}")
          .Append(".lead{display:inline-block;width:1em;}")
          .Append("</style>");

        foreach (var node in roots)
            WriteNode(sb, node, current);

        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, OutlineNode node, OutlineNode? current)
    {
        var isCurrent = ReferenceEquals(node, current);
        var dataLine = node.Line0 + 1; // 0 始まり(LSP) → 1 始まり(UI/ジャンプ)
        var name = MarkdownRenderer.Encode(node.Name);
        var kind = KindLabel(node.Kind);

        if (node.Children.Count > 0)
        {
            sb.Append("<div class=\"node\"><div class=\"line opening")
              .Append(isCurrent ? " current" : "")
              .Append("\" data-line=\"").Append(dataLine).Append("\">")
              .Append("<span class=\"caret\"></span>")
              .Append("<span class=\"meta\">").Append(kind).Append("</span> ")
              .Append("<span class=\"k\">").Append(name).Append("</span>")
              .Append("<span class=\"goto\" title=\"エディタで開く\">↦</span>")
              .Append("</div><div class=\"children\">");

            foreach (var child in node.Children)
                WriteNode(sb, child, current);

            sb.Append("</div></div>");
        }
        else
        {
            sb.Append("<div class=\"line")
              .Append(isCurrent ? " current" : "")
              .Append("\" data-line=\"").Append(dataLine).Append("\">")
              .Append("<span class=\"lead\"></span>")
              .Append("<span class=\"meta\">").Append(kind).Append("</span> ")
              .Append("<span class=\"k\">").Append(name).Append("</span>")
              .Append("<span class=\"goto\" title=\"エディタで開く\">↦</span>")
              .Append("</div>");
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

    /// <summary>シンボル種別の短い表示ラベル（メタ表示用）。未知種別は enum 名の小文字にフォールバック。</summary>
    private static string KindLabel(SymbolKind kind) => kind switch
    {
        SymbolKind.Class => "class",
        SymbolKind.Interface => "interface",
        SymbolKind.Struct => "struct",
        SymbolKind.Enum => "enum",
        SymbolKind.EnumMember => "enum member",
        SymbolKind.Method => "method",
        SymbolKind.Constructor => "ctor",
        SymbolKind.Property => "prop",
        SymbolKind.Field => "field",
        SymbolKind.Function => "func",
        SymbolKind.Namespace => "namespace",
        SymbolKind.Module => "module",
        SymbolKind.Package => "package",
        SymbolKind.Event => "event",
        SymbolKind.Constant => "const",
        SymbolKind.Variable => "var",
        _ => kind.ToString().ToLowerInvariant(),
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
