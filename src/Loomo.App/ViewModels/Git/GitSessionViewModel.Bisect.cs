using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// bisect を Git ペインで（§24.100・git 2.56 の <c>bisect --reset-when-found</c>）。コミット一覧の右クリックで
/// 「ここは良かった・いまは悪い」として始め、ペインの上の帯で 1 歩ずつ判定する。判定はボタンのほかに、
/// ターミナルで最後に走らせたコマンドの終了コードをそのまま使える（ターミナル → Git の素材の流れ）。
/// 犯人が見つかったら始める前の場所へ戻り、そのコミットを一覧で選ぶ。
/// </summary>
public sealed partial class GitSessionViewModel
{
    [ObservableProperty] private bool _bisectInProgress;
    [ObservableProperty] private string _bisectLabel = "";

    /// <summary>ターミナルで最後に終わったコマンド（コマンド行・終了コード）。ShellWindow が結ぶ。無ければ null。</summary>
    public Func<(string Command, int? ExitCode)?>? LastTerminalCommand { get; set; }

    /// <summary>最後に読めた 1 歩（残りの数・試している版）。再起動後や外で始めた bisect では null で、帯は「bisect 中」とだけ言う。</summary>
    private GitBisectStep? _bisectStep;

    private async Task RefreshBisectStateAsync()
    {
        BisectInProgress = _status.IsRepository && await _git.IsBisectingAsync();
        if (!BisectInProgress) _bisectStep = null;
        BisectLabel = BisectPresentation.Label(BisectInProgress, _bisectStep);
    }

    /// <summary>右クリック「bisect：ここは良かった（いまは悪い）として始める」。</summary>
    public async Task StartBisectFromGoodAsync(string goodHash)
    {
        if (IsBusy || BisectInProgress) return;
        var (result, step) = await RunBisectAsync("bisect の開始", () => _git.BisectStartAsync("HEAD", goodHash));
        if (result is { Success: true })
            await ApplyBisectStepAsync(step);
    }

    [RelayCommand] private Task BisectGoodAsync() => MarkBisectAsync(GitBisectVerdict.Good);
    [RelayCommand] private Task BisectBadAsync() => MarkBisectAsync(GitBisectVerdict.Bad);
    [RelayCommand] private Task BisectSkipAsync() => MarkBisectAsync(GitBisectVerdict.Skip);

    /// <summary>ターミナルで最後に走らせたコマンドの成否で判定する（0＝良い・125＝飛ばす・それ以外＝悪い）。</summary>
    [RelayCommand]
    private Task BisectByTerminalAsync()
    {
        var last = LastTerminalCommand?.Invoke();
        if (GitBisectParser.VerdictFromExitCode(last?.ExitCode) is not { } verdict)
        {
            SetError("ターミナルで終わったコマンドがまだありません（終了コードが分かるのは、シェル統合の効いたターミナルで走らせたものです）。");
            return Task.CompletedTask;
        }
        return MarkBisectAsync(verdict, last!.Value.Command);
    }

    [RelayCommand]
    private async Task BisectResetAsync()
    {
        if (IsBusy) return;
        _bisectStep = null;
        await Commands.RunExternalAsync("bisect の終了", _git.BisectResetAsync);
        await RefreshBisectStateAsync();
    }

    private async Task MarkBisectAsync(GitBisectVerdict verdict, string? byCommand = null)
    {
        if (IsBusy || !BisectInProgress) return;
        var label = BisectPresentation.VerdictLabel(verdict) + (byCommand is null ? "" : $"（{byCommand}）");
        var (result, step) = await RunBisectAsync($"bisect：{label}", () => _git.BisectMarkAsync(verdict));
        if (result is { Success: true })
            await ApplyBisectStepAsync(step);
    }

    private async Task<(GitCommandResult? Result, GitBisectStep Step)> RunBisectAsync(
        string label, Func<Task<(GitCommandResult Result, GitBisectStep Step)>> run)
    {
        var step = new GitBisectStep();
        var result = await Commands.RunExternalAsync(label, async () =>
        {
            var (r, s) = await run();
            step = s;
            return r;
        });
        return (result, step);
    }

    /// <summary>1 歩の結果を帯へ。犯人が見つかったら（または飛ばしたものだけが残ったら）、始める前の場所へ戻って知らせる。</summary>
    private async Task ApplyBisectStepAsync(GitBisectStep step)
    {
        if (!step.IsFinished)
        {
            _bisectStep = step;
            await RefreshBisectStateAsync();
            return;
        }
        _bisectStep = null;
        var subject = step.Found is { } found ? (await _git.RunAsync("log", "-1", "--format=%s", found)).Output.Trim() : null;
        await Commands.RunExternalAsync("bisect の終了（元の場所へ戻る）", _git.BisectResetAsync);
        await RefreshBisectStateAsync();
        if (step.Found is { } culprit)
        {
            NotificationService.Post(BisectPresentation.FoundMessage(culprit, subject), NotificationKind.Success, "Git",
                new NotificationAction("コミットを見る", () => _ = SelectCommitAsync(culprit)));
            await SelectCommitAsync(culprit);
        }
        else
        {
            NotificationService.Post(BisectPresentation.AmbiguousMessage(step.Candidates), NotificationKind.Warning, "Git");
        }
    }
}

/// <summary>bisect の帯と知らせの文面（§24.100）。純関数。</summary>
internal static class BisectPresentation
{
    public static string Label(bool inProgress, GitBisectStep? step)
    {
        if (!inProgress) return "";
        if (step is null) return "bisect 中です。いまチェックアウトしている版を試して、良い／悪いを付けてください。";
        var where = step.CurrentHash is { } hash
            ? $"試している版: {Short(hash)} {step.CurrentSubject}"
            : "試している版: いまの HEAD";
        var left = step.Steps is { } steps ? $"あと約 {steps} 歩" : step.Remaining is { } r ? $"残り {r} 版" : null;
        return left is null ? $"bisect 中 · {where}" : $"bisect 中（{left}） · {where}";
    }

    public static string VerdictLabel(GitBisectVerdict verdict) => verdict switch
    {
        GitBisectVerdict.Good => "良い",
        GitBisectVerdict.Bad => "悪い",
        _ => "飛ばす",
    };

    public static string FoundMessage(string hash, string? subject)
        => string.IsNullOrWhiteSpace(subject)
            ? $"bisect：最初に悪くなったのは {Short(hash)} です。元の場所へ戻りました。"
            : $"bisect：最初に悪くなったのは {Short(hash)}「{subject}」です。元の場所へ戻りました。";

    public static string AmbiguousMessage(System.Collections.Generic.IReadOnlyList<string> candidates)
        => $"bisect：飛ばした版ばかりが残り、一つに絞れませんでした（候補 {candidates.Count} 件: " +
           string.Join(", ", System.Linq.Enumerable.Take(System.Linq.Enumerable.Select(candidates, Short), 5)) +
           "）。元の場所へ戻りました。";

    private static string Short(string hash) => hash.Length > 8 ? hash[..8] : hash;
}
