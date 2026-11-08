using System.ComponentModel;
using System.Windows.Threading;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Git ペインで読んだコミットを軌跡の点にする（§24.103・GitLens 18.1.0 のコミット詳細の戻る／進む）。
/// <para>Git ペインへ移っただけでは <c>Pane</c> の点しか残らず、blame・行の履歴・親コミットと飛び歩いたあとに「さっき読んでいたあのコミット」へ
/// 戻れなかった。エディタ・ブラウザ・プレビューが対象まで戻るのと同じく、Git の面でも対象（コミット）まで戻す。</para>
/// <para>点にするのは <b>人が Git ペインで読んでいるとき</b>だけ：選んだコミットに <see cref="CommitDwell"/> とどまり、そのとき Git ペインに
/// フォーカスがある。矢印キーで一覧を流している間・読み直しで選択が付け直されただけ・軌跡を辿っている間（<c>_trailSuppressed</c>）は積まない。</para>
/// </summary>
public partial class ShellWindow {
    /// <summary>選んだコミットにこれだけとどまったら点にする（§27.3.3 の「とどまったら確定」と同じ考え方）。</summary>
    private static readonly TimeSpan CommitDwell = TimeSpan.FromSeconds(1.5);

    private DispatcherTimer? _trailCommitTimer;

    private void InitializeTrailCommits() {
        _trailCommitTimer = new DispatcherTimer { Interval = CommitDwell };
        _trailCommitTimer.Tick += (_, _) => {
            _trailCommitTimer.Stop();
            CommitTrailCommit();
        };
        _vm.GitSession.History.PropertyChanged += OnGitHistoryPropertyChanged;
        _trailJumpCoordinator.Register(TrailEntryKind.Commit, JumpToCommitAsync);
    }

    private void OnGitHistoryPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(ViewModels.GitHistoryViewModel.SelectedLogRow) || _trailCommitTimer is null)
            return;
        _trailCommitTimer.Stop();
        // 軌跡を辿って選び直した分は点にしない（辿った先で新しい点を積むと、過去を見ているだけで軌跡が伸びる）。
        if (_trailSuppressed)
            return;
        if (_vm.GitSession.History.SelectedLogRow?.Hash is not null)
            _trailCommitTimer.Start();
    }

    private void CommitTrailCommit() {
        if (_vm.GitSession.History.SelectedLogRow is not { Hash: { } hash } row
            || _vm.GitSession.RepositoryRoot is not { Length: > 0 } repository)
            return;
        // 人が Git ペインを読んでいるときだけ。読み直し・裏の fetch で選択が付け直されただけのものは積まない。
        if (_focusedRegion?.Pane != PaneKind.Git)
            return;
        RecordTrail((mode, stagePane, layout) =>
            _vm.Trail.RecordCommit(repository, hash, row.Subject, mode, stagePane, layout));
    }

    private async Task JumpToCommitAsync(TrailEntryViewModel entry) {
        if (!TrailLogic.TryParseCommitTarget(entry.Target, out var repository, out var hash))
            return;
        if (!Directory.Exists(repository)) {
            ToastService.Info($"このコミットのリポジトリがありません: {repository}");
            return;
        }
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
        if (!await _vm.GitSession.SelectCommitInRepositoryAsync(repository, hash))
            ToastService.Info($"コミット {hash[..Math.Min(hash.Length, 8)]} が見つかりません（消えたか、別のリポジトリへ移っています）");
        FocusPane(PaneKind.Git);
    }
}
