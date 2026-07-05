using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// LSP ベースのコード構造アウトライン＋②呼び出し解析の<b>純ロジック層</b>（<see cref="CodeEditorSupport"/> /
/// <see cref="CodeOutline"/> / <see cref="CallPanelModel"/> / <see cref="LspNoticeModel"/> /
/// <see cref="OutlineNode"/>）の検証。表示（ネイティブ WPF <see cref="App.Views.CodeOutlineView"/>）は
/// この層のモデルを描くだけなので、ここではモデル生成・包含判定・件数上限・案内の出し分け・拡張子判定を確認する
/// （2026-07：HTML 描画から WPF へ移行したため、旧 HTML 構造アサートはモデルアサートへ置換）。
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
        Assert.True(new CodeEditorSupport().CanHandle(path));
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
        Assert.False(new CodeEditorSupport().CanHandle(path));
    }

    [Fact]
    public void DescribeTitle_Codeプレフィックスとファイル名()
    {
        Assert.Equal("Code: Foo.cs", new CodeEditorSupport().DescribeTitle(@"C:\work\Foo.cs"));
    }

    // ---- KindBadge（種別バッジ）----

    [Theory]
    [InlineData(SymbolKind.Class, "C", "class")]
    [InlineData(SymbolKind.Method, "M", "method")]
    [InlineData(SymbolKind.Property, "P", "property")]
    [InlineData(SymbolKind.Interface, "I", "interface")]
    public void KindBadge_種別ごとのグリフとツールチップ(SymbolKind kind, string glyph, string title)
    {
        var (g, colorHex, t) = CodeOutline.KindBadge(kind);
        Assert.Equal(glyph, g);
        Assert.Equal(title, t);
        Assert.StartsWith("#", colorHex);   // 色は #RRGGBB
    }

    // ---- FindEnclosing（包含判定）----

    [Fact]
    public void FindEnclosing_メンバー内なら最深メンバーを返す()
    {
        var bar = Leaf("Bar", SymbolKind.Method, 5, 9);
        var roots = new[] { Node("Foo", SymbolKind.Class, 0, 20, bar) };

        var hit = CodeOutline.FindEnclosing(roots, 7, 0);

        Assert.Same(bar, hit);
    }

    [Fact]
    public void FindEnclosing_メソッド外でも囲むクラスを返す()
    {
        var foo = Node("Foo", SymbolKind.Class, 0, 20, Leaf("Bar", SymbolKind.Method, 5, 9));
        var roots = new[] { foo };

        var hit = CodeOutline.FindEnclosing(roots, 2, 0);

        Assert.Same(foo, hit);
    }

    [Theory]
    [InlineData(5)]   // 開始行（含む）
    [InlineData(9)]   // 終了行（含む）
    public void FindEnclosing_境界行は含む(int line0)
    {
        var bar = Leaf("Bar", SymbolKind.Method, 5, 9);
        var roots = new[] { Node("Foo", SymbolKind.Class, 0, 20, bar) };

        Assert.Same(bar, CodeOutline.FindEnclosing(roots, line0, 0));
    }

    [Fact]
    public void FindEnclosing_どのノードにも含まれなければnull()
    {
        var roots = new[] { Node("Foo", SymbolKind.Class, 0, 20, Leaf("Bar", SymbolKind.Method, 5, 9)) };

        Assert.Null(CodeOutline.FindEnclosing(roots, 21, 0)); // 全範囲の外
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

    // ---- CallPanelModel（②呼び出し解析の整形）----

    private static CallPanels MakePanels(
        CallReference[]? incoming = null, CallReference[]? outgoing = null,
        CallReference[]? references = null, string? target = null)
        => new(
            incoming ?? System.Array.Empty<CallReference>(),
            outgoing ?? System.Array.Empty<CallReference>(),
            references ?? System.Array.Empty<CallReference>(),
            target);

    [Fact]
    public void Build_3セクションを呼び出し元_呼び出し先_使用箇所の順で返す()
    {
        var result = CallPanelModel.Build(CallPanels.Empty);

        Assert.Collection(result.Sections,
            s => Assert.Equal("呼び出し元", s.Title),
            s => Assert.Equal("呼び出し先", s.Title),
            s => Assert.Equal("使用箇所", s.Title));
        Assert.All(result.Sections, s => Assert.Empty(s.Rows));
    }

    [Fact]
    public void Build_呼び出し元行はシンボル名とファイル名と行1始まりとパスを持つ()
    {
        var result = CallPanelModel.Build(MakePanels(
            incoming: new[] { new CallReference("Caller", "file:///C:/work/Foo.cs", 41) }));

        var row = Assert.Single(result.Sections[0].Rows);
        Assert.Equal("Caller", row.Symbol);
        Assert.Equal("Foo.cs", row.FileName);
        Assert.Equal(42, row.Line1);                  // 0 始まり(41) → 1 始まり(42)
        Assert.Equal(@"C:\work\Foo.cs", row.Path);    // ジャンプ用ローカルパス
    }

    [Fact]
    public void Build_使用箇所はシンボル名を持たずファイル名と行だけ()
    {
        var result = CallPanelModel.Build(MakePanels(
            references: new[] { new CallReference("", "file:///C:/work/Bar.cs", 9) }));

        var row = Assert.Single(result.Sections[2].Rows);
        Assert.Equal("", row.Symbol);
        Assert.Equal("Bar.cs", row.FileName);
        Assert.Equal(10, row.Line1);                  // 9 → 10
        Assert.Equal(@"C:\work\Bar.cs", row.Path);
    }

    [Fact]
    public void Build_パス変換できない対象はPathがnull()
    {
        // 絶対 file URI でもローカルパスでもない対象は Path を付けない（ジャンプ不可）。
        var result = CallPanelModel.Build(MakePanels(
            outgoing: new[] { new CallReference("Ext", "untitled:Untitled-1", 0) }));

        var row = Assert.Single(result.Sections[1].Rows);
        Assert.Equal("Ext", row.Symbol);
        Assert.Null(row.Path);
    }

    [Fact]
    public void Build_上限超過は切り詰めてOverflowに残数を入れる()
    {
        var many = Enumerable.Range(0, CallPanelModel.MaxRows + 5)
            .Select(i => new CallReference("R" + i, "file:///C:/work/F.cs", i))
            .ToArray();

        var section = CallPanelModel.Build(MakePanels(references: many)).Sections[2];

        Assert.Equal(CallPanelModel.MaxRows, section.Rows.Count);   // 表示は上限まで
        Assert.Equal(CallPanelModel.MaxRows + 5, section.TotalCount); // 総件数は保持
        Assert.Equal(5, section.Overflow);                          // 畳んだ残数
    }

    [Fact]
    public void Build_ちょうど上限ならOverflowは0()
    {
        var exactly = Enumerable.Range(0, CallPanelModel.MaxRows)
            .Select(i => new CallReference("R" + i, "file:///C:/work/F.cs", i))
            .ToArray();

        var section = CallPanelModel.Build(MakePanels(references: exactly)).Sections[2];

        Assert.Equal(CallPanelModel.MaxRows, section.Rows.Count);
        Assert.Equal(0, section.Overflow);
    }

    [Fact]
    public void Build_Targetはそのまま持ち越す()
    {
        Assert.Equal("Foo", CallPanelModel.Build(MakePanels(target: "Foo")).Target);
        Assert.Null(CallPanelModel.Build(CallPanels.Empty).Target);
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
        Assert.Equal("", node.Detail);      // 本文未指定なら Detail は空
    }

    [Fact]
    public void ToOutline_本文の宣言行からシグネチャをDetailに入れる()
    {
        // 宣言行（0 始まり 1 行目）で "Foo" は 12 列目から始まる。
        var lines = new[] { "// header", "public Task Foo(int x, string s)" };
        var sym = new DocumentSymbol(
            "Foo", SymbolKind.Method,
            new LspRange(new LspPosition(1, 0), new LspPosition(3, 1)),
            new LspRange(new LspPosition(1, 12), new LspPosition(1, 15)),  // "Foo" の位置
            System.Array.Empty<DocumentSymbol>());

        var node = Assert.Single(CodeEditorSupport.ToOutline(new[] { sym }, lines));

        Assert.Equal("(int x, string s)", node.Detail);   // 名前の直後〜本体手前
    }

    [Fact]
    public void ToOutline_子シンボルを再帰的に写す()
    {
        var child = new DocumentSymbol(
            "Bar", SymbolKind.Method,
            new LspRange(new LspPosition(5, 0), new LspPosition(9, 1)),
            new LspRange(new LspPosition(5, 4), new LspPosition(5, 7)),
            System.Array.Empty<DocumentSymbol>());
        var parent = new DocumentSymbol(
            "Foo", SymbolKind.Class,
            new LspRange(new LspPosition(0, 0), new LspPosition(20, 1)),
            new LspRange(new LspPosition(0, 6), new LspPosition(0, 9)),
            new[] { child });

        var node = Assert.Single(CodeEditorSupport.ToOutline(new[] { parent }));

        Assert.Equal("Foo", node.Name);
        var c = Assert.Single(node.Children);
        Assert.Equal("Bar", c.Name);
        Assert.Equal(5, c.Line0);
    }

    // ---- ★1 シグネチャ抽出（SignatureExtractor）----

    [Theory]
    [InlineData("public Task Foo(int x)", 12, "Foo", "(int x)")]                 // メソッド引数
    [InlineData("    void Bar() {", 9, "Bar", "")]                                // 引数なし＋本体 → 空
    [InlineData("def greet(name: str) -> str:", 4, "greet", "(name: str) -> str")] // Python 後置戻り型（":"末尾は残す）
    [InlineData("private readonly int _count;", 21, "_count", "")]                // フィールド（記号のみ）→ 空
    [InlineData("int Sum => a + b;", 4, "Sum", "")]                               // 式本体 "=>" で切る → 空
    public void SignatureExtractor_宣言行から名前の直後を切り出す(
        string line, int nameCol0, string name, string expected)
    {
        Assert.Equal(expected, SignatureExtractor.Extract(new[] { line }, 0, nameCol0, name));
    }

    [Fact]
    public void SignatureExtractor_列がズレていても行内検索でフォールバック()
    {
        // SelectionRange の列が名前と一致しない場合でも、行内から名前を探して直後を取る。
        Assert.Equal("(x)", SignatureExtractor.Extract(new[] { "fn foo(x)" }, 0, 99, "foo"));
    }

    [Fact]
    public void SignatureExtractor_本文なし_行外_空名は空を返す()
    {
        Assert.Equal("", SignatureExtractor.Extract(null, 0, 0, "Foo"));
        Assert.Equal("", SignatureExtractor.Extract(new[] { "x" }, 5, 0, "Foo"));   // 行 index 範囲外
        Assert.Equal("", SignatureExtractor.Extract(new[] { "" }, 0, 0, ""));
    }

    [Fact]
    public void SignatureExtractor_長すぎるシグネチャは丸める()
    {
        var longArgs = "(" + string.Join(", ", Enumerable.Range(0, 40).Select(i => "int a" + i)) + ")";
        var line = "void M" + longArgs;
        var sig = SignatureExtractor.Extract(new[] { line }, 0, 5, "M");

        Assert.True(sig.Length <= SignatureExtractor.MaxLength + 1); // +1 は「…」
        Assert.EndsWith("…", sig);
    }

    // ---- LspNoticeModel（案内の出し分け）----

    [Fact]
    public void Notice_未導入_サーバー名とコマンドとインストールボタンを出す()
    {
        var info = new LspPromptInfo(
            ".rs", LspPromptKind.NotInstalled,
            "「.rs」の言語サーバー rust-analyzer が見つかりません。",
            "rustup component add rust-analyzer", "rust-analyzer", "https://example/docs");

        var notice = LspNoticeModel.Build(info);

        Assert.Equal("rust-analyzer", notice.ServerName);
        Assert.Equal("rustup component add rust-analyzer", notice.InstallCommand);
        Assert.Equal(".rs", notice.Extension);
        Assert.True(notice.ShowInstall);
        Assert.True(notice.ShowSettings);   // 設定を開くは常に
        Assert.False(notice.ShowDocs);      // コマンドがあるので手順ボタンは出さない
    }

    [Fact]
    public void Notice_コマンド無しでDocsのみ_導入手順ボタンを出す()
    {
        var info = new LspPromptInfo(
            ".foo", LspPromptKind.NotInstalled,
            "「.foo」の言語サーバー Foo が未設定です。", null, "Foo", "https://example/foo");

        var notice = LspNoticeModel.Build(info);

        Assert.False(notice.ShowInstall);
        Assert.True(notice.ShowDocs);
        Assert.Equal("https://example/foo", notice.DocsUrl);
        Assert.True(notice.ShowSettings);
    }

    [Fact]
    public void Notice_未設定は設定ボタンのみ()
    {
        var info = new LspPromptInfo(
            ".zzz", LspPromptKind.NotConfigured,
            "「.zzz」に対応する言語サーバーが設定されていません。", null, null, null);

        var notice = LspNoticeModel.Build(info);

        Assert.False(notice.ShowInstall);
        Assert.False(notice.ShowDocs);
        Assert.True(notice.ShowSettings);
    }

    [Fact]
    public void Notice_null_接続待ち文言でボタンを出さない()
    {
        var notice = LspNoticeModel.Build(prompt: null);

        Assert.Contains("接続待ち", notice.Message);
        Assert.False(notice.ShowInstall);
        Assert.False(notice.ShowDocs);
        Assert.False(notice.ShowSettings);
    }
}
