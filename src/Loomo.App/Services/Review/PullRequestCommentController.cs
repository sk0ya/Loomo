using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Editor.Controls.Rendering;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// PR のレビューのコメントを、手元のファイルのその行に出す（§42）。どの PR かは<b>そのファイルが属するワークツリーの
/// ブランチ</b>で決める——<c>feature/x</c> のワークツリーのファイルには <c>feature/x</c> の PR のコメント、<c>pr/123</c> の
/// ワークツリーには PR 123 のコメント、main の本体には何も出ない。同じリポジトリのワークツリーを並べても取り違えない。
/// <para>Azure DevOps へは<b>読むだけ</b>（§24.27.1：共有の PAT で書き込む口は持たない）。印を押すとブラウザのペインがページの
/// そのスレッドへ移り、返信・解決は人がページで行う。</para>
/// <para>行は PR の版のその行の中身で手元の行に付け直す（<see cref="ReviewLineAnchor"/>）。編集で行がずれるので、手が止まって
/// 少ししてから付け直す——打鍵ごとに本文を写さない（§31.15）。</para>
/// </summary>
internal sealed class PullRequestCommentController
{
    /// <summary>ワークツリーの PR を問い合わせ直すまでの時間（その間はファイルを開くたびに REST を投げない）。</summary>
    private static readonly TimeSpan PullRequestTtl = TimeSpan.FromMinutes(3);

    private readonly GitService _git;
    private readonly Func<CancellationToken, Task<(AzureDevOpsWorkItemClient Client, AuthenticationHeaderValue Auth)?>> _connect;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _openInBrowser;
    private readonly Dictionary<(string Root, string Branch), (DateTime At, Task<PullRequestComments?> Task)> _pullRequests = new();
    private readonly Dictionary<(string Commit, string Path), Task<IReadOnlyList<string>?>> _prVersions = new();
    private readonly Dictionary<string, Task<BitmapSource?>> _images = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<VimEditorControl, EditorState> _states = new();

    internal PullRequestCommentController(
        GitService git,
        Func<CancellationToken, Task<(AzureDevOpsWorkItemClient Client, AuthenticationHeaderValue Auth)?>> connect,
        Dispatcher dispatcher,
        Action<string> openInBrowser)
    {
        _git = git;
        _connect = connect;
        _dispatcher = dispatcher;
        _openInBrowser = openInBrowser;
    }

    /// <summary>そのワークツリーの PR と、そのスレッド。</summary>
    private sealed record PullRequestComments(
        AzureDevOpsRemoteTarget Remote, AzureDevOpsPullRequestDetail Detail, AzureDevOpsWorkItemClient Client, AuthenticationHeaderValue Auth);

    /// <summary>エディタ1枚ぶんの、いま出している印（行 → スレッド）。</summary>
    private sealed class EditorState
    {
        public string? Path;
        public PullRequestComments? Comments;
        public IReadOnlyList<AzureDevOpsThread> FileThreads = [];
        public string RelativePath = "";
        public Dictionary<int, List<(AzureDevOpsThread Thread, bool Exact)>> ByLine = new();
        public DispatcherTimer? Debounce;
        public int Generation;
    }

    internal void WireEditor(VimEditorControl control)
    {
        var state = _states.GetOrCreateValue(control);
        control.NoteMarkerClicked += line0 => OpenThreadInBrowser(control, line0);
        control.NoteToolTipContentProvider = line0 => BuildToolTip(control, line0);
        control.BufferChanged += (_, _) => ScheduleReanchor(control, state);
    }

    /// <summary>ファイルを読み込んだ（<c>AfterEditorFileLoaded</c>）。PR を引いて印を出す。</summary>
    internal void OnFileLoaded(VimEditorControl control) => _ = SyncAsync(control);

    private async Task SyncAsync(VimEditorControl control)
    {
        var state = _states.GetOrCreateValue(control);
        var generation = ++state.Generation;
        var path = control.FilePath;
        state.Path = path;
        state.Comments = null;
        state.FileThreads = [];
        state.ByLine.Clear();
        control.SetNoteMarkers([]);

        if (await Task.Run(() => GitWorkTreeHeads.Find(path)) is not { Branch: { } branch } head || path is null)
        {
            control.SetNoteMarkersEnabled(false);
            return;
        }
        var comments = await PullRequestForAsync(head.Root, branch);
        if (generation != state.Generation)
            return;   // 待っている間に別のファイルを読み込んだ
        if (comments is null) {
            control.SetNoteMarkersEnabled(false);
            return;
        }
        var relative = "/" + Path.GetRelativePath(head.Root, path).Replace('\\', '/');
        var threads = comments.Detail.Threads
            .Where(t => t.FilePath is { } file && t.Line is > 0 && string.Equals(file, relative, StringComparison.OrdinalIgnoreCase))
            .ToList();
        state.Comments = comments;
        state.FileThreads = threads;
        state.RelativePath = relative;
        control.SetNoteMarkersEnabled(threads.Count > 0);
        if (threads.Count > 0)
            await ReanchorAsync(control, state, head.Root, generation);
    }

    /// <summary>ワークツリーのブランチの、進行中の PR（とスレッド）。少しの間は覚えておく。</summary>
    private Task<PullRequestComments?> PullRequestForAsync(string root, string branch)
    {
        var key = (root.ToLowerInvariant(), branch);
        if (_pullRequests.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < PullRequestTtl)
            return cached.Task;
        var task = LoadPullRequestAsync(root, branch);
        _pullRequests[key] = (DateTime.UtcNow, task);
        return task;
    }

    private async Task<PullRequestComments?> LoadPullRequestAsync(string root, string branch)
    {
        try
        {
            var remotes = await _git.For(root).GetRemoteUrlsAsync();
            var remote = remotes.OrderBy(r => string.Equals(r.Name, "origin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Select(r => AzureDevOpsRemote.TryParse(r.Url)).FirstOrDefault(t => t is not null);
            if (remote is null)
                return null;   // Azure DevOps のリポジトリではない
            if (await _connect(CancellationToken.None) is not { } connection)
                return null;
            var organizationUrl = AzureDevOpsWorkItemClient.NormalizeOrganizationUrl(remote.OrganizationUrl);
            var pr = await connection.Client.FindActivePullRequestAsync(organizationUrl, remote.PrTarget, branch, connection.Auth, CancellationToken.None);
            if (pr is null)
                return null;
            var detail = await connection.Client.GetPullRequestDetailAsync(organizationUrl, remote.PrTarget, pr.Id, connection.Auth, CancellationToken.None);
            return new PullRequestComments(remote with { OrganizationUrl = organizationUrl }, detail, connection.Client, connection.Auth);
        }
        catch (Exception ex) when (ex is AzureDevOpsException or HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return null;   // 繋がらない・PAT が無い：印を出さないだけ（ファイルを開く邪魔をしない）
        }
    }

    private void ScheduleReanchor(VimEditorControl control, EditorState state)
    {
        if (state.FileThreads.Count == 0)
            return;
        if (state.Debounce is null)
        {
            state.Debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(1000) };
            state.Debounce.Tick += (_, _) =>
            {
                state.Debounce.Stop();
                if (GitWorkTreeHeads.Find(state.Path) is { } head)
                    _ = ReanchorAsync(control, state, head.Root, state.Generation);
            };
        }
        state.Debounce.Stop();
        state.Debounce.Start();
    }

    /// <summary>スレッドの行を手元の行へ付け直して印を出す。</summary>
    private async Task ReanchorAsync(VimEditorControl control, EditorState state, string root, int generation)
    {
        if (state.Comments is not { } comments || state.FileThreads.Count == 0)
            return;
        var prLines = await PrVersionAsync(root, comments.Detail.SourceCommit, state.RelativePath.TrimStart('/'));
        if (generation != state.Generation || !string.Equals(control.FilePath, state.Path, StringComparison.OrdinalIgnoreCase))
            return;
        var localLines = control.Text.Replace("\r\n", "\n").Split('\n');   // 手が止まってから・開いたときだけ
        var byLine = new Dictionary<int, List<(AzureDevOpsThread, bool)>>();
        foreach (var thread in state.FileThreads)
        {
            var match = ReviewLineAnchor.Map(thread.Line!.Value, prLines, localLines);
            var line0 = match.Line - 1;
            if (!byLine.TryGetValue(line0, out var list))
                byLine[line0] = list = [];
            list.Add((thread, match.Exact));
        }
        state.ByLine = byLine;
        control.SetNoteMarkers(byLine.Select(pair => new EditorNoteMarker(pair.Key,
            pair.Value.Any(t => t.Item1.IsOpen) ? NoteMarkerKind.Open : NoteMarkerKind.Resolved,
            pair.Value.Count,
            PlainSummary(pair.Value))).ToList());
    }

    /// <summary>PR の版のそのファイル（行の付け直しの基準）。手元にその版が無ければ null（行番号のまま付ける）。</summary>
    private Task<IReadOnlyList<string>?> PrVersionAsync(string root, string commit, string path)
    {
        if (commit.Length == 0)
            return Task.FromResult<IReadOnlyList<string>?>(null);
        var key = (commit, path.ToLowerInvariant());
        if (_prVersions.TryGetValue(key, out var cached))
            return cached;
        var task = ReadAsync();
        _prVersions[key] = task;
        return task;

        async Task<IReadOnlyList<string>?> ReadAsync()
        {
            var result = await _git.For(root).RunAsync("show", $"{commit}:{path}");
            return result.Success ? result.Output.Replace("\r\n", "\n").Split('\n') : null;
        }
    }

    // ── 押したとき・乗ったとき ────────────────────────────────────

    /// <summary>印を押した：ブラウザのペインでページのそのスレッドを開く（未解決のものを先に）。</summary>
    private void OpenThreadInBrowser(VimEditorControl control, int line0)
    {
        if (!_states.TryGetValue(control, out var state) || state.Comments is not { } comments
            || !state.ByLine.TryGetValue(line0, out var threads) || threads.Count == 0)
            return;
        var thread = threads.OrderBy(t => t.Thread.IsOpen ? 0 : 1).First().Thread;
        _openInBrowser(ThreadUrl(comments.Detail.Summary.WebUrl, thread));
    }

    /// <summary>PR のページで、そのスレッドを開いた形（ファイルの差分＋そのスレッド）。</summary>
    internal static string ThreadUrl(string pullRequestUrl, AzureDevOpsThread thread)
        => thread.FilePath is { } file
            ? $"{pullRequestUrl}?_a=files&path={Uri.EscapeDataString(file)}&discussionId={thread.Id}"
            : $"{pullRequestUrl}?discussionId={thread.Id}";

    private static string PlainSummary(IEnumerable<(AzureDevOpsThread Thread, bool Exact)> threads)
        => string.Join("\n\n", threads.Select(t =>
            $"{(t.Thread.IsOpen ? "未解決" : "解決済み")} — " + string.Join("\n", t.Thread.Comments.Select(c => $"{c.Author}: {c.Content}"))));

    /// <summary>乗ったときの中身：スレッドごとに、状態・会話（画像は縮めて並べる）・押したときのこと。</summary>
    private object? BuildToolTip(VimEditorControl control, int line0)
    {
        if (!_states.TryGetValue(control, out var state) || state.Comments is not { } comments
            || !state.ByLine.TryGetValue(line0, out var threads))
            return null;
        var panel = new StackPanel { MaxWidth = 520 };
        var first = true;
        foreach (var (thread, exact) in threads.OrderBy(t => t.Thread.IsOpen ? 0 : 1))
        {
            if (!first)
                panel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
            first = false;
            var status = thread.IsOpen ? "● 未解決" : "✓ 解決済み";
            panel.Children.Add(new TextBlock
            {
                Text = exact ? status : $"{status}（PR の版とは行がずれているかもしれません）",
                FontWeight = FontWeights.SemiBold,
                Foreground = thread.IsOpen ? Brushes.DodgerBlue : Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 4),
            });
            foreach (var comment in thread.Comments)
            {
                panel.Children.Add(new TextBlock { Text = comment.Author, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) });
                foreach (var part in ReviewCommentText.Split(comment.Content))
                {
                    if (part.ImageUrl is { } url)
                        panel.Children.Add(ImageFor(comments, url, part.Text));
                    else if (part.Text.Trim().Length > 0)
                        panel.Children.Add(new TextBlock { Text = part.Text.Trim(), TextWrapping = TextWrapping.Wrap });
                }
            }
        }
        panel.Children.Add(new TextBlock
        {
            Text = "押すと、ブラウザでこのスレッドを開きます（返信・解決はページで）",
            Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        });
        return panel;
    }

    /// <summary>コメントの画像。取れるまでは代わりの文字を出し、取れたら差し替える（ツールチップは開いたまま中身が変わる）。</summary>
    private UIElement ImageFor(PullRequestComments comments, string url, string alt)
    {
        var host = new ContentControl
        {
            Content = new TextBlock { Text = $"🖼 {(alt.Length > 0 ? alt : "画像")} を読み込んでいます…", Opacity = 0.6 },
            Margin = new Thickness(0, 4, 0, 4),
        };
        if (!_images.TryGetValue(url, out var task))
            _images[url] = task = LoadImageAsync(comments, url);
        _ = Fill();
        return host;

        async Task Fill()
        {
            var image = await task;
            host.Content = image is null
                ? new TextBlock { Text = $"🖼 {(alt.Length > 0 ? alt : "画像")}（読めませんでした）", Opacity = 0.6 }
                : new Image { Source = image, MaxWidth = 500, MaxHeight = 360, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        }
    }

    private static async Task<BitmapSource?> LoadImageAsync(PullRequestComments comments, string url)
    {
        try
        {
            var bytes = await comments.Client.GetAttachmentAsync(comments.Remote.OrganizationUrl, url, comments.Auth, CancellationToken.None);
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return (BitmapSource)bitmap;
            });
        }
        catch (Exception ex) when (ex is AzureDevOpsException or HttpRequestException or TaskCanceledException
                                       or NotSupportedException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
