using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;
/// <summary>AiBarViewModel のチャット送信パート：エージェントループの駆動（SendAsync）、承認カードの受け口、
/// トランスクリプト／ツールカードの整形ヘルパ。表示状態・ウォームアップ・コマンドは他のパートにある。</summary>
public sealed partial class AiBarViewModel
{

    private bool CanSend() => !IsBusy && !IsWarmingUp && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        PushHistory(text);

        // スラッシュコマンドなら AI へ送らず即実行する。
        if (TryRunChatCommand(text))
        {
            SetInput("");
            CloseCommandPopup();
            return;
        }

        Input = "";
        CloseCommandPopup();
        IsExpanded = true;
        _cts = new CancellationTokenSource();

        // モデル未ロードならチャット実行前にウォームアップしておく。これをしないと最初のターンで
        // 数十秒のモデルロードが「考え中…」のまま無反応に見える（何が起きているか分からない）。
        // ウォームアップ中は IsWarmingUp 表示が出る（送信は CanSend が抑止）。
        // このウォームアップは送信に伴うもので、直後にこのターンが始まるため「完了」内訳表示は出さない。
        _suppressWarmupCompletion = true;
        try { await _warmup.EnsureWarmAsync(_cts.Token); }
        catch (OperationCanceledException) { /* 後段 RunTurnAsync 側でまとめて扱う */ }

        // 直前（起動時など）のウォームアップ完了表示が残っていれば、このターンの「考え中…」と重なるので畳む。
        IsWarmupCompletionVisible = false;
        WarmupCompletionTotalText = "";
        WarmupCompletionStages.Clear();
        IsBusy = true;
        SetStatus("考え中…");

        // トレース（§20）と保存が同じIDを共有するよう、ターン開始前にセッションIDを確定する。
        _currentSessionId ??= Guid.NewGuid().ToString("N");

        Add(EntryKind.User, "あなた", text);
        var turnClock = Stopwatch.StartNew();
        var activity = Add(EntryKind.Activity, "進行状況", "");
        var log = new ActivityLog(activity, () => turnClock.Elapsed);
        TranscriptEntry? assistant = null;
        TranscriptEntry? thinking = null;
        Stopwatch? assistantClock = null;
        Stopwatch? thinkingClock = null;
        var loggedThinking = false;
        var loggedResponse = false;
        var aiCallCount = 0;
        var rawStream = new StringBuilder();   // 現在のAI呼び出しの揮発性ライブ出力（進捗プレビュー専用）

        log.Append(ActivityKind.Config, TranscriptFormatting.FormatRunConfig(_settings));
        log.Append(ActivityKind.Send, "AIに送信しました。応答を待っています。");

        try
        {
            await foreach (var ev in _orchestrator.RunTurnAsync(_conversation, text, _currentSessionId, _cts.Token,
                               turnPreamble: AiSettings.ChatTurnPreamble))
            {
                switch (ev)
                {
                    case ThinkingDelta think:
                        if (!loggedThinking)
                        {
                            log.Append(ActivityKind.Think, "モデルが思考を生成しています。");
                            loggedThinking = true;
                        }
                        if (thinking is null)
                        {
                            thinking = Add(EntryKind.Thinking, "💭 思考", "");
                            thinkingClock = Stopwatch.StartNew();
                        }
                        thinking.AppendText(think.Text);
                        // 進捗状況に「いま何を考えているか」を逐次プレビュー表示する。
                        SetStatus($"💭 思考中… {TranscriptFormatting.StreamPreview(thinking.Text)}");
                        break;

                    case RawTextDelta raw:
                        // 揮発性のライブ出力：トランスクリプト・履歴には残さず、「進行状況」エントリの末尾に
                        // 「いま生成中の生テキスト」を逐次プレビューする（確定すると揮発タグごと取り除かれる）。
                        if (!loggedResponse)
                        {
                            log.Append(ActivityKind.Response, "回答本文の生成を開始しました。");
                            SetStatus("応答生成中…");
                            loggedResponse = true;
                        }
                        rawStream.Append(raw.Text);
                        // 末尾だけ流すのではなく、生成済みの全文を改行を保ったまま貯めてライブ段に見せる（確定で消える）。
                        var preview = rawStream.ToString().Trim();
                        log.SetLive(ActivityKind.LiveResponse, preview.Length == 0 ? "" : $"生成中:{Environment.NewLine}{preview}");
                        break;

                    case TextDelta delta:
                        if (!loggedResponse)
                        {
                            log.Append(ActivityKind.Response, "回答本文の生成を開始しました。");
                            loggedResponse = true;
                        }
                        FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
                        thinking = null; // 本文に入ったので思考ブロックを区切る
                        if (assistant is null)
                        {
                            assistant = Add(EntryKind.Assistant, "エージェント", "");
                            assistantClock = Stopwatch.StartNew();
                        }
                        assistant.AppendText(delta.Text);
                        SetStatus("応答生成中…");
                        break;

                    case ToolUseRequested req:
                        FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
                        thinking = null;
                        // ツールが確定したので、配列の生 JSON を見せていたライブ段は消す
                        // （以降はツールカードで表示する。複数ツールでも二重表示にならない）。
                        rawStream.Clear();
                        log.ClearLive();
                        // AIがツール呼び出しと一緒に生成した本文（説明・narration）は、独立した
                        // 「🤖 エージェント」エントリにはせず、ツールカードへ畳んで併記する。
                        // 本文 → 複数ツールの場合は最初のツールにのみ付け、以降はそのまま引数だけ出す。
                        var narration = assistant?.Text;
                        if (assistant is not null)
                        {
                            Transcript.Remove(assistant);   // ライブ表示していた本文エントリを取り下げ、カードへ畳む
                            assistant = null;               // テキストブロックを区切る
                            assistantClock = null;
                        }
                        // 進捗状況に「どのツールを何の引数で呼ぶか」を表示する。
                        SetStatus($"🔧 {req.ToolUse.Name} を準備中… {TranscriptFormatting.StreamPreview(req.ToolUse.ArgumentsJson)}");
                        log.Append(ActivityKind.ToolPrepare, $"{req.ToolUse.Name} の呼び出しを準備しています: {TranscriptFormatting.StreamPreview(req.ToolUse.ArgumentsJson)}");
                        Add(EntryKind.Tool, ToolUseHeader(req.ToolUse.Name, req.ToolUse.ArgumentsJson), ComposeToolCard(narration, req.ToolUse.ArgumentsJson, req.ToolUse.RawJson));
                        break;

                    case ApprovalRequested approval:
                        SetStatus($"⏳ {approval.ToolName} の承認待ち…");
                        log.Append(ActivityKind.Approval, $"{approval.ToolName} の実行承認を待っています。");
                        break;

                    case ToolExecutionStarted started:
                        // 進捗状況に「いま実行しているコマンド」を表示する。
                        SetStatus($"🔧 {started.ToolUse.Name} を実行中… {TranscriptFormatting.StreamPreview(started.ToolUse.ArgumentsJson)}");
                        log.Append(ActivityKind.ToolRun, $"{started.ToolUse.Name} を実行しています: {TranscriptFormatting.StreamPreview(started.ToolUse.ArgumentsJson)}");
                        break;

                    case ToolExecutionCompleted done:
                        // ツール結果を踏まえてAIが再応答する。直前ツールの結果概要も進捗状況に出す。
                        SetStatus($"考え中…（直前 {done.ToolUse.Name}: {(done.Result.IsError ? "エラー" : "完了")} {TranscriptFormatting.StreamPreview(done.Result.Content)}）");
                        log.Append(done.Result.IsError ? ActivityKind.ToolError : ActivityKind.ToolDone,
                            $"{done.ToolUse.Name} が完了しました（{(done.Result.IsError ? "エラー" : "成功")}）: {TranscriptFormatting.StreamPreview(done.Result.Content)}。結果を踏まえて次の応答を待っています。");
                        Add(EntryKind.Tool, $"↳ 結果 ({done.ToolUse.Name})", Truncate(done.Result.Content));
                        break;

                    case ToolCallParseFailed parseFailed:
                        // 何が出力されたか分かるよう生出力を見せ、AI に再試行させる旨を示す。
                        FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
                        thinking = null;
                        if (assistant is not null)
                        {
                            Transcript.Remove(assistant);   // ライブ表示していた本文を取り下げてカードへ畳む
                            assistant = null;
                            assistantClock = null;
                        }
                        SetStatus("⚠️ ツール呼び出しJSONが不正。AIに再試行させています…");
                        log.Append(ActivityKind.Warn, "ツール呼び出しのJSONが不正でした。モデルの生出力を表示し、正しいJSONで出し直させます。");
                        Add(EntryKind.Error, "⚠️ 不正なツール出力（再試行）", parseFailed.RawText);
                        break;

                    case AgentError err:
                        log.Append(ActivityKind.Error, $"エラーで停止しました。合計 {FormatDuration(turnClock.Elapsed)} かかりました。");
                        Add(EntryKind.Error, "⚠️ エラー", err.Message);
                        break;

                    case AiUsageReported usage:
                        aiCallCount++;
                        log.Append(ActivityKind.Usage, TranscriptFormatting.FormatUsage(usage, aiCallCount));
                        rawStream.Clear();   // このAI呼び出しは終了。次の呼び出しの揮発プレビューを新規に始める
                        break;

                    case TurnCompleted:
                        log.Append(ActivityKind.Complete, $"回答が完了しました。合計 {FormatDuration(turnClock.Elapsed)} かかりました。");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            log.Append(ActivityKind.Cancel, $"ユーザー操作で中断しました。合計 {FormatDuration(turnClock.Elapsed)} かかりました。");
            Add(EntryKind.Error, "⏹ 中断", "ユーザーにより中断されました。");
        }
        catch (Exception ex)
        {
            log.Append(ActivityKind.Error, $"例外で停止しました。合計 {FormatDuration(turnClock.Elapsed)} かかりました。");
            Add(EntryKind.Error, "⚠️ 例外", ex.Message);
        }
        finally
        {
            // モデルがロード済みでウォームアップが走らなかった場合などに抑止フラグが次の自発的
            // ウォームアップへ漏れないよう、ターン終了時に必ず戻す（終了遷移で既に消費されていれば no-op）。
            _suppressWarmupCompletion = false;
            log.ClearLive();   // ライブ段が残ったまま ProgressLog に保存されないよう必ず片付ける
            turnClock.Stop();
            activity.Header = $"進行状況 ({FormatDuration(turnClock.Elapsed)})";
            FinishTimedEntry(ref assistant, ref assistantClock, "エージェント");
            FinishTimedEntry(ref thinking, ref thinkingClock, "💭 思考");
            IsBusy = false;
            ClearStatus();
            _cts?.Dispose();
            _cts = null;

            // このターンの進行状況ログを、ターンを開始した user メッセージへ畳んで永続化する
            // （セッション復元時に進捗表示を再構築できるようにする）。
            var turnUser = _conversation.Messages.LastOrDefault(m => m.Role == ChatRole.User);
            if (turnUser is not null) turnUser.ProgressLog = activity.Text;

            // ターン終了時にセッションを自動保存（新規なら採番）
            try { _currentSessionId = _sessions.Save(_currentSessionId, _conversation); }
            catch { /* 保存失敗は会話を妨げない */ }
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void OnApprovalRequested(ApprovalContext ctx)
    {
        // チャットのターン実行中だけ処理する。ワークフロー実行中の承認は WorkflowViewModel が
        // 自分のステップログへ橋渡しするため、ここでは拾わない（同じ singleton イベントの二重処理を防ぐ）。
        if (!IsBusy) return;

        var entry = Add(EntryKind.Approval, $"承認が必要: {ctx.ToolName}", ctx.Summary);
        if (ContainsDiff(ctx.Summary))
            entry.SetDiff(ctx.Summary);   // サマリを色付き差分へ展開
        entry.BindApproval(ctx.Completion);
    }

    /// <summary>承認サマリが統合差分（+/- 接頭辞付きの行）を含むか。編集系ツールの「ヘッダ＋差分」要約は
    /// 含み、引数不正などのエラー要約は含まないので、ツール名のハードコードより堅牢に差分カードを出し分けられる。</summary>
    private static bool ContainsDiff(string summary)
    {
        foreach (var line in summary.AsSpan().EnumerateLines())
            if (line.Length > 0 && (line[0] == '+' || line[0] == '-'))
                return true;
        return false;
    }

    private TranscriptEntry Add(EntryKind kind, string header, string text)
    {
        // ツール使用・ツール結果は既定で折りたたむ（ヘッダーの1行要約だけ常時見え、詳細は開いたときだけ）。
        var entry = new TranscriptEntry { Kind = kind, Header = header, Text = text, IsCollapsed = kind == EntryKind.Tool };
        Transcript.Add(entry);
        return entry;
    }

    private static string Truncate(string s, int max = 2000)
        => s.Length <= max ? s : s[..max] + $"\n…(+{s.Length - max} 文字)";

    /// <summary>ツール使用エントリのヘッダー（折りたたんでも常時見える1行）を組み立てる。
    /// 「🔧 ツール名: 主要引数」の形で、何をしたかが一目で分かるようにする
    /// （run_powershell=コマンド／write_file・edit_file=パス／web_search=検索語）。</summary>
    private static string ToolUseHeader(string toolName, string argumentsJson)
    {
        var summary = SummarizeToolArgs(toolName, argumentsJson);
        return string.IsNullOrEmpty(summary) ? $"🔧 {toolName}" : $"🔧 {toolName}: {summary}";
    }

    /// <summary>ツールの引数JSONから代表引数を1つ抜き出し、1行要約に整える。小モデルが別名キーで
    /// 送ってきても拾えるよう各 *Contract の別名配列を順に見る。見つからなければ最初の文字列引数で代用。</summary>
    private static string SummarizeToolArgs(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";

            var keys = toolName switch
            {
                PwshContract.ToolName => PwshContract.CommandKeys,
                WriteFileContract.ToolName => WriteFileContract.PathKeys,
                EditFileContract.ToolName => EditFileContract.PathKeys,
                WebSearchContract.ToolName => WebSearchContract.QueryKeys,
                _ => null
            };

            if (keys is not null)
                foreach (var k in keys)
                    if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                        return OneLine(v.GetString() ?? "");

            // 未知のツール・代表引数が無い場合は最初の文字列引数で代用する。
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    return OneLine(p.Value.GetString() ?? "");
        }
        catch { /* パース不能なら要約なし（ヘッダーはツール名のみになる） */ }
        return "";
    }

    /// <summary>改行・連続空白を畳んで1行にし、長ければ先頭から切って末尾に省略記号を付ける（ヘッダー用）。</summary>
    private static string OneLine(string text, int max = 80)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>ツールカードの本文を組み立てる。AIがツール呼び出しと一緒に生成した本文（説明・narration）が
    /// あれば引数JSONの上に併記し、無ければ引数JSONのみを示す。</summary>
    private static string ComposeToolCard(string? narration, string argumentsJson, string? rawJson)
    {
        var text = narration?.Trim();
        var raw = rawJson?.Trim();
        var body = string.IsNullOrEmpty(text)
            ? "arguments:" + Environment.NewLine + argumentsJson
            : text + Environment.NewLine + "arguments:" + Environment.NewLine + argumentsJson;

        if (!string.IsNullOrEmpty(raw) && raw != argumentsJson)
            body += Environment.NewLine + "raw:" + Environment.NewLine + raw;

        return body;
    }

    private static void FinishTimedEntry(ref TranscriptEntry? entry, ref Stopwatch? clock, string baseHeader)
    {
        if (entry is null || clock is null) return;
        clock.Stop();
        entry.Header = $"{baseHeader} ({FormatDuration(clock.Elapsed)})";
        clock = null;
    }
}

