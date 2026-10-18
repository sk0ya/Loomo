namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: Azure DevOps（Azure Repos）の PR を部屋の中で読む（§24.27.1）。GitHub（§24.27）と同じ形——
/// 一覧から選ぶと、その PR でできること（概要と会話を読む・分岐点からの差分・ワークツリーで開く・ブラウザ）を並べる。
/// <para><b>読むだけ</b>。承認（vote）・コメントは持たない——共有の PAT で Azure DevOps へ書き込む口になるので、
/// それはブラウザで行う。接続（設定・PAT の候補）は Work Items ペインと同じもの（<see cref="WorkItemsViewModel.ConnectAsync"/>）。</para>
/// </summary>
public partial class ShellWindow
{
    private const string AzureCategory = "Azure DevOps";

    private void InitializeAzurePullRequests()
    {
        if (_vm.WorkItems is { } workItems)
            workItems.PullRequestActionsRequested += pull => _ = OpenAzurePullRequestActionsAsync(pull);
    }

    /// <summary>パレット「Azure DevOps: プルリクエスト」：自分の PR とレビューを頼まれている PR。</summary>
    private async void OpenAzurePullRequests()
    {
        if (_vm.WorkItems is not { } workItems) return;
        IReadOnlyList<PaletteCommand> loading = [new PaletteCommand(AzureCategory, "プルリクエストを読み込んでいます…", () => { })];
        OpenCommandPalette(PaletteMode.Command, loading);
        List<PaletteCommand> items;
        try
        {
            var connection = await workItems.ConnectAsync(CancellationToken.None);
            var client = workItems.Client;
            var mine = client.GetMyPullRequestsAsync(connection.OrganizationUrl, connection.Targets, connection.Auth, CancellationToken.None);
            var review = client.GetPullRequestsToReviewAsync(connection.OrganizationUrl, connection.Targets, connection.Auth, CancellationToken.None);
            var mineList = await mine;
            var mineKeys = mineList.Select(Key).ToHashSet();
            items = mineList.Select(pull => AzurePullRequestItem(pull, "自分"))
                .Concat((await review).Where(pull => !mineKeys.Contains(Key(pull))).Select(pull => AzurePullRequestItem(pull, "レビュー依頼")))
                .ToList();
            if (items.Count == 0)
                items.Add(new PaletteCommand(AzureCategory, "自分の PR・レビューを頼まれている PR はありません", () => { })
                {
                    Detail = string.Join(Environment.NewLine, connection.Targets.Select(t => $"{t.Project} / {t.Repository}")),
                });
        }
        catch (Exception ex) when (ex is AzureDevOpsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            items = [new PaletteCommand(AzureCategory, "プルリクエストを読めませんでした", () => { }) { Detail = ex.Message }];
        }
        if (IsPaletteOpen && ReferenceEquals(_paletteCommands, loading))
            OpenCommandPalette(PaletteMode.Command, items);

        static (string, string, int) Key(AzureDevOpsPullRequest pull) => (pull.Project, pull.Repository, pull.Id);
    }

    private PaletteCommand AzurePullRequestItem(AzureDevOpsPullRequest pull, string role)
    {
        var now = DateTimeOffset.Now;
        return new PaletteCommand(AzureCategory, $"PR {pull.Id} {pull.Title}", () => _ = OpenAzurePullRequestActionsAsync(pull))
        {
            Badge = pull.IsDraft ? $"{role} 下書き" : role,
            Detail = string.Join(Environment.NewLine, new[]
            {
                $"{pull.Project} / {pull.Repository}",
                pull.Author.Length > 0 ? $"作成者: {pull.Author}" : null,
                pull.SourceBranch.Length > 0 ? $"{pull.SourceBranch} → {pull.TargetBranch}" : null,
                pull.CreatedAt is { } at ? $"作成: {RelativeAge(now - at)}" : null,
                pull.LinkedWorkItemIds.Count > 0 ? $"Work Item: {string.Join(" ", pull.LinkedWorkItemIds.Select(id => $"AB#{id}"))}" : null,
            }.Where(line => line is not null)),
        };
    }

    private Task OpenAzurePullRequestActionsAsync(AzureDevOpsPullRequest pull)
    {
        var title = $"PR {pull.Id} {pull.Title}";
        PaletteCommand Act(string label, Action run, string? detail = null)
            => new(AzureCategory, label, run) { Detail = detail ?? title };
        OpenCommandPalette(PaletteMode.Command, [
            Act("概要と会話を読む（エディタ）", () => _ = OpenAzurePullRequestDocumentAsync(pull)),
            Act("差分を見る（分岐点から）", () => _ = OpenAzurePullRequestDiffAsync(pull),
                $"{title}\n{pull.TargetBranch} から分かれたあとの変更だけ（Azure Repos の「ファイル」と同じ）"),
            Act("ワークツリーで開く（別の部屋）", () => _ = OpenAzurePullRequestWorktreeAsync(pull),
                $"{title}\nブランチ pr/{pull.Id} を作ってワークツリーに置き、その部屋へ移る。今の作業ツリーには触らない"),
            Act("ブラウザで開く（承認・コメントはこちら）", () => _ = OpenUrlInBrowserAsync(pull.WebUrl, title), pull.WebUrl),
        ]);
        return Task.CompletedTask;
    }

    private async Task OpenAzurePullRequestDocumentAsync(AzureDevOpsPullRequest pull)
    {
        if (_vm.WorkItems is not { } workItems) return;
        try
        {
            var connection = await workItems.ConnectAsync(CancellationToken.None);
            var detail = await workItems.Client.GetPullRequestDetailAsync(connection.OrganizationUrl,
                new AzureDevOpsPrTarget(pull.Project, pull.Repository), pull.Id, connection.Auth, CancellationToken.None);
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
            await _editor.OpenDocumentAsync(new EditorDocument
            {
                FileName = $"PR-{pull.Id}.md",
                Content = AzureDevOpsPullRequestJson.Markdown(detail),
                OnSaved = _ => { },   // 読むための写し：保存しても Azure DevOps へは戻さない
            });
        }
        catch (Exception ex) when (ex is AzureDevOpsException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            ShowAzureError($"PR {pull.Id} を読めませんでした", ex.Message);
        }
    }

    private async Task OpenAzurePullRequestDiffAsync(AzureDevOpsPullRequest pull)
    {
        if (await FetchAzurePullRequestAsync(pull) is { } fetched)
            ShowPullRequestDiff(fetched, pull.TargetBranch, $"PR {pull.Id}");
    }

    private async Task OpenAzurePullRequestWorktreeAsync(AzureDevOpsPullRequest pull)
    {
        if (await FetchAzurePullRequestAsync(pull) is { } fetched)
            await OpenPullRequestWorktreeAsync($"pr/{pull.Id}", $"pr-{pull.Id}", fetched.HeadHash, ShowAzureError);
    }

    /// <summary>元のブランチと入れ先のブランチを、その PR のリポジトリを指すリモートから取る。Azure Repos の PR は
    /// 同じリポジトリのブランチ同士なので、<c>refs/heads/…</c> をそのまま取ればよい（一覧に元のブランチ名が無い
    /// 古い応答は、ブラウザへ誘う）。</summary>
    private async Task<FetchedPullRequest?> FetchAzurePullRequestAsync(AzureDevOpsPullRequest pull)
    {
        if (pull.SourceRefName.Length == 0 || pull.TargetRefName.Length == 0)
        {
            ShowAzureError($"PR {pull.Id} のブランチが分かりません", "ブラウザで開いて確かめてください。");
            return null;
        }
        var remote = _vm.GitSession.Remotes
            .OrderBy(r => string.Equals(r.Name, "origin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault(r => AzureDevOpsPullRequestJson.RemoteMatches(r.Url, pull.Project, pull.Repository));
        if (remote is null)
        {
            ShowAzureError($"PR {pull.Id} のリポジトリが見つかりません",
                $"いまの Git の対象に {pull.Project} / {pull.Repository} を指すリモートがありません。そのリポジトリのフォルダーを開いてください。");
            return null;
        }
        return await FetchPullRequestAsync(remote.Name, pull.SourceRefName, pull.TargetRefName, $"PR {pull.Id}", ShowAzureError);
    }

    private void ShowAzureError(string title, string reason)
        => MessageBox.Show(this, string.IsNullOrWhiteSpace(reason) ? title : $"{title}\n\n{reason.Trim()}",
            "Azure DevOps", MessageBoxButton.OK, MessageBoxImage.Warning);
}
