using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Editor.Core.Lsp;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 言語サーバー（LSP）のドキュメントシンボルから、コードファイルの構造アウトライン
/// （クラス／メソッド等の折りたたみツリー・現在メンバーのハイライト・クリックでジャンプ）と
/// ②呼び出し解析（呼び出し元／呼び出し先／使用箇所）を EditorSupport ペインへ表示する
/// フォールバック提供者の<b>純ロジック層</b>。<see cref="HexEditorSupport"/> と同じく
/// <see cref="EditorSupportRegistry"/> には登録せず、専用プロバイダ（Markdown/JSON/XML/CSV 等）を
/// 持たない拡張子だけを <see cref="EditorSupportResolver"/> が <see cref="CanHandle"/> で拾う。
/// <para>
/// 表示は WebView2 ではなく <see cref="Views.CodeOutlineView"/>（ネイティブ WPF）が担う
/// （2026-07：初回コールドスタート・白フラッシュ・HTML 生成コストを避けるため HTML から移行）。
/// LSP 呼び出し自体は <see cref="Views.ShellWindow"/> が担い（コントロール束縛のため）、ここは
/// 純粋な「LSP シンボル → 内部モデル」変換と、テスト可能な整形ロジックに集中する。
/// </para>
/// </summary>
public sealed class CodeEditorSupport
{
    /// <summary>
    /// LSP コード解析の対象拡張子（キュレーション）。専用プロバイダを持つ
    /// <c>.json/.xml/.yaml/.md/.csv/.html</c> 等は<b>含めない</b>＝そもそもフォールバックに来ない。
    /// </summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt",
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".rb", ".php", ".swift", ".scala", ".lua", ".dart",
    };

    /// <summary>この拡張子が LSP コード解析のフォールバック対象か（＝専用プロバイダ不在の想定）。</summary>
    public bool CanHandle(string? filePath)
        => !string.IsNullOrEmpty(filePath) && CodeExtensions.Contains(Path.GetExtension(filePath));

    public string DescribeTitle(string filePath) => $"Code: {Path.GetFileName(filePath)}";

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
    public static IReadOnlyList<OutlineNode> ToOutline(
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
public sealed record OutlineNode(
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
/// アウトライン（<see cref="OutlineNode"/> ツリー）の純ロジック：キャレット包含判定と種別バッジ。
/// 表示は <see cref="Views.CodeOutlineView"/> が担うので、ここは HTML/WPF いずれにも依存しない
/// （バッジは 1 文字グリフ＋色コード＋ツールチップ文字列だけを返し、色→ブラシ化はビュー側）。
/// </summary>
internal static class CodeOutline
{
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
    /// シンボル種別 → 表示バッジ（1 文字グリフ・テーマ追従の色リソースキー・ツールチップ）。等幅の色付き
    /// アイコンで走査性を上げつつ横幅を詰める（<c>Title</c> に正式名）。<c>BrushKey</c> はパレット
    /// （<c>Palette.*.xaml</c>）の <c>Sym*</c> ブラシキーで、ビューが <c>SetResourceReference</c> で張るため
    /// テーマ切替（Light/HighContrast 等の背景差）に追従する（以前は <c>#RRGGBB</c> のテーマ非依存固定色で、
    /// 白背景テーマでバッジが読めなかった）。
    /// </summary>
    public static (string Glyph, string BrushKey, string Title) KindBadge(SymbolKind kind) => kind switch
    {
        SymbolKind.Namespace => ("N", "SymNamespace", "namespace"),
        SymbolKind.Module => ("N", "SymNamespace", "module"),
        SymbolKind.Package => ("N", "SymNamespace", "package"),
        SymbolKind.Class => ("C", "SymType", "class"),
        SymbolKind.Struct => ("S", "SymType", "struct"),
        SymbolKind.Interface => ("I", "SymInterface", "interface"),
        SymbolKind.Enum => ("E", "SymEnum", "enum"),
        SymbolKind.EnumMember => ("e", "SymEnum", "enum member"),
        SymbolKind.Method => ("M", "SymMethod", "method"),
        SymbolKind.Constructor => ("c", "SymMethod", "constructor"),
        SymbolKind.Function => ("ƒ", "SymMethod", "function"),
        SymbolKind.Property => ("P", "SymProperty", "property"),
        SymbolKind.Field => ("F", "SymField", "field"),
        SymbolKind.Event => ("⚡", "SymEnum", "event"),
        SymbolKind.Constant => ("K", "SymConstant", "constant"),
        SymbolKind.Variable => ("V", "SymProperty", "variable"),
        _ => ("•", "SymNamespace", kind.ToString().ToLowerInvariant()),
    };
}

/// <summary>
/// ②呼び出し解析（<see cref="CallPanels"/>）を、表示に必要な「セクション（見出し＋件数）＋行（ジャンプ先付き）
/// ＋上限超過数」の純モデルへ整形する。件数上限（<see cref="MaxRows"/>）で頭打ちし、超過分は数だけ返す
/// （巨大参照で UI を固めない）。ビュー（<see cref="Views.CodeOutlineView"/>）はこのモデルをそのまま描く。
/// </summary>
internal static class CallPanelModel
{
    /// <summary>各セクションで表示する最大件数。超過分は「他 N 件」で畳む。</summary>
    internal const int MaxRows = 50;

    /// <summary>1 行分の表示モデル。</summary>
    /// <param name="Symbol">シンボル名（呼び出し元/先。使用箇所は空）。</param>
    /// <param name="FileName">表示用ファイル名。</param>
    /// <param name="Line1">表示・ジャンプ用の行（1 始まり）。</param>
    /// <param name="Path">ジャンプ先ローカルパス（変換できなければ null＝ジャンプ不可）。</param>
    internal sealed record Row(string Symbol, string FileName, int Line1, string? Path);

    /// <summary>1 セクション分の表示モデル（見出し＋総件数＋表示行＋超過数）。</summary>
    /// <param name="Title">見出し（「呼び出し元」等）。</param>
    /// <param name="TotalCount">総件数（上限前）。</param>
    /// <param name="Rows">表示する行（最大 <see cref="MaxRows"/>）。</param>
    /// <param name="Overflow">上限超過で畳んだ件数（0 なら畳みなし）。</param>
    internal sealed record Section(string Title, int TotalCount, IReadOnlyList<Row> Rows, int Overflow);

    /// <summary>
    /// 3 セクション（呼び出し元 / 呼び出し先 / 使用箇所）へ整形した結果。<paramref name="Target"/> は
    /// 見出し（「◯◯ の呼び出し関係」）用のシンボル名（解決できなければ null）。
    /// </summary>
    internal sealed record Result(string? Target, IReadOnlyList<Section> Sections);

    public static Result Build(CallPanels panels) => new(
        panels.Target,
        new[]
        {
            BuildSection("呼び出し元", panels.Incoming),
            BuildSection("呼び出し先", panels.Outgoing),
            BuildSection("使用箇所", panels.References),
        });

    private static Section BuildSection(string title, IReadOnlyList<CallReference> items)
    {
        var shown = Math.Min(items.Count, MaxRows);
        var rows = new List<Row>(shown);
        for (var i = 0; i < shown; i++)
        {
            var it = items[i];
            rows.Add(new Row(
                it.Symbol,
                CodeEditorSupport.DisplayFileName(it.Uri),
                it.Line0 + 1, // 0 始まり(LSP) → 1 始まり(表示・ジャンプ)
                CodeEditorSupport.TryUriToLocalPath(it.Uri)));
        }
        return new Section(title, items.Count, rows, Math.Max(0, items.Count - MaxRows));
    }
}

/// <summary>
/// 言語サーバー未接続／未対応時の案内（<see cref="LspManagementService.EvaluateForFile"/> の結果）を、
/// 表示に必要な純モデルへ整形する。<paramref name="prompt"/> が非 null（未導入／未設定）なら対応サーバー名・
/// インストールコマンド・「インストール」/「導入手順」ボタンの出し分けを、null（＝導入済みだが接続待ち）なら
/// 待機文言のみを返す。ビュー（<see cref="Views.CodeOutlineView"/>）がこのモデルからボタンを配置する。
/// </summary>
internal static class LspNoticeModel
{
    /// <param name="Message">本文（接続待ち文言、または prompt のメッセージ）。</param>
    /// <param name="ServerName">対応サーバー名（無ければ null）。</param>
    /// <param name="InstallCommand">インストールコマンド（無ければ null）。</param>
    /// <param name="DocsUrl">導入手順 URL（無ければ null）。</param>
    /// <param name="Extension">対象拡張子（インストールボタンの再判定ヒント）。</param>
    /// <param name="ShowInstall">「インストール」ボタンを出すか（コマンドがあるとき）。</param>
    /// <param name="ShowDocs">「導入手順」ボタンを出すか（コマンド無し・URL ありのとき）。</param>
    /// <param name="ShowSettings">「LSP 設定を開く」ボタンを出すか（prompt 非 null のとき常に）。</param>
    internal sealed record Notice(
        string Message,
        string? ServerName,
        string? InstallCommand,
        string? DocsUrl,
        string? Extension,
        bool ShowInstall,
        bool ShowDocs,
        bool ShowSettings);

    public static Notice Build(LspPromptInfo? prompt)
    {
        if (prompt is null)
            return new Notice(
                "言語サーバーへの接続待ちです。解析が完了すると、クラス・メソッド等の構造アウトラインと" +
                "呼び出し解析（呼び出し元／呼び出し先／使用箇所）を表示します。",
                null, null, null, null, false, false, false);

        var hasCommand = !string.IsNullOrEmpty(prompt.InstallCommand);
        var hasDocs = !string.IsNullOrEmpty(prompt.DocsUrl);
        return new Notice(
            prompt.Message,
            string.IsNullOrEmpty(prompt.DisplayName) ? null : prompt.DisplayName,
            hasCommand ? prompt.InstallCommand : null,
            hasDocs ? prompt.DocsUrl : null,
            prompt.Extension,
            ShowInstall: hasCommand,
            ShowDocs: !hasCommand && hasDocs,
            ShowSettings: true);
    }
}
