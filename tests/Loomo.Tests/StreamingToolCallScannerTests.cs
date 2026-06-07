using System.Collections.Generic;
using System.Text.Json;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Models;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ストリーム中のツール呼び出し配列を逐次検出する <see cref="StreamingToolCallScanner"/> の検証。
/// 配列直下のオブジェクトが閉じるたびに（全文確定を待たず）ToolUse を返すこと、文字列内の括弧を誤検出しないこと、
/// 非配列は無効化して終端パーサに委ねること、不正要素以降を捨てて先頭の正しい連続分だけ活かすことを確認する。
/// </summary>
public class StreamingToolCallScannerTests
{
    private static List<ToolUse> FeedAll(params string[] chunks)
    {
        var scanner = new StreamingToolCallScanner();
        var all = new List<ToolUse>();
        foreach (var c in chunks) all.AddRange(scanner.Feed(c));
        return all;
    }

    [Fact]
    public void Emits_single_object_array_in_one_chunk()
    {
        var t = Assert.Single(FeedAll("[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}]"));
        Assert.Equal("run_powershell", t.Name);
        Assert.Equal("{\"command\":\"ls\"}", t.ArgumentsJson);
    }

    [Fact]
    public void Emits_phi4_tool_call_wrapped_array()
    {
        var t = Assert.Single(FeedAll("<|tool_call|>[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}]<|/tool_call|>"));
        Assert.Equal("run_powershell", t.Name);
        Assert.Equal("{\"command\":\"ls\"}", t.ArgumentsJson);
    }

    [Fact]
    public void Emits_multiple_objects_in_order_in_one_chunk()
    {
        var tools = FeedAll(
            "[{\"name\":\"write_file\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"A\"}}," +
            "{\"name\":\"write_file\",\"arguments\":{\"path\":\"b.txt\",\"content\":\"B\"}}]");
        Assert.Equal(2, tools.Count);
        using (var a = JsonDocument.Parse(tools[0].ArgumentsJson))
            Assert.Equal("a.txt", a.RootElement.GetProperty("path").GetString());
        using (var b = JsonDocument.Parse(tools[1].ArgumentsJson))
            Assert.Equal("b.txt", b.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void Emits_each_object_as_it_closes_across_chunks()
    {
        // 1件目は閉じた時点で即返り、2件目は後続チャンクで返る（早期ディスパッチ・順序維持）。
        var scanner = new StreamingToolCallScanner();

        var first = scanner.Feed("[{\"name\":\"write_file\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"A\"}},");
        Assert.Single(first);
        Assert.Equal(1, scanner.EmittedCount);

        var second = scanner.Feed("{\"name\":\"write_file\",\"arguments\":{\"path\":\"b.txt\",\"content\":\"B\"}}]");
        Assert.Single(second);
        Assert.Equal(2, scanner.EmittedCount);
    }

    [Fact]
    public void Splits_chunks_in_the_middle_of_a_string()
    {
        var t = Assert.Single(FeedAll(
            "[{\"name\":\"run_pow", "ershell\",\"arguments\":{\"command\":\"Get-", "ChildItem\"}}]"));
        using var args = JsonDocument.Parse(t.ArgumentsJson);
        Assert.Equal("Get-ChildItem", args.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Ignores_braces_and_brackets_inside_string_values()
    {
        // 文字列内の } ] { [ や エスケープした " は構造として数えない。
        var t = Assert.Single(FeedAll(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"echo } ] { [ \\\" hi\"}}]"));
        using var args = JsonDocument.Parse(t.ArgumentsJson);
        Assert.Equal("echo } ] { [ \" hi", args.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Skips_leading_whitespace_even_across_chunks()
    {
        var t = Assert.Single(FeedAll("  \n", "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}]"));
        Assert.Equal("run_powershell", t.Name);
    }

    [Fact]
    public void Stops_at_top_level_array_close_ignoring_trailing_text()
    {
        var t = Assert.Single(FeedAll("[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}]  done"));
        Assert.Equal("run_powershell", t.Name);
    }

    [Fact]
    public void Exposes_array_boundaries_with_leading_whitespace_and_trailing_text()
    {
        // 配列の前後（前置き空白・配列後の自然文）を確定本文として拾えるよう、開き '[' と閉じ ']' の次位置を公開する。
        const string s = "  [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}] done";
        var scanner = new StreamingToolCallScanner();
        scanner.Feed(s);

        Assert.Equal(2, scanner.JsonStartIndex);          // 先頭2文字は空白
        Assert.Equal(s.IndexOf("] ") + 1, scanner.JsonEndIndex);
        Assert.Equal(" done", s[scanner.JsonEndIndex..]);
    }

    [Fact]
    public void Leaves_json_end_unset_when_array_never_closes()
    {
        // 不正要素で打ち切った／配列が閉じていない場合は境界を確定させない（残骸を本文へ混ぜないため）。
        var scanner = new StreamingToolCallScanner();
        scanner.Feed("[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}},{\"foo\":\"bar\"}]");

        Assert.Equal(1, scanner.EmittedCount);
        Assert.Equal(-1, scanner.JsonEndIndex);
    }

    [Fact]
    public void Stops_after_first_unparseable_object_keeping_leading_valid_ones()
    {
        // 1件目は正常、2件目は name/command を欠く（解釈不能）→ そこで打ち切り、3件目は捨てる。
        var tools = FeedAll(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}," +
            "{\"foo\":\"bar\"}," +
            "{\"name\":\"write_file\",\"arguments\":{\"path\":\"c.txt\",\"content\":\"C\"}}]");
        var t = Assert.Single(tools);
        Assert.Equal("run_powershell", t.Name);
    }

    [Fact]
    public void Disables_when_not_starting_with_bracket()
    {
        // 単体 {…}・関数呼び出し風・通常テキストは配列モードにせず、何も返さない（終端パーサに委ねる）。
        var scanner = new StreamingToolCallScanner();
        Assert.Empty(scanner.Feed("{\"command\":\"ls\"}"));
        Assert.Equal(0, scanner.EmittedCount);

        var scanner2 = new StreamingToolCallScanner();
        Assert.Empty(scanner2.Feed("こんにちは、これは普通の文章です。"));
        Assert.Equal(0, scanner2.EmittedCount);
    }
}
