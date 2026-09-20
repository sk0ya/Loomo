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
/// 「何に対する差分として見るか」（作業ツリー／ブランチ／分岐点）の選択状態。
/// <see cref="GitRootSwitchViewModel"/> と同じく Singleton——サイドバー Git パネルと Diff ペインは
/// 別々の画面領域だが<b>同じ一つの基準</b>を見ているので、どちらで切り替えても両方に反映される。
///
/// <para>解決（ref の確定）は毎回実行する。分岐点は fetch でリモートが動けば変わり、
/// ブランチは消えることもあるため、選択変更時は最新の ref を確認する。一方、下位の GitService は
/// status／ブランチ／ログなどの同一照会を共有キャッシュし、RepositoryChanged でまとめて破棄する。
/// そのため Git パネルと Diff ペインが同じ照会を重ねて git を起動することはない。</para>
///
/// <para>既知の限界：<see cref="GitRepositoryMonitor"/> のポーリング署名は <c>git status</c> の出力と
/// 作業ツリーの印だけなので、<b>Loomo の外で</b> fetch してリモート追跡ブランチだけが動いた場合、
/// 分岐点が変わっても自動では気づかない。アプリ内のフェッチ／プル等は
/// <see cref="GitMutationExecutor"/> が変更を通知するので追従する。</para>
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

    public GitCompareBaseViewModel(GitService git)
    {
        _git = git;
        _selectedMode = ModeOptions[0];
        _git.ActiveRootChanged += (_, _) => DispatchReloadBranches();
        // ブランチが増減しても候補が古いままにならないように。ただし候補が画面に出ている
        // （＝ブランチ／分岐点基準の）ときだけ引く——作業ツリー基準では git を1回も起動しない。
        _git.RepositoryChanged += (_, _) =>
        {
            if (NeedsBranch) DispatchReloadBranches();
        };
    }

    /// <summary>比較基準が切り替わった（種別・ブランチのどちらでも）。購読側は一覧と差分を読み直す。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<GitCompareModeOption> ModeOptions { get; } = new[]
    {
        new GitCompareModeOption(GitCompareBaseKind.WorkingTree, "作業ツリー"),
        new GitCompareModeOption(GitCompareBaseKind.Branch, "ブランチと比較"),
        new GitCompareModeOption(GitCompareBaseKind.MergeBase, "分岐点と比較"),
    };

    [ObservableProperty] private GitCompareModeOption _selectedMode;

    /// <summary>比較先に選べるブランチ（ローカル＋リモート追跡）。</summary>
    [ObservableProperty] private IReadOnlyList<string> _branchOptions = Array.Empty<string>();

    [ObservableProperty] private string? _selectedBranch;

    /// <summary>基準を解決できなかった理由（空リポジトリ・ブランチ不在・分岐点なし）。空なら問題なし。
    /// 一覧の取得そのものが失敗した理由は<b>ここではなく</b>一覧側（空メッセージ）に出す。</summary>
    [ObservableProperty] private string _errorMessage = "";

    /// <summary>今の基準。UI の2つの選択から組み立てた正本。</summary>
    public GitCompareBaseSelection Selection => new(SelectedMode.Kind, SelectedBranch);

    /// <summary>作業ツリー基準か（表示の分岐は <see cref="Capabilities"/> を使うこと）。</summary>
    public bool IsWorkingTree => SelectedMode.Kind == GitCompareBaseKind.WorkingTree;

    /// <summary>ブランチ選択 ComboBox を出すか（作業ツリー基準では出さない）。</summary>
    public bool NeedsBranch => SelectedMode.Kind != GitCompareBaseKind.WorkingTree;

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

    /// <summary>作業ツリー基準へ戻す（Diff ペインの「作業ツリーへ」など）。</summary>
    public void ResetToWorkingTree()
    {
        if (IsWorkingTree) return;
        SelectedMode = ModeOptions[0];
    }

    public GitCompareSnapshot Capture()
        => new() { Kind = (int)SelectedMode.Kind, Branch = _desiredBranch ?? SelectedBranch };

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
            SelectedMode = ModeOptions.First(o => o.Kind == kind);
            ErrorMessage = "";
        }
        NotifyModeDependents();
        RaiseChanged();
        _ = ReloadBranchesAsync();
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
            _ = ReloadThenNotifyAsync();
            return;
        }
        RaiseChanged();
    }

    partial void OnSelectedBranchChanged(string? value)
    {
        OnPropertyChanged(nameof(Selection));
        RaiseChanged();
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    private async Task ReloadThenNotifyAsync()
    {
        if (!await ReloadBranchesAsync())
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

    // GitService の通知はポーリングのタイマースレッドから来るので、UI スレッドへ寄せてから読み込む。
    private void DispatchReloadBranches() => UiDispatch.Post(() => _ = ReloadBranchesAsync());

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
