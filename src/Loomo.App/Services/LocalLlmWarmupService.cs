using System;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 起動時にローカル推論エンジン（ONNX Runtime GenAI）へモデルを先読みさせ、最初のAIターンの待ち時間を減らす。
/// 最大のコールド要因はモデル重みのロード（CPU 実行で数十秒）で、これはワークスペースに依存しないため
/// 起動直後に前倒しする。<see cref="Phi4Engine"/> は常駐するので、コールドは初回 1 回だけになる。
/// </summary>
public sealed class LocalLlmWarmupService : IDisposable
{
    private readonly AiSettings _settings;
    private readonly Phi4Engine _engine;
    private readonly CancellationTokenSource _startupCts = new();

    public LocalLlmWarmupService(AiSettings settings, Phi4Engine engine)
    {
        _settings = settings;
        _engine = engine;

        // モデル重みのロードを起動直後に前倒しする（失敗しても通常のAI呼び出しで改めて確認する）。
        _ = WarmModelAtStartupAsync(_startupCts.Token);
    }

    private async Task WarmModelAtStartupAsync(CancellationToken ct)
    {
        try
        {
            var path = _settings.Local.ModelPath;
            if (!string.IsNullOrWhiteSpace(path))
                // ロードだけでなくダミー生成まで走らせて重みをページインさせる（初回プロンプトの十数秒の
                // ページインを起動時バックグラウンドへ前倒しする）。
                await _engine.WarmUpAsync(path, ct);
        }
        catch (OperationCanceledException) { }
        catch { /* ウォームアップは体感改善用。失敗は通常のAI呼び出し時に改めて顕在化する。 */ }
    }

    public void Dispose()
    {
        _startupCts.Cancel();
        _startupCts.Dispose();
    }
}
