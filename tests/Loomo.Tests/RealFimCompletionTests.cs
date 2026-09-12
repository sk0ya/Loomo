using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai.Completion;
using sk0ya.Loomo.Core.Settings;
using Xunit.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 先読みを別プロセスのワーカーへ投げて、実際に答えが返るところまで。モデル（約506MB）を
/// 置いた環境でだけ走る——普段のテストで 0.5B を読み込むわけにはいかないので、
/// モデルが無ければ黙って通す。
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
        MaxTokens = 8,
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
    public async Task The_worker_continues_a_line_and_reuses_its_cache_between_keystrokes()
    {
        if (!File.Exists(ModelPath))
        {
            output.WriteLine($"モデルが無いので飛ばす: {ModelPath}");
            return;
        }

        var source = await File.ReadAllLinesAsync(
            Path.Combine(FindRepoRoot(), "src", "Loomo.Services", "Lsp", "LspDocumentTable.cs"));

        using var client = new FimCompletionClient();
        var settings = Settings();

        // 1 回目はワーカーの起動・モデルの読み込み・全文の prefill を含む。
        var first = Cut(source, 146);
        var cold = Stopwatch.StartNew();
        var coldResult = await client.CompleteAsync(settings, first.Lines, first.Line, first.Column, default);
        cold.Stop();
        output.WriteLine($"1回目 {cold.ElapsedMilliseconds} ms → {coldResult ?? "(なし)"}");
        Assert.True(client.IsRunning, "ワーカーが起動していない");

        // 2 回目以降は「1 文字打った」状態。共通の接頭辞が KV に残っているので差分だけで済む。
        var typed = (string[])first.Lines.Clone();
        typed[first.Line] += first.Truth[..1];

        var warm = Stopwatch.StartNew();
        var warmResult = await client.CompleteAsync(
            settings, typed, first.Line, typed[first.Line].Length, default);
        warm.Stop();

        var last = client.LastResponse;
        output.WriteLine($"2回目 {warm.ElapsedMilliseconds} ms → {warmResult ?? "(なし)"}");
        output.WriteLine($"  再利用 {last.ReusedTokens} tok / 送り直し {last.PrefillTokens} tok "
            + $"(prefill {last.PrefillMs} ms) / 生成 {last.GeneratedTokens} tok ({last.GenerateMs} ms)");

        Assert.True(last.ReusedTokens > 0,
            "KV キャッシュが再利用されていない（打鍵ごとに全文を prefill し直すと先読みとして成立しない）");
        Assert.True(last.PrefillTokens < last.ReusedTokens,
            $"送り直しが再利用より多い（再利用 {last.ReusedTokens} / 送り直し {last.PrefillTokens}）");
    }

    /// <summary>
    /// 別プロセスにした眼目。ワーカーは<b>本体より低い優先度</b>で動いていなければならない
    /// ——人の入力より先読みが優先されることが構造的に起きないようにするための一点。
    /// </summary>
    [Fact]
    public async Task The_worker_runs_below_normal_priority()
    {
        if (!File.Exists(ModelPath))
        {
            output.WriteLine($"モデルが無いので飛ばす: {ModelPath}");
            return;
        }

        using var client = new FimCompletionClient();
        string[] lines = ["public void Run()", "{", "    var value = 1;", "    var v", "}"];
        await client.CompleteAsync(Settings(), lines, 3, lines[3].Length, default);
        Assert.True(client.IsRunning, "ワーカーが起動していない");

        var worker = Process.GetProcessesByName("sk0ya.Loomo.Completion.Host");
        Assert.NotEmpty(worker);
        foreach (var process in worker)
        {
            output.WriteLine($"pid={process.Id} priority={process.PriorityClass}");
            Assert.Equal(ProcessPriorityClass.BelowNormal, process.PriorityClass);
            process.Dispose();
        }
    }

    /// <summary>設定が無効、モデルのパスが無い——どちらも「黙って何も出さない」。
    /// ワーカーを起動すらしない（無駄なプロセスもメモリも作らない）。</summary>
    [Fact]
    public async Task A_missing_model_or_a_disabled_setting_never_starts_the_worker()
    {
        using var client = new FimCompletionClient();
        string[] lines = ["public void Run()", "{", "    var x = 1;", "}"];

        Assert.Null(await client.CompleteAsync(
            new InlineCompletionSettings { Enabled = false, ModelPath = ModelPath }, lines, 2, 14, default));

        Assert.Null(await client.CompleteAsync(
            new InlineCompletionSettings { Enabled = true, ModelPath = @"Z:\no\such\model.gguf" }, lines, 2, 14, default));

        Assert.Null(await client.CompleteAsync(
            new InlineCompletionSettings { Enabled = true, ModelPath = null }, lines, 2, 14, default));

        Assert.False(client.IsRunning, "使えない設定でワーカーを起動してはいけない");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "sk0ya.Loomo.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("リポジトリのルートが見つかりません。");
    }
}
