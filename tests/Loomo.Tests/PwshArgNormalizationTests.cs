using System.Text.Json;
using sk0ya.Loomo.Core.Tools.Implementations;

namespace sk0ya.Loomo.Tests;

/// <summary>pwsh の引数正規化：小モデルのキー揺れ（cmd/script 等）と空引数の扱い。
/// NormalizeArguments は _terminal を使わない純粋変換なので null で構築してよい。</summary>
public class PwshArgNormalizationTests
{
    private static readonly PwshTool Tool = new(terminal: null!);

    private static string CommandOf(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var normalized = Tool.NormalizeArguments(doc.RootElement);
        return normalized.GetProperty("command").GetString() ?? "";
    }

    [Theory]
    [InlineData("{\"command\":\"Get-ChildItem\"}")]
    [InlineData("{\"cmd\":\"Get-ChildItem\"}")]
    [InlineData("{\"script\":\"Get-ChildItem\"}")]
    [InlineData("{\"code\":\"Get-ChildItem\"}")]
    [InlineData("{\"powershell\":\"Get-ChildItem\"}")]
    public void Aliases_are_normalized_to_command(string argsJson)
        => Assert.Equal("Get-ChildItem", CommandOf(argsJson));

    [Fact]
    public void Canonical_command_wins_over_alias()
        => Assert.Equal("real", CommandOf("{\"command\":\"real\",\"cmd\":\"ignored\"}"));

    [Fact]
    public void Single_unknown_string_property_is_adopted_as_command()
        => Assert.Equal("Get-Location", CommandOf("{\"line\":\"Get-Location\"}"));

    [Fact]
    public void Lone_arbitrary_string_property_is_adopted()
        => Assert.Equal("whoami", CommandOf("{\"foo\":\"whoami\"}"));

    [Fact]
    public void Ambiguous_multiple_strings_without_known_key_yield_empty()
        => Assert.Equal("", CommandOf("{\"foo\":\"a\",\"bar\":\"b\"}"));

    [Fact]
    public void Empty_object_yields_empty_command()
        => Assert.Equal("", CommandOf("{}"));
}
