using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ワークツリーの片付け（§24.55）の選択結果。<see cref="Force"/> は未コミットの変更ごと消すか。</summary>
public sealed record WorktreeCleanupResult(IReadOnlyList<GitWorktreeInfo> Worktrees, bool Force);

/// <summary>使われていないワークツリーを大きさつきで並べ、選んで消す窓。大きさ・最後のコミット・取り込み済みかは
/// 開いてから裏で数え、数え終わったものから行に入る（閉じたら数えるのをやめる）。</summary>
public partial class WorktreeCleanupDialog : Window
{
    private readonly WorktreeCleanupViewModel _model;
    private readonly CancellationTokenSource _cts = new();

    private WorktreeCleanupDialog(WorktreeCleanupViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        Closed += (_, _) => _cts.Cancel();
    }

    public static WorktreeCleanupResult? Prompt(Window? owner, GitSessionViewModel git)
    {
        var now = DateTime.UtcNow;
        var rows = git.Worktrees
            .Where(w => !w.IsMain && !w.IsBare)
            .Select(w => new WorktreeCleanupRowViewModel(w, git.WorktreeRoomLastUsed?.Invoke(w.Path), now))
            .ToList();
        var model = new WorktreeCleanupViewModel(rows);
        var dialog = new WorktreeCleanupDialog(model) { Owner = owner };
        dialog.Loaded += async (_, _) => await dialog.MeasureAsync(git);
        return dialog.ShowDialog() == true
            ? new WorktreeCleanupResult(model.ToRemove.Select(r => r.Worktree).ToList(), model.DiscardChanges)
            : null;
    }

    private async Task MeasureAsync(GitSessionViewModel git)
    {
        var byPath = _model.Rows.ToDictionary(r => r.Worktree.Path, StringComparer.OrdinalIgnoreCase);
        // Progress は作った文脈（UI スレッド）へ戻して報告する。
        var progress = new Progress<GitWorktreeUsage>(usage =>
        {
            if (byPath.TryGetValue(usage.Path, out var row)) row.ApplyUsage(usage);
        });
        try
        {
            await git.InspectWorktreesAsync(_model.Rows.Select(r => r.Worktree).ToList(), progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _model.Summary = $"数えられませんでした: {ex.Message}";
        }
        // Progress の報告は後から届くことがあるので、一巡させてから締める。
        await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        foreach (var row in _model.Rows.Where(r => r.IsMeasuring))
            row.MarkMeasureFinished();
        _model.IsMeasuring = false;
        _model.PreselectCandidates();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var count = _model.ToRemove.Count;
        if (count == 0) return;
        var message = _model.DiscardChanges && _model.ToRemove.Any(r => r.Worktree.IsDirty)
            ? $"{count} 件のワークツリーを削除します。未コミットの変更があるものも、変更ごと消えます（復元できません）。\n続けますか？"
            : $"{count} 件のワークツリーを削除します（フォルダーと部屋が片付きます。ブランチは残ります）。\n続けますか？";
        if (MessageBox.Show(this, message, "ワークツリーの片付け", MessageBoxButton.OKCancel,
                _model.DiscardChanges ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        DialogResult = true;
    }
}
