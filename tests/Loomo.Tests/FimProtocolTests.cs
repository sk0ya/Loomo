using sk0ya.Loomo.Core.Completion;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 本体とワーカーのあいだの約束。<b>相手の不調でこちらが落ちない</b>ことを中心に固定する
/// ——ワーカーは別プロセスなので、壊れた行や中断された行はいつでも届きうる。
/// </summary>
public sealed class FimProtocolTests
{
    [Fact]
    public void A_request_survives_the_round_trip()
    {
        var sent = new FimProtocol.Request(42, "<|fim_prefix|>var x = <|fim_suffix|>;<|fim_middle|>", 8);

        var received = FimProtocol.ReadRequest(FimProtocol.Serialize(sent));

        Assert.Equal(sent, received);
    }

    [Fact]
    public void A_response_survives_the_round_trip()
    {
        var sent = new FimProtocol.Response(7, "1;", 297, 37, 177, 3, 58);

        var received = FimProtocol.ReadResponse(FimProtocol.Serialize(sent));

        Assert.Equal(sent, received);
    }

    /// <summary>「出せなかった」も応答。本体はこれを受けて静かに何も出さない。</summary>
    [Fact]
    public void An_empty_answer_is_a_valid_response()
    {
        var received = FimProtocol.ReadResponse(FimProtocol.Serialize(new FimProtocol.Response(3)));

        Assert.NotNull(received);
        Assert.Null(received!.Value.Text);
        Assert.Equal(3, received.Value.Id);
    }

    [Fact]
    public void An_error_is_carried_back_rather_than_thrown()
    {
        var received = FimProtocol.ReadResponse(
            FimProtocol.Serialize(new FimProtocol.Response(9, Error: "モデルが無い")));

        Assert.Equal("モデルが無い", received!.Value.Error);
        Assert.Null(received.Value.Text);
    }

    /// <summary>1 行 1 メッセージなので、本文の改行は必ずエスケープされていなければならない
    /// ——生の改行が混ざると、そこから先が全部ずれる。</summary>
    [Fact]
    public void A_multi_line_prompt_stays_on_one_line()
    {
        var line = FimProtocol.Serialize(new FimProtocol.Request(1, "a\nb\r\nc", 8));

        Assert.DoesNotContain('\n', line);
        Assert.Equal("a\nb\r\nc", FimProtocol.ReadRequest(line)!.Value.Prompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("これは JSON ではない")]
    [InlineData("{\"id\":")]                       // 途中で切れた行
    [InlineData("{\"id\":0,\"prompt\":\"x\"}")]    // 通し番号が無い
    [InlineData("{\"id\":1}")]                     // プロンプトが無い
    public void A_broken_line_is_ignored_rather_than_fatal(string? line)
        => Assert.Null(FimProtocol.ReadRequest(line));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<<not json>>")]
    [InlineData("{\"id\":0}")]
    public void A_broken_response_line_is_ignored_rather_than_fatal(string? line)
        => Assert.Null(FimProtocol.ReadResponse(line));

    /// <summary>ワーカーが stderr へ書く覚え書きが stdout に紛れても、応答として誤読しない。</summary>
    [Fact]
    public void A_log_line_is_not_mistaken_for_a_response()
        => Assert.Null(FimProtocol.ReadResponse("[fim] ready pid=1234 model=Qwen2.5-Coder-0.5B-Q8_0.gguf"));
}
