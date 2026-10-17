using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// Claude Code が IDE 連携で出してきた編集の提案（<c>openDiff</c>、§38）を、ターミナルペインの上端のバーで
/// 受け入れる／却下する。差分そのものは Diff（ペインか別ウィンドウ）で見る——ここは「決める」だけの場所。
/// <para>Claude はターミナルでも同じ提案に Yes/No を聞いている。そちらで答えれば Claude が <c>close_tab</c> を
/// 送ってくるので、その提案はバーから消える（<see cref="Enqueue"/> の取り消し）。どちらで答えても同じ結論になる。</para>
/// <para>提案は重なりうる（並行するエージェント）ので列に積み、先頭から1件ずつ見せる。UI スレッド専用。</para>
/// </summary>
public sealed partial class ClaudeProposalViewModel : ObservableObject
{
    private sealed record Pending(
        string TabName, string FilePath, string Contents, TaskCompletionSource<ClaudeIdeDiffDecision> Decision);

    private readonly List<Pending> _queue = [];

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _message = "";

    /// <summary>先頭の提案の差分を見せてほしい（ファイル・提案本文）。</summary>
    public event Action<string, string>? ShowDiffRequested;

    /// <summary>受け入れた（ファイル）。書くのは Claude なので、少し後に読み直すきっかけに使う。</summary>
    public event Action<string>? Accepted;

    public Task<ClaudeIdeDiffDecision> Enqueue(string tabName, string filePath, string contents, CancellationToken cancellationToken)
    {
        var pending = new Pending(tabName, filePath, contents,
            new TaskCompletionSource<ClaudeIdeDiffDecision>(TaskCreationOptions.RunContinuationsAsynchronously));
        _queue.Add(pending);
        cancellationToken.Register(() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Resolve(pending, null)));
        if (_queue.Count == 1)
            ShowHead(openDiff: true);
        else
            Refresh();
        return pending.Decision.Task;
    }

    [RelayCommand]
    private void Accept()
    {
        if (_queue.FirstOrDefault() is { } head)
        {
            Resolve(head, new ClaudeIdeDiffDecision(true, head.Contents));
            Accepted?.Invoke(head.FilePath);
        }
    }

    [RelayCommand]
    private void Reject()
    {
        if (_queue.FirstOrDefault() is { } head)
            Resolve(head, ClaudeIdeDiffDecision.Rejected);
    }

    [RelayCommand]
    private void ShowDiff()
    {
        if (_queue.FirstOrDefault() is { } head)
            ShowDiffRequested?.Invoke(head.FilePath, head.Contents);
    }

    /// <summary><paramref name="decision"/> が null なら Claude 側で片付いた（却下として閉じる）。</summary>
    private void Resolve(Pending pending, ClaudeIdeDiffDecision? decision)
    {
        var wasHead = _queue.FirstOrDefault() == pending;
        if (!_queue.Remove(pending))
            return;
        pending.Decision.TrySetResult(decision ?? ClaudeIdeDiffDecision.Rejected);
        if (wasHead)
            ShowHead(openDiff: _queue.Count > 0);
        else
            Refresh();
    }

    private void ShowHead(bool openDiff)
    {
        Refresh();
        if (openDiff)
            ShowDiff();
    }

    private void Refresh()
    {
        IsVisible = _queue.Count > 0;
        if (_queue.FirstOrDefault() is not { } head)
            return;
        var more = _queue.Count > 1 ? $"（ほか {_queue.Count - 1} 件）" : "";
        Message = $"Claude が {System.IO.Path.GetFileName(head.FilePath)} の編集を提案しています{more}";
    }
}
