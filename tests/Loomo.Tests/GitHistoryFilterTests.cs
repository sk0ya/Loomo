using System.IO;
using System.Threading;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Git ペインのコミット絞り込み。守りたいのは「読み込み済みページの外も検索できる」ことと、
/// その副作用で「作者ドロップダウンが痩せない」こと。
/// 読み直しは実際には打鍵から少し遅れて走る（デバウンス）ので、ここでは <c>ReloadAsync</c> を直接呼ぶ。
///
/// <para>一覧は <see cref="System.Windows.Data.CollectionViewSource"/> 経由の
/// <c>ICollectionView</c> を持ち、<b>作った側のスレッドからしか変更できない</b>。
/// テストの <c>await</c> が既定でスレッドプールへ戻ると例外になるので、
/// VM の生成から後始末までを1本のディスパッチャースレッド上で回す（＝実アプリと同じ形）。</para>
/// </summary>
[Collection(GitProcessTests.Name)]
public sealed class GitHistoryFilterTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "loomo-git-history-tests", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;
    private GitHistoryViewModel _history = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);

        await MustRunAsync("init", "-b", "main");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 競合は無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public void 検索語は一覧そのものを絞る() => RunOnDispatcher(async () =>
    {
        await CommitAsync("Alice", "a.txt", "ずっと前の修正");
        await CommitAsync("Alice", "b.txt", "ふつうのコミット");

        _history.LogFilter = "ずっと前";
        await _history.ReloadAsync();

        // 見えている一覧（篩後）だけでなく、<b>読み込んだ行そのもの</b>が絞られている
        // ＝git に条件が渡っている証拠。クライアント側の篩だけなら LogRows には2件残る。
        Assert.Equal(new[] { "ずっと前の修正" }, Subjects());
        Assert.Equal(new[] { "ずっと前の修正" },
            _history.LogRows.Where(row => row.IsCommit).Select(row => row.Subject).ToArray());
    });

    [Fact]
    public void 作者で絞り込んでも作者の選択肢は痩せない() => RunOnDispatcher(async () =>
    {
        await CommitAsync("Alice", "a.txt", "alice のコミット");
        await CommitAsync("Bob", "b.txt", "bob のコミット");
        await _history.ReloadAsync();

        var before = _history.AuthorOptions;
        Assert.Equal(new[] { GitHistoryViewModel.AllAuthorsLabel, "Alice", "Bob" }, before);

        _history.AuthorSelection = "Alice";
        await _history.ReloadAsync();

        // 一覧は Alice だけになるが、選択肢が1件に痩せると他の作者へ切り替えられなくなる
        Assert.Equal(new[] { "alice のコミット" }, Subjects());
        Assert.Equal(new[] { GitHistoryViewModel.AllAuthorsLabel, "Alice", "Bob" }, _history.AuthorOptions);
        // 顔ぶれが変わらないので同じインスタンス（ComboBox の状態を壊さない）
        Assert.Same(before, _history.AuthorOptions);
    });

    [Fact]
    public void スコープを変えると作者の顔ぶれを引き直す() => RunOnDispatcher(async () =>
    {
        await CommitAsync("Alice", "a.txt", "alice のコミット");
        await CommitAsync("Bob", "b.txt", "bob のコミット");
        await _history.ReloadAsync();
        Assert.Contains("Bob", _history.AuthorOptions);

        // a.txt を触ったのは Alice だけ
        await _history.ShowPathAsync(_root, Path.Combine(_root, "a.txt"));

        Assert.Equal(new[] { GitHistoryViewModel.AllAuthorsLabel, "Alice" }, _history.AuthorOptions);
    });

    [Fact]
    public void ファイル履歴はリネームをまたいで続く() => RunOnDispatcher(async () =>
    {
        await CommitAsync("Alice", "old.txt", "作成");
        await MustRunAsync("mv", "old.txt", "new.txt");
        await MustRunAsync("commit", "-m", "改名");

        await _history.ShowPathAsync(_root, Path.Combine(_root, "new.txt"));

        Assert.Equal(new[] { "改名", "作成" }, Subjects());
        Assert.True(_history.IsFileScoped);
        Assert.Equal("new.txt", _history.ScopedPath);
    });

    [Fact]
    public void フォルダーの履歴はリネーム追跡を使わない() => RunOnDispatcher(async () =>
    {
        // --follow は pathspec 1件のファイル向け。フォルダーに付けると git が fatal で終わり、
        // 履歴が丸ごと空になる（サービス側の再試行が効いていることの確認も兼ねる）
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        await CommitAsync("Alice", Path.Combine("docs", "a.md"), "文書を追加");

        await _history.ShowPathAsync(_root, Path.Combine(_root, "docs"));

        Assert.False(_history.IsFileScoped);
        Assert.Equal(new[] { "文書を追加" }, Subjects());
    });

    [Fact]
    public void コミットへ手繰るときは絞り込みを外す() => RunOnDispatcher(async () =>
    {
        var target = await CommitAsync("Alice", "a.txt", "目的のコミット");
        await CommitAsync("Alice", "b.txt", "別のコミット");
        await _history.ReloadAsync();

        _history.LogFilter = "別の";
        await _history.ReloadAsync();
        Assert.Equal(new[] { "別のコミット" }, Subjects());

        await _history.SelectCommitAsync(target);

        // 絞り込みが残っていると git 側が篩ってしまい、目的のコミットへ着地できない
        Assert.Equal("", _history.LogFilter);
        Assert.Equal(target, _history.SelectedLogRow?.Hash);
    });

    // ===== 補助 =====

    /// <summary>
    /// ディスパッチャー付きの STA スレッドで非同期の本体を回す。<c>await</c> の続きが同じスレッドへ
    /// 戻るので、<c>ICollectionView</c> を持つ VM を実アプリと同じ条件で扱える。
    /// </summary>
    private void RunOnDispatcher(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    _history = new GitHistoryViewModel(new GitSessionQuery(_git));
                    await body();
                }
                catch (Exception exception) { error = exception; }
                finally { frame.Continue = false; }
            }));
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }

    private string[] Subjects() =>
        _history.LogView.Cast<GitLogRow>().Where(row => row.IsCommit).Select(row => row.Subject!).ToArray();

    private async Task<string> CommitAsync(string author, string relativePath, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, relativePath), message);
        await MustRunAsync("add", "-A");
        await MustRunAsync("config", "user.name", author);
        await MustRunAsync("commit", "-m", message);
        return (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
