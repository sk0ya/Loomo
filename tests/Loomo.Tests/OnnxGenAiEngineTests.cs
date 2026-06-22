using System.Text;
using sk0ya.Loomo.Ai.Clients;

namespace sk0ya.Loomo.Tests;

public class OnnxGenAiEngineTests
{
    [Fact]
    public void Detects_repeated_long_answer_block()
    {
        const string block =
            "フォルダAT114の詳細情報を提供しました。フォルダの詳細は以下の通りです：\n\n" +
            "```\n\"d-----        2026/06/07     16:06                AT114\"\n```\n\n";

        var text = new StringBuilder(block + block + block);

        Assert.True(DecodeLoopGuards.IsRepeatingTextTail(text));
    }

    [Fact]
    public void Does_not_flag_normal_non_repeating_answer()
    {
        var text = new StringBuilder(
            "AT114フォルダが見つかりました。更新日時は2026/06/07 16:06です。" +
            "必要であれば、このフォルダ内のファイル一覧も確認できます。");

        Assert.False(DecodeLoopGuards.IsRepeatingTextTail(text));
    }

    [Fact]
    public void Requires_at_least_three_repeats()
    {
        const string block =
            "フォルダAT114の詳細情報を提供しました。フォルダの詳細は以下の通りです：\n\n" +
            "```\n\"d-----        2026/06/07     16:06                AT114\"\n```\n\n";

        var text = new StringBuilder(block + block);

        Assert.False(DecodeLoopGuards.IsRepeatingTextTail(text));
    }
}
