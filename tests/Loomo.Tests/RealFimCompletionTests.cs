using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using sk0ya.Loomo.Ai.Completion;
using sk0ya.Loomo.Core.Settings;
using Xunit.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 実際の FIM モデルを読み込んで先読みが返ることの確認。モデル（約506MB）を置いた環境でだけ走る
/// ——普段のテストで 0.5B を読み込むわけにはいかないので、モデルが無ければ黙って通す。
/// </summary>
public sealed class RealFimCompletionTests(ITestOutputHelper output)
{
    private static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "models", "completion", "qwen25-coder-0.5b-q8_0", "Qwen2.5-Coder-0.5B-Q8_0.gguf");

    private static InlineCompletionSettings Settings() => new()
    {
        Enabled = true,
        ModelPath = ModelPath,
        PrefixLines = 30,
        SuffixLines = 3,
        MaxTokens = 16,
    };

    /// <summary>手掛かりの途中で切った 1 行を、モデルに書き継がせる。</summary>
    private static (string[] Lines, int Line, int Column, string Truth) Cut(string[] source, int line)
    {
        var text = source[line];
        int column = text.Length / 2;
        var lines = (string[])source.Clone();
        lines[line] = text[..column];
        return (lines, line, column, text[column..]);
    }

    [Fact]
    public async Task The_model_continues_a_line_and_reuses_its_cache_between_keystrokes()
    {
        if (!File.Exists(ModelPath))
        {
            output.WriteLine($"モデルが無いので飛ばす: {ModelPath}");
            return;
        }

        var source = await File.ReadAllLinesAsync(
            Path.Combine(FindRepoRoot(), "src", "Loomo.Services", "Lsp", "LspDocumentTable.cs"));

        using var engine = new FimCompletionEngine();
        var settings = Settings();

        // 1 回目はモデルのロードと全文の prefill を含む（実測でおよそ 1.5 秒）。
        var first = Cut(source, 146);
        var cold = Stopwatch.StartNew();
        var coldResult = await engine.CompleteAsync(settings, first.Lines, first.Line, first.Column, default);
        cold.Stop();
        output.WriteLine($"1回目 {cold.ElapsedMilliseconds} ms → {coldResult ?? "(なし)"}");
        Assert.True(engine.IsLoaded, "モデルが読み込まれていない");

        // 2 回目以降は「1 文字打った」状態。共通の接頭辞が KV に残っているので差分だけで済む。
        var typed = (string[])first.Lines.Clone();
        typed[first.Line] += first.Truth[..1];

        var warm = Stopwatch.StartNew();
        var warmResult = await engine.CompleteAsync(
            settings, typed, first.Line, typed[first.Line].Length, default);
        warm.Stop();

        var timing = engine.LastTiming;
        output.WriteLine($"2回目 {warm.ElapsedMilliseconds} ms → {warmResult ?? "(なし)"}");
        output.WriteLine($"  再利用 {timing.ReusedTokens} tok / 送り直し {timing.PrefillTokens} tok "
            + $"(prefill {timing.PrefillMs} ms) / 生成 {timing.GeneratedTokens} tok ({timing.GenerateMs} ms)");

        Assert.True(timing.ReusedTokens > 0,
            "KV キャッシュが再利用されていない（打鍵ごとに全文を prefill し直すと 1 秒を超え、先読みとして成立しない）");
        Assert.True(timing.PrefillTokens < timing.ReusedTokens,
            $"送り直しが再利用より多い（再利用 {timing.ReusedTokens} / 送り直し {timing.PrefillTokens}）");
    }

    /// <summary>設定が無効、モデルのパスが無い——どちらも「黙って何も出さない」。
    /// 先読みの失敗で入力が止まることがあってはならない。</summary>
    [Fact]
    public async Task A_missing_model_or_a_disabled_setting_simply_yields_nothing()
    {
        using var engine = new FimCompletionEngine();
        string[] lines = ["public void Run()", "{", "    var x = 1;", "}"];

        Assert.Null(await engine.CompleteAsync(
            new InlineCompletionSettings { Enabled = false, ModelPath = ModelPath }, lines, 2, 14, default));

        Assert.Null(await engine.CompleteAsync(
            new InlineCompletionSettings { Enabled = true, ModelPath = @"Z:\no\such\model.gguf" }, lines, 2, 14, default));

        Assert.Null(await engine.CompleteAsync(
            new InlineCompletionSettings { Enabled = true, ModelPath = null }, lines, 2, 14, default));

        Assert.False(engine.IsLoaded, "使えない設定でモデルを読み込んではいけない");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "sk0ya.Loomo.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("リポジトリのルートが見つかりません。");
    }
}
