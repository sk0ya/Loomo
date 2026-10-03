using System.IO;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Core.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>デバッグ停止中の行末の値（Inline Values）：関数範囲の推定と、行内の識別子との突き合わせ。</summary>
public class DebugInlineValuesTests
{
    private static string[] Lines(string source) => source.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void CSharp_method_start_is_found_past_inner_control_blocks()
    {
        var lines = Lines("""
            class C
            {
                int Field = 1;
                public int Foo(int a, string name)
                {
                    var x = a + 1;
                    if (x > 0)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            x += i;
                        }
                    }
                    return x;
                }
            }
            """);
        // 停止行 = "x += i;"（10 行目）。if/for を越えてメソッドの宣言行（3 行目）まで遡る。
        Assert.Equal(3, DebugInlineValues.FindFunctionStart(lines, 10));
        // 停止行 = "return x;"：閉じた for/if のブロックは数え合わせて読み飛ばす。
        Assert.Equal(3, DebugInlineValues.FindFunctionStart(lines, 13));
    }

    [Fact]
    public void Stopping_on_the_opening_brace_after_step_in_uses_that_function()
    {
        var lines = Lines("""
            class C
            {
                void Bar(int a)
                {
                    Use(a);
                }
            }
            """);
        Assert.Equal(2, DebugInlineValues.FindFunctionStart(lines, 3));
    }

    [Fact]
    public void Lambda_and_local_function_bodies_are_their_own_frames()
    {
        var lines = Lines("""
            void Outer()
            {
                var total = 0;
                items.ForEach(item =>
                {
                    total += item;
                });
            }
            """);
        Assert.Equal(3, DebugInlineValues.FindFunctionStart(lines, 5));
    }

    [Fact]
    public void Object_initializers_and_try_catch_do_not_end_the_search()
    {
        var lines = Lines("""
            void Run(int n)
            {
                try
                {
                    var o = new Options
                    {
                        Count = n,
                    };
                }
                catch (Exception ex)
                {
                    Log(ex, n);
                }
            }
            """);
        Assert.Equal(0, DebugInlineValues.FindFunctionStart(lines, 6));   // 初期化子の中
        Assert.Equal(0, DebugInlineValues.FindFunctionStart(lines, 11));  // catch の中
    }

    [Fact]
    public void Braces_inside_comments_and_strings_are_not_counted()
    {
        var lines = Lines("""
            void Run(int n)
            {
                var s = "{ not a block";
                // } nor this
                Use(s, n);
            }
            """);
        Assert.Equal(0, DebugInlineValues.FindFunctionStart(lines, 4));
    }

    [Fact]
    public void TypeScript_functions_and_arrow_functions_are_recognized()
    {
        var lines = Lines("""
            export function sum(xs: number[]): number {
              let total = 0;
              for (const x of xs) {
                total += x;
              }
              return total;
            }
            const handler = async (req) => {
              const body = req.body;
              if (body) {
                send(body);
              }
            };
            """);
        Assert.Equal(0, DebugInlineValues.FindFunctionStart(lines, 3));
        Assert.Equal(7, DebugInlineValues.FindFunctionStart(lines, 10));
    }

    [Fact]
    public void Build_matches_locals_per_line_in_order_without_member_access_or_duplicates()
    {
        var lines = Lines("""
            void Foo(int a, string name)
            {
                var x = Compute(a);   // name はコメントなので拾わない
                x = x + a + other.x;
                Print("name", name);
            }
            """);
        var values = new Dictionary<string, string> { ["a"] = "1", ["x"] = "3", ["name"] = "\"abc\"" };

        var result = DebugInlineValues.Build(lines, 0, 4, values);

        Assert.Equal(new[]
        {
            new DebugInlineValueLine(0, "a = 1, name = \"abc\""),
            new DebugInlineValueLine(2, "x = 3, a = 1"),
            new DebugInlineValueLine(3, "x = 3, a = 1"),
            new DebugInlineValueLine(4, "name = \"abc\""),
        }, result);
    }

    [Fact]
    public void Lines_below_the_stop_line_get_no_values()
    {
        var lines = Lines("""
            void Foo(int a)
            {
                var x = a;
                var y = x;
            }
            """);
        var result = DebugInlineValues.Compute("f.cs", lines, 2,
            new Dictionary<string, string> { ["a"] = "1", ["x"] = "1", ["y"] = "0" });

        Assert.Equal(new[] { 0, 2 }, result.Lines.Select(l => l.Line0));
    }

    [Fact]
    public void Long_and_multiline_values_are_flattened_and_truncated()
    {
        Assert.Equal("{ A = 1, B = 2 }", DebugInlineValues.FormatValue("{\n  A = 1,\n  B = 2\n}"));
        var longValue = new string('z', 200);
        var formatted = DebugInlineValues.FormatValue(longValue);
        Assert.Equal(DebugInlineValues.MaxValueChars, formatted.Length);
        Assert.EndsWith("…", formatted);
    }

    [Fact]
    public void Value_map_keeps_the_innermost_scope_and_skips_non_identifiers()
    {
        var map = DebugInlineValues.ToValueMap(new[]
        {
            new DebugVariable("x", "1", "int", 0),
            new DebugVariable("this", "{C}", "C", 5),
            new DebugVariable("[0]", "9", "int", 0),
            new DebugVariable("Static members", "", null, 7),
            new DebugVariable("@event", "\"e\"", "string", 0),
            new DebugVariable("x", "2", "int", 0),   // 外側スコープの同名は隠れる
        });

        Assert.Equal(2, map.Count);
        Assert.Equal("1", map["x"]);
        Assert.Equal("\"e\"", map["event"]);
    }

    [Fact]
    public void Heavy_and_global_scopes_are_not_read()
    {
        Assert.True(DebugInlineValues.ShouldReadScope(new DebugScope("Locals", 1, false)));
        Assert.True(DebugInlineValues.ShouldReadScope(new DebugScope("Closure", 2, false)));
        Assert.False(DebugInlineValues.ShouldReadScope(new DebugScope("Global", 3, false)));
        Assert.False(DebugInlineValues.ShouldReadScope(new DebugScope("Locals", 4, true)));
    }

    [Fact]
    public void Verbatim_identifiers_in_source_match_their_plain_name()
    {
        var lines = Lines("var @event = Make();");
        var result = DebugInlineValues.Build(lines, 0, 0, new Dictionary<string, string> { ["event"] = "\"e\"" });
        Assert.Equal("event = \"e\"", Assert.Single(result).Text);
    }

    [Fact]
    public void LinesFor_returns_lines_only_for_the_same_file_regardless_of_case_and_relative_parts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loomo-iv");
        var path = Path.Combine(dir, "Program.cs");
        var set = new DebugInlineValueSet(path, new[] { new DebugInlineValueLine(1, "x = 1") });

        Assert.Single(set.LinesFor(Path.Combine(dir, "sub", "..", "PROGRAM.cs")));
        Assert.Empty(set.LinesFor(Path.Combine(dir, "Other.cs")));
        Assert.Empty(set.LinesFor(null));
        Assert.Empty(DebugInlineValueSet.Empty.LinesFor(path));
    }
}
