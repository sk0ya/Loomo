using System.Linq;
using System.Text.RegularExpressions;
using Editor.Core.Lsp;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// LSP ベースのコード構造アウトライン（<see cref="CodeEditorSupport"/> / <see cref="LspOutlineRenderer"/> /
/// <see cref="OutlineNode"/>）の純ロジック検証。ライブ言語サーバーは不要で、内部モデル
/// <see cref="OutlineNode"/> だけを組んで、HTML 構造・<c>current</c> 付与・<c>data-line</c>（Line0+1）・
/// 包含判定（<see cref="LspOutlineRenderer.FindEnclosing"/>）・対象拡張子（<see cref="CodeEditorSupport.CanHandle"/>）を確認する。
/// </summary>
public class CodeEditorSupportTests
{
    private static OutlineNode Leaf(string name, SymbolKind kind, int line0, int endLine0)
        => new(name, kind, line0, endLine0, line0, 0, System.Array.Empty<OutlineNode>());

    private static OutlineNode Node(string name, SymbolKind kind, int line0, int endLine0, params OutlineNode[] children)
        => new(name, kind, line0, endLine0, line0, 0, children);

    // ---- CanHandle（対象拡張子）----

    [Theory]
    [InlineData(@"C:\work\Foo.cs")]
    [InlineData(@"C:\work\app.ts")]
    [InlineData(@"C:\work\mod.py")]
    [InlineData(@"C:\work\main.go")]
    [InlineData(@"C:\work\lib.rs")]
    [InlineData(@"C:\work\UPPER.CS")]   // 大文字小文字は無視
    public void CanHandle_コード拡張子は対象(string path)
    {
        Assert.True(new CodeEditorSupport(new AiSettings()).CanHandle(path));
    }

    [Theory]
    [InlineData(@"C:\work\data.json")]   // 専用プロバイダあり
    [InlineData(@"C:\work\doc.md")]      // 専用プロバイダあり
    [InlineData(@"C:\work\conf.xml")]    // 専用プロバイダあり
    [InlineData(@"C:\work\rows.csv")]    // 専用プロバイダあり
    [InlineData(@"C:\work\page.html")]   // ブラウザ提供者あり
    [InlineData(@"C:\work\README")]      // 拡張子なし
    [InlineData(null)]
    public void CanHandle_専用プロバイダ持ちや対象外はfalse(string? path)
    {
        Assert.False(new CodeEditorSupport(new AiSettings()).CanHandle(path));
    }

    [Fact]
    public void DescribeTitle_Codeプレフィックスとファイル名()
    {
        Assert.Equal("Code: Foo.cs", new CodeEditorSupport(new AiSettings()).DescribeTitle(@"C:\work\Foo.cs"));
    }

    // ---- RenderBody（折りたたみ HTML）----

    [Fact]
    public void RenderBody_子ありは折りたたみノード_子なしは行になる()
    {
        var roots = new[]
        {
            Node("Foo", SymbolKind.Class, 0, 10,
                Leaf("Bar", SymbolKind.Method, 2, 5)),
        };

        var body = LspOutlineRenderer.RenderBody(roots, caret: null);

        Assert.Contains("class=\"node\"", body);           // 親（子あり）は折りたたみノード
        Assert.Contains("class=\"line opening\"", body);   // 開閉行
        Assert.Contains("class=\"children\"", body);       // 子コンテナ
        Assert.Contains("Foo", body);
        Assert.Contains("Bar", body);
        Assert.Contains("class=\"goto\"", body);           // ジャンプアイコン
    }

    [Fact]
    public void RenderBody_dataLineはLine0プラス1()
    {
        var roots = new[] { Leaf("Bar", SymbolKind.Method, 41, 50) };

        var body = LspOutlineRenderer.RenderBody(roots, caret: null);

        Assert.Contains("data-line=\"42\"", body);         // 0 始まり(41) → 1 始まり(42)
        Assert.DoesNotContain("data-line=\"41\"", body);
    }

    [Fact]
    public void RenderBody_キャレットを含む最深メンバーにcurrentが付く()
    {
        var roots = new[]
        {
            Node("Foo", SymbolKind.Class, 0, 20,
                Leaf("Bar", SymbolKind.Method, 5, 9),
                Leaf("Baz", SymbolKind.Method, 12, 18)),
        };

        // Baz(12..18) の中（15 行目）
        var body = LspOutlineRenderer.RenderBody(roots, caret: (15, 0));

        // Baz の行だけ current。クラス Foo の開閉行には付かない（最深のみ）。
        Assert.Contains("class=\"line current\" data-line=\"13\"", body); // Baz は data-line 13
        Assert.Contains("class=\"line opening\"", body);                  // Foo は current 無し
        Assert.DoesNotContain("class=\"line opening current\"", body);
    }

    [Fact]
    public void RenderBody_キャレットがメンバー外でも囲むクラスにcurrentが付く()
    {
        var roots = new[]
        {
            Node("Foo", SymbolKind.Class, 0, 20,
                Leaf("Bar", SymbolKind.Method, 5, 9)),
        };

        // クラス内だがどのメソッドの外（2 行目）
        var body = LspOutlineRenderer.RenderBody(roots, caret: (2, 0));

        Assert.Contains("class=\"line opening current\"", body); // 囲む Foo が current
    }

    [Fact]
    public void RenderBody_空ならプレースホルダ()
    {
        var body = LspOutlineRenderer.RenderBody(System.Array.Empty<OutlineNode>(), caret: null);

        Assert.Contains("コード構造がありません", body);
        Assert.DoesNotContain("class=\"node\"", body);
    }

    [Fact]
    public void RenderBody_名前をHTMLエスケープする()
    {
        var roots = new[] { Leaf("op<T>", SymbolKind.Method, 0, 0) };

        var body = LspOutlineRenderer.RenderBody(roots, caret: null);

        Assert.Contains("op&lt;T&gt;", body);
        Assert.DoesNotContain("op<T>", body);
    }

    // ---- FindEnclosing（包含判定）----

    [Fact]
    public void FindEnclosing_メンバー内なら最深メンバーを返す()
    {
        var bar = Leaf("Bar", SymbolKind.Method, 5, 9);
        var roots = new[] { Node("Foo", SymbolKind.Class, 0, 20, bar) };

        var hit = LspOutlineRenderer.FindEnclosing(roots, 7, 0);

        Assert.Same(bar, hit);
    }

    [Fact]
    public void FindEnclosing_メソッド外でも囲むクラスを返す()
    {
        var foo = Node("Foo", SymbolKind.Class, 0, 20, Leaf("Bar", SymbolKind.Method, 5, 9));
        var roots = new[] { foo };

        var hit = LspOutlineRenderer.FindEnclosing(roots, 2, 0);

        Assert.Same(foo, hit);
    }

    [Theory]
    [InlineData(5)]   // 開始行（含む）
    [InlineData(9)]   // 終了行（含む）
    public void FindEnclosing_境界行は含む(int line0)
    {
        var bar = Leaf("Bar", SymbolKind.Method, 5, 9);
        var roots = new[] { Node("Foo", SymbolKind.Class, 0, 20, bar) };

        Assert.Same(bar, LspOutlineRenderer.FindEnclosing(roots, line0, 0));
    }

    [Fact]
    public void FindEnclosing_どのノードにも含まれなければnull()
    {
        var roots = new[] { Node("Foo", SymbolKind.Class, 0, 20, Leaf("Bar", SymbolKind.Method, 5, 9)) };

        Assert.Null(LspOutlineRenderer.FindEnclosing(roots, 21, 0)); // 全範囲の外
    }

    // ---- URI → ローカルパス / 表示名 ----

    [Fact]
    public void TryUriToLocalPath_fileURIをローカルパスへ変換()
    {
        Assert.Equal(@"C:\work\Foo.cs", CodeEditorSupport.TryUriToLocalPath("file:///C:/work/Foo.cs"));
    }

    [Fact]
    public void TryUriToLocalPath_パーセントエンコードをデコード()
    {
        // %20（空白）を含む file URI もデコードされる。
        Assert.Equal(@"C:\my dir\Foo.cs", CodeEditorSupport.TryUriToLocalPath("file:///C:/my%20dir/Foo.cs"));
    }

    [Fact]
    public void TryUriToLocalPath_fileでないものはそのまま_空はnull()
    {
        Assert.Equal(@"C:\work\Foo.cs", CodeEditorSupport.TryUriToLocalPath(@"C:\work\Foo.cs")); // 既にパス
        Assert.Null(CodeEditorSupport.TryUriToLocalPath(null));
        Assert.Null(CodeEditorSupport.TryUriToLocalPath(""));
    }

    [Fact]
    public void DisplayFileName_URIからファイル名だけ取り出す()
    {
        Assert.Equal("Foo.cs", CodeEditorSupport.DisplayFileName("file:///C:/work/Foo.cs"));
        Assert.Equal("Foo.cs", CodeEditorSupport.DisplayFileName(@"C:\work\Foo.cs"));
        Assert.Equal("", CodeEditorSupport.DisplayFileName(null));
    }

    // ---- 呼び出し解析パネルの整形 ----

    private static CallPanels MakePanels(
        CallReference[]? incoming = null, CallReference[]? outgoing = null, CallReference[]? references = null)
        => new(
            incoming ?? System.Array.Empty<CallReference>(),
            outgoing ?? System.Array.Empty<CallReference>(),
            references ?? System.Array.Empty<CallReference>());

    [Fact]
    public void RenderPanels_3セクションの見出しを出す()
    {
        var html = CallPanelRenderer.RenderPanels(CallPanels.Empty);

        Assert.Contains("呼び出し元", html);
        Assert.Contains("呼び出し先", html);
        Assert.Contains("使用箇所", html);
    }

    [Fact]
    public void RenderPanels_空セクションはなしを出す()
    {
        var html = CallPanelRenderer.RenderPanels(CallPanels.Empty);

        Assert.Contains("（なし）", html);
        Assert.DoesNotContain("class=\"call-row\"", html);
    }

    [Fact]
    public void RenderPanels_呼び出し元行はシンボル名とファイル名と行を出す_dataLineは1始まり()
    {
        var panels = MakePanels(
            incoming: new[] { new CallReference("Caller", "file:///C:/work/Foo.cs", 41) });

        var html = CallPanelRenderer.RenderPanels(panels);

        Assert.Contains("data-line=\"42\"", html);            // 0 始まり(41) → 1 始まり(42)
        Assert.Contains("<span class=\"k\">Caller</span>", html);
        Assert.Contains("Foo.cs:42", html);                   // ファイル名:行
        Assert.Contains("data-path=\"C:\\work\\Foo.cs\"", html); // ジャンプ用ローカルパス
    }

    [Fact]
    public void RenderPanels_使用箇所はシンボル名を持たずファイル名と行だけ()
    {
        var panels = MakePanels(
            references: new[] { new CallReference("", "file:///C:/work/Bar.cs", 9) });

        var html = CallPanelRenderer.RenderPanels(panels);

        Assert.Contains("Bar.cs:10", html);            // 9 → 10
        Assert.Contains("data-path=\"C:\\work\\Bar.cs\"", html);
        Assert.DoesNotContain("<span class=\"k\">", html); // 名前欄は出さない
    }

    [Fact]
    public void RenderPanels_パス変換できない対象はdataPath無し()
    {
        // 絶対 file URI でもローカルパスでもない対象は data-path を付けず（ジャンプ不可）表示だけする。
        var panels = MakePanels(
            outgoing: new[] { new CallReference("Ext", "untitled:Untitled-1", 0) });

        var html = CallPanelRenderer.RenderPanels(panels);

        Assert.Contains("<span class=\"k\">Ext</span>", html);
        Assert.DoesNotContain("data-path=", html);
    }

    [Fact]
    public void RenderPanels_シンボル名をHTMLエスケープする()
    {
        var panels = MakePanels(
            incoming: new[] { new CallReference("op<T>", "file:///C:/work/Foo.cs", 0) });

        var html = CallPanelRenderer.RenderPanels(panels);

        Assert.Contains("op&lt;T&gt;", html);
        Assert.DoesNotContain("<span class=\"k\">op<T>", html);
    }

    // ---- ToOutline（DocumentSymbol → OutlineNode の写し）----

    [Fact]
    public void ToOutline_RangeとSelectionRangeから行と名前位置を写す()
    {
        var sym = new DocumentSymbol(
            "Bar", SymbolKind.Method,
            new LspRange(new LspPosition(10, 4), new LspPosition(20, 5)),   // Range＝本体
            new LspRange(new LspPosition(11, 8), new LspPosition(11, 11)),  // SelectionRange＝名前
            System.Array.Empty<DocumentSymbol>());

        var node = Assert.Single(CodeEditorSupport.ToOutline(new[] { sym }));

        Assert.Equal(10, node.Line0);       // Range.Start.Line
        Assert.Equal(20, node.EndLine0);    // Range.End.Line
        Assert.Equal(11, node.NameLine0);   // SelectionRange.Start.Line（シンボル名の行）
        Assert.Equal(8, node.NameCol0);     // SelectionRange.Start.Character（シンボル名の列）
    }

    // ---- 呼び出し/参照の件数上限（タスク3）----

    [Fact]
    public void RenderPanels_上限超過は切り詰めて他N件を出す()
    {
        var many = Enumerable.Range(0, CallPanelRenderer.MaxRows + 5)
            .Select(i => new CallReference("R" + i, "file:///C:/work/F.cs", i))
            .ToArray();

        var html = CallPanelRenderer.RenderPanels(MakePanels(references: many));

        var rows = Regex.Matches(html, "class=\"call-row\"").Count;
        Assert.Equal(CallPanelRenderer.MaxRows, rows);   // 表示は上限まで
        Assert.Contains("他 5 件", html);                // 残数
        Assert.Contains("class=\"call-more\"", html);    // 切り詰めの行（CSS 定義ではなく実要素）
    }

    [Fact]
    public void RenderPanels_ちょうど上限なら他N件を出さない()
    {
        var exactly = Enumerable.Range(0, CallPanelRenderer.MaxRows)
            .Select(i => new CallReference("R" + i, "file:///C:/work/F.cs", i))
            .ToArray();

        var html = CallPanelRenderer.RenderPanels(MakePanels(references: exactly));

        Assert.DoesNotContain("class=\"call-more\"", html);  // 切り詰め行は出ない（CSS 定義文字列は無視）
        Assert.Equal(CallPanelRenderer.MaxRows, Regex.Matches(html, "class=\"call-row\"").Count);
    }

    [Fact]
    public void RenderPanelsInner_styleを含まずcallPanelsのdivだけ返す()
    {
        var inner = CallPanelRenderer.RenderPanelsInner(CallPanels.Empty);

        Assert.StartsWith("<div class=\"call-panels\">", inner);
        Assert.DoesNotContain("<style>", inner);   // 部分更新用は CSS を含まない
    }

    // ---- 案内ページ（LspNoticeRenderer / タスク1）----

    [Fact]
    public void Notice_未導入_サーバー名とコマンドとインストールボタンを出す()
    {
        var info = new LspPromptInfo(
            ".rs", LspPromptKind.NotInstalled,
            "「.rs」の言語サーバー rust-analyzer が見つかりません。",
            "rustup component add rust-analyzer", "rust-analyzer", "https://example/docs");

        var html = LspNoticeRenderer.RenderBody(@"C:\work\a.rs", info);

        Assert.Contains("rust-analyzer", html);                       // 対応サーバー名
        Assert.Contains("rustup component add rust-analyzer", html);  // インストールコマンド
        Assert.Contains("class=\"lsp-install-btn\"", html);
        Assert.Contains("data-ext=\".rs\"", html);
        Assert.Contains("class=\"lsp-settings-btn\"", html);          // 設定を開くは常に
        Assert.DoesNotContain("lsp-docs-btn", html);                 // コマンドがあるので手順ボタンは出さない
    }

    [Fact]
    public void Notice_コマンド無しでDocsのみ_導入手順ボタンを出す()
    {
        var info = new LspPromptInfo(
            ".foo", LspPromptKind.NotInstalled,
            "「.foo」の言語サーバー Foo が未設定です。", null, "Foo", "https://example/foo");

        var html = LspNoticeRenderer.RenderBody(@"C:\work\a.foo", info);

        Assert.DoesNotContain("lsp-install-btn", html);
        Assert.Contains("class=\"lsp-docs-btn\"", html);
        Assert.Contains("data-url=\"https://example/foo\"", html);
        Assert.Contains("class=\"lsp-settings-btn\"", html);
    }

    [Fact]
    public void Notice_未設定は設定ボタンのみ()
    {
        var info = new LspPromptInfo(
            ".zzz", LspPromptKind.NotConfigured,
            "「.zzz」に対応する言語サーバーが設定されていません。", null, null, null);

        var html = LspNoticeRenderer.RenderBody(@"C:\work\a.zzz", info);

        Assert.DoesNotContain("lsp-install-btn", html);
        Assert.DoesNotContain("lsp-docs-btn", html);
        Assert.Contains("class=\"lsp-settings-btn\"", html);
    }

    [Fact]
    public void Notice_null_接続待ち文言でボタンを出さない()
    {
        var html = LspNoticeRenderer.RenderBody(@"C:\work\a.cs", prompt: null);

        Assert.Contains("接続待ち", html);
        Assert.DoesNotContain("lsp-install-btn", html);
        Assert.DoesNotContain("lsp-settings-btn", html);
    }
}
