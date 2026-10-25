using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ブランチの片付けの 1 行（§24.69）。取り込み済みかは裏で調べ終わったら入る。</summary>
public sealed partial class BranchCleanupRowViewModel : ObservableObject
{
    private readonly DateTime _nowUtc;

    public BranchCleanupRowViewModel(GitBranchInfo branch, string? blockedReason, bool hasOpenPullRequest, DateTime nowUtc)
    {
        Branch = branch;
        BlockedReason = blockedReason;
        HasOpenPullRequest = hasOpenPullRequest;
        _nowUtc = nowUtc;
        Recompute();
    }

    public GitBranchInfo Branch { get; }
    public string Name => Branch.Name;
    public string? BlockedReason { get; }
    public bool CanClean => BlockedReason is null;
    public bool HasOpenPullRequest { get; }
    public string PullRequestText => HasOpenPullRequest ? "PR あり" : "";
    public string LastCommitText => GitWorktreeCleanup.FormatAgo(Branch.LastCommit?.UtcDateTime, _nowUtc);
    public string AheadText => Branch.Ahead > 0 ? $"↑{Branch.Ahead}" : "";

    /// <summary>既定ブランチへ取り込み済みか（調べ終わるまで false）。</summary>
    public bool IsMerged { get; private set; }

    public GitBranchCleanupReason Reasons { get; private set; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _reasonText = "";
    [ObservableProperty] private string _reasonDetail = "";
    [ObservableProperty] private bool _isCandidate;

    private string? _mergedInto;

    internal void ApplyMerged(bool merged, string? mergedInto)
    {
        IsMerged = merged;
        _mergedInto = mergedInto;
        Recompute();
    }

    private void Recompute()
    {
        Reasons = CanClean ? GitBranchCleanup.Reasons(Branch, IsMerged, _nowUtc) : GitBranchCleanupReason.None;
        IsCandidate = Reasons != GitBranchCleanupReason.None;
        ReasonText = BlockedReason ?? GitBranchCleanup.ReasonText(Reasons, _mergedInto, Branch.Upstream);
        ReasonDetail = BlockedReason ?? GitBranchCleanup.ReasonDetail(Reasons, _mergedInto, Branch.Upstream);
    }
}

/// <summary>ブランチの片付けの面（§24.69）。並べて、選んで、まとめて消す。消すのは呼び出し側（Git ペイン）。</summary>
public sealed partial class BranchCleanupViewModel : ObservableObject
{
    public BranchCleanupViewModel(IEnumerable<BranchCleanupRowViewModel> rows)
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        // 候補だけのときも、印を付けた行は隠さない（選んだものが見えないまま消えるのを避ける）。
        View.Filter = o => !CandidatesOnly || o is BranchCleanupRowViewModel { IsCandidate: true } or BranchCleanupRowViewModel { IsChecked: true };
        foreach (var row in rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(BranchCleanupRowViewModel.IsChecked) or nameof(BranchCleanupRowViewModel.IsCandidate))
                    Refresh();
                if (e.PropertyName is nameof(BranchCleanupRowViewModel.IsCandidate))
                    View.Refresh();
            };
            Rows.Add(row);
        }
        Refresh();
    }

    public ICollectionView View { get; }

    public ObservableCollection<BranchCleanupRowViewModel> Rows { get; } = new();

    [ObservableProperty] private bool _candidatesOnly = true;

    /// <summary>取り込まれていないもの（上流が消えた・動いていないだけ）も <c>-D</c> で消すか。既定は消さない——選んでも飛ばす。</summary>
    [ObservableProperty] private bool _forceUnmerged;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _canRemove;
    [ObservableProperty] private bool _isInspecting = true;

    partial void OnCandidatesOnlyChanged(bool value) => View.Refresh();
    partial void OnForceUnmergedChanged(bool value) => Refresh();

    public IEnumerable<BranchCleanupRowViewModel> Selected => Rows.Where(r => r.IsChecked && r.CanClean);

    /// <summary>実際に消すもの（取り込まれていないものは <see cref="ForceUnmerged"/> のときだけ）。</summary>
    public IReadOnlyList<BranchCleanupRowViewModel> ToRemove
        => Selected.Where(r => ForceUnmerged || r.IsMerged).ToList();

    /// <summary>調べ終わった候補に印を付ける（最初に一度だけ）。</summary>
    public void PreselectCandidates()
    {
        foreach (var row in Rows)
            row.IsChecked = GitBranchCleanup.Preselect(row.Reasons, row.CanClean, row.HasOpenPullRequest);
    }

    private void Refresh()
    {
        var remove = ToRemove;
        var skipped = Selected.Count() - remove.Count;
        Summary = remove.Count == 0
            ? (skipped > 0 ? $"取り込まれていないものが {skipped} 本（消すには下の印を付ける）" : "消すブランチを選んでください")
            : $"{remove.Count} 本を削除します" + (skipped > 0 ? $"（取り込まれていない {skipped} 本は残します）" : "");
        CanRemove = remove.Count > 0;
    }
}
