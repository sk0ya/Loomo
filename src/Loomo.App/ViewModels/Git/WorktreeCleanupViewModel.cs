using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ワークツリーの片付けの 1 行（§24.55）。大きさと最後のコミットは裏で数え終わったら入る。</summary>
public sealed partial class WorktreeCleanupRowViewModel : ObservableObject
{
    private readonly DateTime _nowUtc;

    public WorktreeCleanupRowViewModel(GitWorktreeInfo worktree, DateTime? roomLastUsedUtc, DateTime nowUtc)
    {
        Worktree = worktree;
        RoomLastUsedUtc = roomLastUsedUtc;
        _nowUtc = nowUtc;
        CanClean = GitWorktreeCleanup.CanClean(worktree);
        BlockedReason = GitWorktreeCleanup.BlockedReason(worktree);
        _isMeasuring = CanClean && worktree.Exists;
        Recompute();
    }

    public GitWorktreeInfo Worktree { get; }

    /// <summary>このワークツリーの部屋（ワークスペース）を最後に開いた時刻。部屋が無ければ null。</summary>
    public DateTime? RoomLastUsedUtc { get; }

    public bool HasRoom => RoomLastUsedUtc is not null;

    public bool CanClean { get; }
    public string? BlockedReason { get; }

    public GitWorktreeUsage? Usage { get; private set; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isMeasuring;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _lastActivityText = "";
    [ObservableProperty] private string _reasonText = "";
    [ObservableProperty] private bool _isCandidate;

    public string Name => Worktree.DisplayName;
    public string ChangesText => Worktree.IsDirty ? $"±{Worktree.ChangeCount}" : "";

    internal void ApplyUsage(GitWorktreeUsage usage)
    {
        Usage = usage;
        IsMeasuring = false;
        Recompute();
    }

    internal void MarkMeasureFinished()
    {
        IsMeasuring = false;
        Recompute();
    }

    private void Recompute()
    {
        SizeText = IsMeasuring ? "数えています…" : GitWorktreeCleanup.FormatSize(Usage?.SizeBytes);
        LastActivityText = GitWorktreeCleanup.FormatAgo(GitWorktreeCleanup.LastActivity(Usage, RoomLastUsedUtc), _nowUtc);
        var reasons = GitWorktreeCleanup.Reasons(Worktree, Usage, RoomLastUsedUtc, _nowUtc);
        IsCandidate = reasons != GitWorktreeCleanupReason.None;
        ReasonText = BlockedReason ?? GitWorktreeCleanup.ReasonText(reasons, Usage?.MergedInto);
    }
}

/// <summary>ワークツリーの片付けの面（§24.55）。並べて、選んで、まとめて消す。消すのは呼び出し側（Git ペイン）。</summary>
public sealed partial class WorktreeCleanupViewModel : ObservableObject
{
    public WorktreeCleanupViewModel(IEnumerable<WorktreeCleanupRowViewModel> rows)
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        // 候補だけのときも、印を付けた行は隠さない（選んだものが見えないまま消えるのを避ける）。
        View.Filter = o => !CandidatesOnly || o is WorktreeCleanupRowViewModel { IsCandidate: true } or WorktreeCleanupRowViewModel { IsChecked: true };
        foreach (var row in rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(WorktreeCleanupRowViewModel.IsChecked)
                    or nameof(WorktreeCleanupRowViewModel.SizeText)
                    or nameof(WorktreeCleanupRowViewModel.IsCandidate))
                    Refresh();
                if (e.PropertyName is nameof(WorktreeCleanupRowViewModel.IsCandidate))
                    View.Refresh();
            };
            Rows.Add(row);
        }
        Refresh();
    }

    /// <summary>並べる行（候補だけ／全部）。</summary>
    public ICollectionView View { get; }

    partial void OnCandidatesOnlyChanged(bool value) => View.Refresh();

    public ObservableCollection<WorktreeCleanupRowViewModel> Rows { get; } = new();

    /// <summary>候補（取り込み済み・しばらく触っていない・消えた）だけを出すか。</summary>
    [ObservableProperty] private bool _candidatesOnly = true;

    /// <summary>未コミットの変更があるものも消してよいか（既定は消さない——選んでも飛ばす）。</summary>
    [ObservableProperty] private bool _discardChanges;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _canRemove;

    /// <summary>数え終わったか（見出しの「数えています」を消す）。</summary>
    [ObservableProperty] private bool _isMeasuring = true;

    public IEnumerable<WorktreeCleanupRowViewModel> Selected => Rows.Where(r => r.IsChecked && r.CanClean);

    /// <summary>実際に消すもの（未コミットがあるものは <see cref="DiscardChanges"/> のときだけ）。</summary>
    public IReadOnlyList<WorktreeCleanupRowViewModel> ToRemove
        => Selected.Where(r => DiscardChanges || !r.Worktree.IsDirty).ToList();

    partial void OnDiscardChangesChanged(bool value) => Refresh();

    /// <summary>数え終わった候補のうち、未コミットの無いものに印を付ける（最初に一度だけ）。</summary>
    public void PreselectCandidates()
    {
        foreach (var row in Rows)
            row.IsChecked = row.CanClean && row.IsCandidate && !row.Worktree.IsDirty;
    }

    private void Refresh()
    {
        var remove = ToRemove;
        var skipped = Selected.Count() - remove.Count;
        var bytes = remove.Sum(r => r.Usage?.SizeBytes ?? 0);
        Summary = remove.Count == 0
            ? (skipped > 0 ? $"未コミットの変更があるものが {skipped} 件（消すには下の印を付ける）" : "消すワークツリーを選んでください")
            : $"{remove.Count} 件・{GitWorktreeCleanup.FormatSize(bytes)} を空けます"
              + (skipped > 0 ? $"（未コミットの変更がある {skipped} 件は残します）" : "");
        CanRemove = remove.Count > 0;
    }
}
