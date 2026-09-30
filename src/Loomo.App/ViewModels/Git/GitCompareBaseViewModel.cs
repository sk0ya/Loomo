using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>比較基準の種別を選ぶ ComboBox の1候補。</summary>
public sealed record GitCompareModeOption(GitCompareBaseKind Kind, string Label);

/// <summary>
/// 「何に対する差分として見るか」（作業ツリー／ブランチ／分岐点／ワークツリー／リビジョン）の選択状態。
/// <see cref="GitRootSwitchViewModel"/> と同じく Singleton——サイドバー Git パネルと Diff ペインは
/// 別々の画面領域だが<b>同じ一つの基準</b>を見ているので、どちらで切り替えても両方に反映される。
///
/// <para>解決（ref の確定）は毎回実行する。分岐点は fetch でリモートが動けば変わり、
/// ブランチは消えることもあるため、選択変更時は最新の ref を確認する。一方、下位の GitService は
/// status／ブランチ／ログなどの同一照会を共有キャッシュし、RepositoryChanged でまとめて破棄する。
/// そのため Git パネルと Diff ペインが同じ照会を重ねて git を起動することはない。</para>
///
/// <para><b>対象は種別ごとに別の欄で持つ</b>（ブランチ名・ワークツリーのパス・リビジョン）。1つの欄を
/// 使い回すと、「ブランチと比較（main）」から「ワークツリーと比較」へ切り替えた瞬間に main をパスとして
/// 解決しに行き、戻したときには選んでいたブランチが消えている。</para>
///
/// <para>既知の限界：<see cref="GitRepositoryMonitor"/> のポーリング署名は <c>git status</c> の出力と
/// 作業ツリーの印だけなので、<b>Loomo の外で</b> fetch してリモート追跡ブランチだけが動いた場合、
/// 分岐点が変わっても自動では気づかない。アプリ内のフェッチ／プル等は
/// <see cref="GitMutationExecutor"/> が変更を通知するので追従する。ワークツリー基準の相手側の編集も
/// 同じく監視には現れない——一覧を読み直すたびに固め直すので、こちらで何か変われば追いつく。</para>
/// </summary>
public sealed partial class GitCompareBaseViewModel : ObservableObject
{
    private readonly GitService _git;

    /// <summary>通知の抑止段数。<b>同期区間だけ</b>を包む——await を跨いで抑止すると、その待ち時間に
    /// ユーザーが基準を切り替えた通知まで飲み込んで「押せるのに何も起きない」になる。</summary>
    private int _suppressDepth;

    /// <summary>
    /// まだ実在を確認できていない「選びたい枝」。ワークスペース復元は
    /// <c>FolderTree.LoadRoot</c> と前後し得るので、候補（<see cref="BranchOptions"/>）が空のうちに
    /// 復元値を捨てると保存した枝が既定ブランチへすり替わる。候補が揃った時点で実在すれば再適用し、
    /// <b>候補があるのにその中に無い＝本当に消えた枝</b>のときだけ諦める。
    /// </summary>
    private string? _desiredBranch;

    /// <summary><see cref="_desiredBranch"/> のワークツリー版（候補が読めるまで復元値を預かる）。</summary>
    private string? _desiredWorktree;

    public GitCompareBaseViewModel(GitService git)
    {
        _git = git;
        _selectedMode = ModeOptions[0];
        _git.ActiveRootChanged += (_, _) =>
        {
            DispatchReloadBranches();
            // ワークツリー基準のときだけ引く（作業ツリー基準では git を起動しない約束を守る）。
            if (NeedsWorktree) DispatchReloadWorktrees();
        };
        // ブランチが増減しても候補が古いままにならないように。ただし候補が画面に出ている
        // （＝ブランチ／分岐点基準の）ときだけ引く——作業ツリー基準では git を1回も起動しない。
        _git.RepositoryChanged += (_, _) =>
        {
            if (NeedsBranch) DispatchReloadBranches();
            if (NeedsWorktree) DispatchReloadWorktrees();
        };
    }

    /// <summary>比較基準が切り替わった（種別・対象のどちらでも）。購読側は一覧と差分を読み直す。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<GitCompareModeOption> ModeOptions { get; } = new[]
    {
        new GitCompareModeOption(GitCompareBaseKind.WorkingTree, "作業ツリー"),
        new GitCompareModeOption(GitCompareBaseKind.Branch, "ブランチと比較"),
        new GitCompareModeOption(GitCompareBaseKind.MergeBase, "分岐点と比較"),
        new GitCompareModeOption(GitCompareBaseKind.Worktree, "ワークツリーと比較"),
        new GitCompareModeOption(GitCompareBaseKind.Revision, "リビジョンと比較"),
    };

    [ObservableProperty] private GitCompareModeOption _selectedMode;

    /// <summary>比較先に選べるブランチ（ローカル＋リモート追跡）。</summary>
    [ObservableProperty] private IReadOnlyList<string> _branchOptions = Array.Empty<string>();

    [ObservableProperty] private string? _selectedBranch;

    /// <summary>比較先に選べるワークツリー（いま開いているもの自身と bare は除く）。</summary>
    [ObservableProperty] private IReadOnlyList<GitWorktreeInfo> _worktreeOptions = Array.Empty<GitWorktreeInfo>();

    /// <summary>比較先ワークツリーのパス（<see cref="WorktreeOptions"/> の <see cref="GitWorktreeInfo.Path"/>）。</summary>
    [ObservableProperty] private string? _selectedWorktree;

    /// <summary>比較先リビジョン（タグ・ハッシュ・<c>HEAD~1</c> 等）。入力欄は確定（Enter／フォーカス移動）で書き戻す。</summary>
    [ObservableProperty] private string? _revision;

    /// <summary>基準を解決できなかった理由（空リポジトリ・ブランチ不在・分岐点なし）。空なら問題なし。
    /// 一覧の取得そのものが失敗した理由は<b>ここではなく</b>一覧側（空メッセージ）に出す。</summary>
    [ObservableProperty] private string _errorMessage = "";

    /// <summary>今の基準。UI の選択から組み立てた正本。</summary>
    public GitCompareBaseSelection Selection => new(SelectedMode.Kind, TargetFor(SelectedMode.Kind));

    /// <summary>作業ツリー基準か（表示の分岐は <see cref="Capabilities"/> を使うこと）。</summary>
    public bool IsWorkingTree => SelectedMode.Kind == GitCompareBaseKind.WorkingTree;

    /// <summary>ブランチ選択 ComboBox を出すか（ブランチ／分岐点基準のときだけ）。</summary>
    public bool NeedsBranch => SelectedMode.Kind is GitCompareBaseKind.Branch or GitCompareBaseKind.MergeBase;

    /// <summary>ワークツリー選択 ComboBox を出すか。</summary>
    public bool NeedsWorktree => SelectedMode.Kind == GitCompareBaseKind.Worktree;

    /// <summary>リビジョン入力欄を出すか。</summary>
    public bool NeedsRevision => SelectedMode.Kind == GitCompareBaseKind.Revision;

    /// <summary>ワークツリー基準なのに比べる相手が1つも無い（案内を出す）。</summary>
    public bool HasNoWorktreeOptions => NeedsWorktree && WorktreeOptions.Count == 0;

    /// <summary>この基準で意味を持つ操作。ビューのゲートもコマンドのガードもここ一箇所を見る。</summary>
    public GitCompareCapabilities Capabilities => GitCompareCapabilities.For(SelectedMode.Kind);

    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>今の基準を実際の ref へ解決する。作業ツリー基準なら git を起動しない。</summary>
    public async Task<GitCompareResolution> ResolveAsync()
    {
        if (IsWorkingTree)
        {
            SetError("");
            return GitCompareResolution.WorkingTree;
        }
        var resolution = await _git.ResolveCompareBaseAsync(Selection);
        SetError(resolution.Error ?? "");
        return resolution;
    }

    /// <summary>
    /// 基準を外から一度に切り替える（ワークツリー一覧の「このワークツリーと比較」、コミットの
    /// 「作業ツリーと比較」など）。種別と対象を<b>まとめて</b>書き、通知は1回だけ出す——
    /// 別々に書くと、種別だけ変わった瞬間に古い対象で一度解決しに行き、一覧が2回ちらつく。
    /// </summary>
    public void Apply(GitCompareBaseSelection selection)
    {
        if (Selection == selection) return;
        using (Suppress())
        {
            switch (selection.Kind)
            {
                case GitCompareBaseKind.Branch or GitCompareBaseKind.MergeBase:
                    _desiredBranch = selection.Target;
                    SelectedBranch = selection.Target;
                    break;
                case GitCompareBaseKind.Worktree:
                    _desiredWorktree = selection.Target;
                    SelectedWorktree = selection.Target;
                    break;
                case GitCompareBaseKind.Revision:
                    Revision = selection.Target;
                    break;
            }
            SelectedMode = ModeOptions.First(o => o.Kind == selection.Kind);
            ErrorMessage = "";
        }
        NotifyModeDependents();
        RaiseChanged();
        if (NeedsBranch) _ = ReloadBranchesAsync();
        if (NeedsWorktree) _ = ReloadWorktreesAsync();
    }

    /// <summary>
    /// ブランチ候補を読み直す（リポジトリ・対象フォルダーが変わったとき）。まだブランチを選んでいなければ
    /// 既定ブランチ（origin/HEAD → main → master …）を第一候補にし、候補があるのにその中に無い枝は外す。
    /// </summary>
    /// <returns>選択が動いて <see cref="Changed"/> を出したか。</returns>
    public async Task<bool> ReloadBranchesAsync()
    {
        // git を叩く待ち時間は抑止の外に置く（await を跨いで抑止しない）。
        var refs = await _git.GetComparableRefsAsync();
        var wanted = _desiredBranch ?? SelectedBranch;
        var wantedExists = wanted is not null && refs.Contains(wanted, StringComparer.Ordinal);
        var fallback = !wantedExists && refs.Count > 0
            ? await _git.GetDefaultBranchAsync(refs)
            : null;

        var before = SelectedBranch;
        using (Suppress())
        {
            ApplyBranchOptions(refs);
            if (wantedExists)
            {
                _desiredBranch = null;             // 実在を確認できたので保留を解消
                SelectedBranch = wanted;
            }
            else if (refs.Count == 0)
            {
                // 候補がまだ読めていない（リポジトリ未オープン・git 不在）。選びたい枝は捨てずに待つ。
                _desiredBranch = wanted;
            }
            else
            {
                // 候補はあるのにその中に無い＝本当に消えた枝。既定ブランチへ寄せる。
                _desiredBranch = null;
                SelectedBranch = fallback;
            }
        }

        if (NeedsBranch && !string.Equals(before, SelectedBranch, StringComparison.Ordinal))
        {
            RaiseChanged();
            return true;
        }
        return false;
    }

    /// <summary>
    /// ワークツリー候補を読み直す。選んでいたものが消えていれば（候補がある限り）先頭へ寄せる。
    /// 候補が0件なら選択は預かったまま（リポジトリ未オープンのうちに復元値を捨てない）。
    /// </summary>
    /// <returns>選択が動いて <see cref="Changed"/> を出したか。</returns>
    public async Task<bool> ReloadWorktreesAsync()
    {
        // ワークツリー基準でないときに候補外の値を先頭へ寄せると、別の種別で使っている間に
        // 保存してあった選択が消える（ルート切替の直後など、候補がまだ別リポジトリのものであり得る）。
        if (!NeedsWorktree) return false;
        var all = await _git.GetWorktreesAsync();
        var options = all.Where(w => !w.IsCurrent && !w.IsBare).ToList();
        var wanted = _desiredWorktree ?? SelectedWorktree;
        var match = wanted is null ? null : options.FirstOrDefault(w => w.IsSamePath(wanted));

        var before = SelectedWorktree;
        using (Suppress())
        {
            if (!SameWorktrees(options, WorktreeOptions))
                WorktreeOptions = options;
            if (match is not null)
            {
                _desiredWorktree = null;
                SelectedWorktree = match.Path;
            }
            else if (options.Count == 0)
            {
                _desiredWorktree = wanted;
            }
            else
            {
                _desiredWorktree = null;
                SelectedWorktree = options[0].Path;
            }
        }
        OnPropertyChanged(nameof(HasNoWorktreeOptions));

        if (NeedsWorktree && !string.Equals(before, SelectedWorktree, StringComparison.OrdinalIgnoreCase))
        {
            RaiseChanged();
            return true;
        }
        return false;
    }

    /// <summary>作業ツリー基準へ戻す（Diff ペインの「作業ツリーへ」など）。</summary>
    public void ResetToWorkingTree()
    {
        if (IsWorkingTree) return;
        SelectedMode = ModeOptions[0];
    }

    public GitCompareSnapshot Capture() => new()
    {
        Kind = (int)SelectedMode.Kind,
        Branch = _desiredBranch ?? SelectedBranch,
        Worktree = _desiredWorktree ?? SelectedWorktree,
        Revision = Revision,
    };

    /// <summary>ワークスペース復元。この時点で候補が読めているとは限らない（Git の対象フォルダーが
    /// まだ切り替わっていないことがある）ので、選びたい枝を <see cref="_desiredBranch"/> に預け、
    /// <see cref="ReloadBranchesAsync"/> が候補を得た時点で実在すれば適用する。</summary>
    public void Restore(GitCompareSnapshot? snapshot)
    {
        var kind = snapshot is not null && Enum.IsDefined(typeof(GitCompareBaseKind), snapshot.Kind)
            ? (GitCompareBaseKind)snapshot.Kind
            : GitCompareBaseKind.WorkingTree;
        using (Suppress())
        {
            _desiredBranch = snapshot?.Branch;
            SelectedBranch = snapshot?.Branch;
            _desiredWorktree = snapshot?.Worktree;
            SelectedWorktree = snapshot?.Worktree;
            Revision = snapshot?.Revision;
            SelectedMode = ModeOptions.First(o => o.Kind == kind);
            ErrorMessage = "";
        }
        NotifyModeDependents();
        RaiseChanged();
        _ = ReloadBranchesAsync();
        if (NeedsWorktree) _ = ReloadWorktreesAsync();
    }

    partial void OnSelectedModeChanged(GitCompareModeOption value)
    {
        NotifyModeDependents();
        if (_suppressDepth > 0) return;
        // ブランチ基準へ切り替えた瞬間に選ぶものが無いと「押せるのに何も起きない」ので、
        // 先に候補（＝既定ブランチ）を埋めてから通知する。読み込みが選択を動かせばそちらが
        // 通知を出すので、出なかったときだけここで種別の切替として1回出す（二重に出さない）。
        if (NeedsBranch && SelectedBranch is null)
        {
            _ = ReloadThenNotifyAsync(ReloadBranchesAsync);
            return;
        }
        // ワークツリーは選ぶたびに候補を読み直す（作った・消したがブランチより頻繁で、しかも
        // こちらのリポジトリ監視に現れないことがある）。
        if (NeedsWorktree)
        {
            _ = ReloadThenNotifyAsync(ReloadWorktreesAsync);
            return;
        }
        RaiseChanged();
    }

    partial void OnSelectedBranchChanged(string? value)
    {
        OnPropertyChanged(nameof(Selection));
        if (NeedsBranch) RaiseChanged();
    }

    partial void OnSelectedWorktreeChanged(string? value)
    {
        OnPropertyChanged(nameof(Selection));
        if (NeedsWorktree) RaiseChanged();
    }

    partial void OnRevisionChanged(string? value)
    {
        OnPropertyChanged(nameof(Selection));
        if (NeedsRevision) RaiseChanged();
    }

    partial void OnWorktreeOptionsChanged(IReadOnlyList<GitWorktreeInfo> value)
        => OnPropertyChanged(nameof(HasNoWorktreeOptions));

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    private string? TargetFor(GitCompareBaseKind kind) => kind switch
    {
        GitCompareBaseKind.Branch or GitCompareBaseKind.MergeBase => SelectedBranch,
        GitCompareBaseKind.Worktree => SelectedWorktree,
        GitCompareBaseKind.Revision => string.IsNullOrWhiteSpace(Revision) ? null : Revision.Trim(),
        _ => null,
    };

    private async Task ReloadThenNotifyAsync(Func<Task<bool>> reload)
    {
        if (!await reload())
            RaiseChanged();
    }

    /// <summary>
    /// 候補を差し替える。WPF の <c>Selector</c> は ItemsSource を差し替えると選択をクリアし、
    /// TwoWay の <c>SelectedItem</c> バインドへ<b>null を書き戻す</b>——放っておくと、ブランチが1本
    /// 増減しただけでユーザーの選んだ基準が既定ブランチへ勝手に移る。候補に残っている選択は
    /// ここで明示的に戻す（この区間は <see cref="Suppress"/> の中なので通知は出ない）。
    /// </summary>
    private void ApplyBranchOptions(IReadOnlyList<string> refs)
    {
        // 中身が同じなら差し替えない（差し替えのたびに選択が跳ねるのを避ける）。
        if (refs.SequenceEqual(BranchOptions, StringComparer.Ordinal)) return;
        var keep = SelectedBranch;
        BranchOptions = refs;
        if (keep is not null && refs.Contains(keep, StringComparer.Ordinal))
            SelectedBranch = keep;
    }

    /// <summary>候補の見た目（パス・名前）が同じなら差し替えない（ComboBox の選択が跳ねるのを避ける）。
    /// 変更件数は比べない——相手で1ファイル触るたびに候補を差し替えることになる。</summary>
    private static bool SameWorktrees(IReadOnlyList<GitWorktreeInfo> left, IReadOnlyList<GitWorktreeInfo> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.IsSamePath(pair.Second.Path)
            && string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal));

    // GitService の通知はポーリングのタイマースレッドから来るので、UI スレッドへ寄せてから読み込む。
    private void DispatchReloadBranches() => UiDispatch.Post(() => _ = ReloadBranchesAsync());

    private void DispatchReloadWorktrees() => UiDispatch.Post(() => _ = ReloadWorktreesAsync());

    private void RaiseChanged()
    {
        if (_suppressDepth > 0) return;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyModeDependents()
    {
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(IsWorkingTree));
        OnPropertyChanged(nameof(NeedsBranch));
        OnPropertyChanged(nameof(NeedsWorktree));
        OnPropertyChanged(nameof(NeedsRevision));
        OnPropertyChanged(nameof(HasNoWorktreeOptions));
        OnPropertyChanged(nameof(Capabilities));
    }

    private void SetError(string message)
    {
        if (ErrorMessage != message)
            ErrorMessage = message;
    }

    private IDisposable Suppress()
    {
        _suppressDepth++;
        return new Suppression(this);
    }

    private sealed class Suppression : IDisposable
    {
        private GitCompareBaseViewModel? _owner;
        public Suppression(GitCompareBaseViewModel owner) => _owner = owner;

        public void Dispose()
        {
            var owner = _owner;
            _owner = null;
            if (owner is not null) owner._suppressDepth--;
        }
    }
}
