using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Editor.Controls.Formatting;
using Editor.Core.Formatting;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.Services.Formatting;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 保存時整形（全言語の formatOnSave）と .editorconfig 空白規則の検証。
/// CLI の実行と PATH 判定は差し替え、実際の整形ツールには依存しない。
/// </summary>
public sealed class SaveFormattingTests : IDisposable
{
    private readonly string _dir;
    private readonly FormatterRegistry _registry;

    public SaveFormattingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "loomo-savefmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registry = new FormatterRegistry(Path.Combine(_dir, "formatters.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    // ── 判定 ───────────────────────────────────────────────────────────

    [Fact]
    public void 既定では保存時整形は走らずeditorconfig空白規則は走る()
    {
        var settings = new LoomoSettings().Editor;
        Assert.False(settings.FormatOnSave);
        Assert.True(settings.ApplyEditorConfigOnSave);
        Assert.False(FormatOnSavePolicy.ShouldFormat(PathOf("a.ts"), settings));
        Assert.True(FormatOnSavePolicy.ShouldApplyEditorConfig(PathOf("a.ts"), settings));
    }

    [Fact]
    public void 除外した拡張子は保存時整形しない()
    {
        var settings = new EditorSettings
        {
            FormatOnSave = true,
            FormatOnSaveExcludedExtensions = FormatOnSavePolicy.ParseExtensions("md, .YML"),
        };
        Assert.False(FormatOnSavePolicy.ShouldFormat(PathOf("README.md"), settings));
        Assert.False(FormatOnSavePolicy.ShouldFormat(PathOf("ci.yml"), settings));
        Assert.True(FormatOnSavePolicy.ShouldFormat(PathOf("main.ts"), settings));
        Assert.False(FormatOnSavePolicy.ShouldFormat(PathOf("Makefile"), settings));
    }

    [Fact]
    public void CSharpCleanupがONならcsは汎用処理を重ねない()
    {
        var settings = new EditorSettings { FormatOnSave = true, CleanCSharpOnSave = true };
        Assert.True(FormatOnSavePolicy.IsHandledByCSharpCleanup(PathOf("A.cs"), settings));
        Assert.False(FormatOnSavePolicy.ShouldFormat(PathOf("A.cs"), settings));
        Assert.False(FormatOnSavePolicy.ShouldApplyEditorConfig(PathOf("A.cs"), settings));
        // C# 以外には影響しない。
        Assert.True(FormatOnSavePolicy.ShouldFormat(PathOf("a.ts"), settings));

        settings.CleanCSharpOnSave = false;
        Assert.True(FormatOnSavePolicy.ShouldFormat(PathOf("A.cs"), settings));
    }

    [Fact]
    public void 拡張子の入力を正規化し重複を落とす()
    {
        Assert.Equal([".md", ".yml", ".json"],
            FormatOnSavePolicy.ParseExtensions(" md .yml;.MD、json  ,"));
        Assert.Empty(FormatOnSavePolicy.ParseExtensions("  "));
        Assert.Equal(".md .yml", FormatOnSavePolicy.FormatExtensions([".md", ".yml"]));
    }

    // ── .editorconfig 空白規則 ───────────────────────────────────────────

    [Fact]
    public void 規則が書かれていなければ本文を変えない()
    {
        const string text = "line  \nnext\t";   // Markdown の行末2スペース改行を含む
        Assert.Same(text, EditorConfigWhitespace.Apply(text, EditorConfigWhitespaceRules.None));
        Assert.True(EditorConfigWhitespaceRules.None.IsEmpty);
    }

    [Fact]
    public void 行末空白を削り末尾改行を足す()
    {
        var rules = new EditorConfigWhitespaceRules(true, true, null);
        Assert.Equal("a\n\tb\n\nc\n", EditorConfigWhitespace.Apply("a  \n\tb\t\n  \nc", rules));
    }

    [Fact]
    public void 全角スペースは行末空白として削らない()
    {
        var rules = new EditorConfigWhitespaceRules(true, null, null);
        Assert.Equal("a　", EditorConfigWhitespace.Apply("a　 ", rules));
    }

    [Fact]
    public void 末尾改行は空ファイルへ足さずfalseでも既存の改行を削らない()
    {
        Assert.Equal("", EditorConfigWhitespace.Apply("", new(null, true, null)));
        Assert.Equal("a\n", EditorConfigWhitespace.Apply("a\n", new(null, true, null)));
        Assert.Equal("a\n", EditorConfigWhitespace.Apply("a\n", new(null, false, null)));
        Assert.Equal("a", EditorConfigWhitespace.Apply("a", new(null, false, null)));
    }

    [Fact]
    public void editorconfigの明示設定だけを規則として読む()
    {
        File.WriteAllText(PathOf(".editorconfig"),
            "root = true\n\n[*]\ninsert_final_newline = true\nend_of_line = crlf\n\n" +
            "[*.ts]\ntrim_trailing_whitespace = true\n\n[*.md]\ninsert_final_newline = unset\n");
        var service = new CSharpEditorConfigService();

        var ts = EditorConfigWhitespaceRules.From(service.Resolve(PathOf("a.ts")).Get);
        Assert.Equal(new EditorConfigWhitespaceRules(true, true, "dos"), ts);

        var md = EditorConfigWhitespaceRules.From(service.Resolve(PathOf("README.md")).Get);
        Assert.Null(md.TrimTrailingWhitespace);   // 書かれていない＝行末2スペースを守る
        Assert.Null(md.InsertFinalNewline);       // unset は書かれていない扱い
        Assert.Equal("dos", md.FileFormat);
    }

    // ── 整形の経路 ─────────────────────────────────────────────────────

    [Fact]
    public async Task 設定済みのCLI整形がLSPより優先される()
    {
        _registry.Set(".ts", new FormatterDef("fmt", ["--stdin", "{file}"]));
        var lsp = new FakeLspDocument(PathOf("a.ts"), null)
        {
            Formatting = () => throw new InvalidOperationException("LSP は呼ばれないはず"),
        };
        string? seenPath = null;
        var formatter = new SaveFormatter(
            (def, path, input, _) =>
            {
                seenPath = path;
                return new FormatterRunner.RunResult(true, input.ToUpperInvariant() + "\r\n", null);
            },
            _ => false);

        var result = await formatter.FormatAsync(PathOf("a.ts"), "let a\nlet b", _registry, lsp, 2, true);

        Assert.Null(result.Error);
        Assert.Equal("LET A\nLET B\n", result.Text);   // CRLF はバッファの \n へ揃える
        Assert.Equal("fmt", result.FormatterName);
        Assert.Equal(PathOf("a.ts"), seenPath);
    }

    [Fact]
    public async Task CLI整形が無ければ言語サーバーの整形を使う()
    {
        var lsp = new FakeLspDocument(PathOf("a.rs"), null)
        {
            Formatting = () => Task.FromResult<IReadOnlyList<LspTextEdit>>(
                [new LspTextEdit(new LspRange(new LspPosition(0, 0), new LspPosition(0, 2)), "fn")]),
        };
        var formatter = new SaveFormatter((_, _, _, _) => throw new InvalidOperationException(), _ => false);

        var result = await formatter.FormatAsync(PathOf("a.rs"), "FN main", _registry, lsp, 4, true);

        Assert.Equal("fn main", result.Text);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task 言語サーバーが応答しなければ時間で見切って本文を変えない()
    {
        var never = new TaskCompletionSource<IReadOnlyList<LspTextEdit>>();
        var lsp = new FakeLspDocument(PathOf("a.rs"), null) { Formatting = () => never.Task };
        var formatter = new SaveFormatter((_, _, _, _) => throw new InvalidOperationException(), _ => false);

        var result = await formatter.FormatAsync(PathOf("a.rs"), "x", _registry, lsp, 4, true,
            TimeSpan.FromMilliseconds(50));

        Assert.Null(result.Text);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task PATH上の既知整形ツールを使い対応表へ登録する()
    {
        var formatter = new SaveFormatter(
            (def, _, input, _) => new FormatterRunner.RunResult(true, input.Trim() + "\n", null),
            exe => exe == "prettier");

        var result = await formatter.FormatAsync(PathOf("a.json"), "{ }  ", _registry, null, 2, true);

        Assert.Equal("{ }\n", result.Text);
        Assert.Equal("prettier", _registry.GetForExtension(".json")?.Executable);
    }

    [Fact]
    public async Task 整形ツールの失敗は例外にせず本文を変えない()
    {
        _registry.Set(".py", new FormatterDef("black", ["-q", "-"]));
        var formatter = new SaveFormatter(
            (_, _, _, _) => new FormatterRunner.RunResult(false, null, "cannot parse\nline 2"),
            _ => true);

        var result = await formatter.FormatAsync(PathOf("a.py"), "x = (", _registry, null, 4, true);

        Assert.Null(result.Text);
        Assert.Equal("black: cannot parse …", result.Error);
    }

    [Fact]
    public async Task 整形できる手段が無ければ何もしない()
    {
        var formatter = new SaveFormatter((_, _, _, _) => throw new InvalidOperationException(), _ => false);

        var result = await formatter.FormatAsync(PathOf("a.unknownext"), "x", _registry, null, 4, true);

        Assert.Equal(SaveFormatResult.Unchanged, result);
    }

    // ── 永続化 ─────────────────────────────────────────────────────────

    [Fact]
    public void 保存時整形の設定を保存して読み戻す()
    {
        var path = PathOf("settings.json");
        var saved = new LoomoSettings();
        saved.Editor.FormatOnSave = true;
        saved.Editor.FormatOnSaveExcludedExtensions = [".md", ".yml"];
        saved.Editor.ApplyEditorConfigOnSave = false;
        new SettingsStore(path).Save(saved);

        var loaded = new LoomoSettings();
        new SettingsStore(path).Load(loaded);

        Assert.True(loaded.Editor.FormatOnSave);
        Assert.Equal([".md", ".yml"], loaded.Editor.FormatOnSaveExcludedExtensions);
        Assert.False(loaded.Editor.ApplyEditorConfigOnSave);
    }
}
