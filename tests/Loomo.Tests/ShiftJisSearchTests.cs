using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Services.Search;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>Shift_JIS のファイル：テキスト検索で当たること、置換で文字コード・BOM を壊さないこと。</summary>
public class ShiftJisSearchTests
{
    // ===== 判定 =====

    [Fact]
    public void Decode_detects_each_encoding()
    {
        const string text = "設定ファイル";
        Assert.Equal("UTF-8", TextFileEncoding.Decode(Encoding.UTF8.GetBytes(text))!.Name);
        Assert.Equal("UTF-8 (BOM)", TextFileEncoding.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)])!.Name);
        Assert.Equal("UTF-16 LE (BOM)", TextFileEncoding.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)])!.Name);

        var sjis = TextFileEncoding.Decode(TextFileEncoding.ShiftJis.GetBytes(text))!;
        Assert.Equal("Shift_JIS", sjis.Name);
        Assert.Equal(text, sjis.Text);
    }

    [Fact]
    public void Decode_refuses_binary()
        => Assert.Null(TextFileEncoding.Decode([0x50, 0x4B, 0x00, 0x03, 0xFF]));

    // ===== 検索 =====

    [Fact]
    public async Task Grep_finds_japanese_in_shift_jis_and_utf8_files_once_each()
    {
        var root = NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "sjis.txt"), TextFileEncoding.ShiftJis.GetBytes("一行目\n設定ファイルの読み込み\n"));
            File.WriteAllText(Path.Combine(root, "utf8.txt"), "設定ファイルの読み込み\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "bom.txt"), "設定ファイルの読み込み\n", new UTF8Encoding(true));
            var svc = NewService(root);

            var hits = await svc.GrepAsync("設定", new GrepOptions(), CancellationToken.None);

            Assert.Equal(new[] { "bom.txt", "sjis.txt", "utf8.txt" }, hits.Select(h => h.RelativePath).OrderBy(p => p));
            var sjisHit = hits.Single(h => h.RelativePath == "sjis.txt");
            Assert.Equal(2, sjisHit.Line);
            Assert.Equal("設定ファイルの読み込み", sjisHit.LineText.TrimEnd());   // 化けずに UTF-8 で届く
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Grep_keeps_every_match_on_the_same_shift_jis_line()
    {
        var root = NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "sjis.txt"), TextFileEncoding.ShiftJis.GetBytes("検索と検索\n"));
            var svc = NewService(root);

            var hits = await svc.GrepAsync("検索", new GrepOptions(), CancellationToken.None);

            // 同じ行の2つ目の一致も残す（行で一意にすると「1件だけ置換」で2つ目を選べなくなる）。
            // 列は文字位置（rg が返す UTF-8 のバイト位置 10 ではなく 4）。
            Assert.Equal(new[] { 1, 4 }, hits.Select(h => h.Column).OrderBy(c => c));
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData("検索と検索", 10, 4)]     // rg は UTF-8 のバイト位置で返す
    [InlineData("abc needle", 5, 5)]       // ASCII だけの行はそのまま
    [InlineData("検索", 1, 1)]
    public void Rg_byte_column_becomes_char_column(string line, int byteColumn, int charColumn)
        => Assert.Equal(charColumn, WorkspaceSearchService.CharColumn(line, byteColumn));

    // ===== 置換 =====

    [Fact]
    public void Replace_keeps_shift_jis_encoding()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "sjis.txt");
            File.WriteAllBytes(path, TextFileEncoding.ShiftJis.GetBytes("設定ファイル\n設定の値\n"));
            var query = NewQuery(root);

            Assert.Equal(2, query.ReplaceInFile(path, "設定", "構成", caseSensitive: false, useRegex: false));

            Assert.Equal("構成ファイル\n構成の値\n", TextFileEncoding.ShiftJis.GetString(File.ReadAllBytes(path)));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Replace_keeps_utf8_bom()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "bom.cs");
            File.WriteAllText(path, "var a = 1;\n", new UTF8Encoding(true));
            var query = NewQuery(root);

            Assert.True(query.ReplaceOneInFile(path, "a", "b", caseSensitive: true, useRegex: false, line: 1, column: 2));

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            Assert.Equal("vbr a = 1;\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Replace_refuses_text_that_shift_jis_cannot_hold()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "sjis.txt");
            var original = TextFileEncoding.ShiftJis.GetBytes("設定\n");
            File.WriteAllBytes(path, original);
            var query = NewQuery(root);

            Assert.Equal(0, query.ReplaceInFile(path, "設定", "😀", caseSensitive: false, useRegex: false));
            Assert.Equal(original, File.ReadAllBytes(path));   // 触っていない
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Replace_leaves_files_with_alternate_cp932_code_points_untouched()
    {
        var root = NewTempDir();
        try
        {
            // EE E0 は NEC 選定 IBM 拡張の「髙」（IBM 拡張 FB FC と同じ文字の別の符号位置）。読んで書き戻すと
            // 別のバイトになりうる文字なので、判定できない＝書き換えない側に倒れること。
            var path = Path.Combine(root, "nec.txt");
            byte[] original = [.. TextFileEncoding.ShiftJis.GetBytes("設定 "), 0xEE, 0xE0, .. TextFileEncoding.ShiftJis.GetBytes("\n")];
            File.WriteAllBytes(path, original);
            var query = NewQuery(root);

            Assert.Equal(0, query.ReplaceInFile(path, "設定", "構成", caseSensitive: false, useRegex: false));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void Replace_one_of_several_matches_keeps_the_rest()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "sjis.txt");
            File.WriteAllBytes(path, TextFileEncoding.ShiftJis.GetBytes("検索と検索\n"));
            var query = NewQuery(root);

            Assert.True(query.ReplaceOneInFile(path, "検索", "探索", caseSensitive: false, useRegex: false, line: 1, column: 4));

            Assert.Equal("検索と探索\n", TextFileEncoding.ShiftJis.GetString(File.ReadAllBytes(path)));
        }
        finally { TryDelete(root); }
    }

    // ===== 補助 =====

    private static WorkspaceSearchService NewService(string root)
    {
        var ws = new FakeWorkspaceService();
        ws.OpenFolder(root);
        return new WorkspaceSearchService(ws, new DocumentTextCache(Path.Combine(root, ".doc-cache")));
    }

    private static SearchPanelQuery NewQuery(string root)
        => new(NewService(root), new SearchResultTreeMapper());

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LoomoSjisTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 一時フォルダーの掃除失敗は無視 */ }
    }
}
