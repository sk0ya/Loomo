using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>ブランチの片付け（§24.69）の選択結果。<see cref="Force"/> は取り込まれていないものも <c>-D</c> で消すか。</summary>
public sealed record BranchCleanupResult(IReadOnlyList<GitBranchInfo> Branches, bool Force);

/// <summary>終わったローカルブランチを理由つきで並べ、選んで消す窓。取り込み済みかは開いてから裏で調べ、調べ終わったら行に入る
/// （上流が消えた・動いていないは一覧に既にある値なので、開いた時点で出る）。</summary>
public partial class BranchCleanupDialog : Window
{
    private readonly BranchCleanupViewModel _model;
    private readonly CancellationTokenSource _cts = new();

    private BranchCleanupDialog(BranchCleanupViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        Closed += (_, _) => _cts.Cancel();
    }

    public static BranchCleanupResult? Prompt(Window? owner, GitSessionViewModel git, string? defaultBranchLocal)
    {
        var now = DateTime.UtcNow;
        var inWorktrees = GitBranchCleanup.WorktreeBranches(git.Worktrees);
        var rows = git.LocalBranches
            .Select(b => new BranchCleanupRowViewModel(b,
                GitBranchCleanup.BlockedReason(b, defaultBranchLocal, inWorktrees), git.HasOpenPullRequest(b.Name), now))
            .OrderBy(r => r.CanClean ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var model = new BranchCleanupViewModel(rows);
        var dialog = new BranchCleanupDialog(model) { Owner = owner };
        dialog.Loaded += async (_, _) => await dialog.InspectAsync(git);
        return dialog.ShowDialog() == true
            ? new BranchCleanupResult(model.ToRemove.Select(r => r.Branch).ToList(), model.ForceUnmerged)
            : null;
    }

    private async Task InspectAsync(GitSessionViewModel git)
    {
        try
        {
            var names = _model.Rows.Where(r => r.CanClean).Select(r => r.Name).ToList();
            var (defaultBranch, merged) = await git.InspectBranchMergesAsync(names, _cts.Token);
            foreach (var row in _model.Rows)
                row.ApplyMerged(merged.Contains(row.Name), defaultBranch);
            if (defaultBranch is null)
                _model.Summary = "既定ブランチが決まらないので、取り込み済みかは調べていません。";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _model.Summary = $"調べられませんでした: {ex.Message}";
        }
        _model.IsInspecting = false;
        _model.PreselectCandidates();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var remove = _model.ToRemove;
        if (remove.Count == 0) return;
        var unmerged = remove.Count(r => !r.IsMerged);
        var message = unmerged > 0
            ? $"{remove.Count} 本のブランチを削除します。うち {unmerged} 本は既定ブランチへ取り込まれていないので -D で消します（コミットが失われることがあります）。\n続けますか？"
            : $"{remove.Count} 本のブランチを削除します（リモートのブランチは残ります）。\n続けますか？";
        if (MessageBox.Show(this, message, "ブランチの片付け", MessageBoxButton.OKCancel,
                unmerged > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        DialogResult = true;
    }
}
