using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// Work Items 一覧の1行（Work Item・PR・見出しのどれか）。種類の文字と ID は出さない——種類は左端の色で分かり、
/// ID はツールチップとコピーで足りる。そのぶんタイトルに幅を回す。
/// </summary>
public sealed class WorkItemListRowViewModel(WorkItemListEntry entry)
{
    /// <summary>PR の色（Azure DevOps の PR アイコンの青）。</summary>
    private const string PullRequestColor = "#0078D4";

    public WorkItemListEntry Entry => entry;
    public bool IsWorkItem => entry.Kind == WorkItemListEntryKind.WorkItem;
    public bool IsPullRequest => entry.Kind == WorkItemListEntryKind.PullRequest;
    public bool IsSection => entry.Kind == WorkItemListEntryKind.Section;
    public bool CanOpen => !IsSection;

    public string Title => entry.WorkItem?.Title ?? entry.PullRequest?.Title ?? entry.SectionTitle;

    /// <summary>右端の小さな文字：Work Item は状態、PR はリポジトリ（下書きならその旨）。</summary>
    public string Detail => entry.WorkItem?.State
        ?? (entry.PullRequest is { } pull ? (pull.IsDraft ? $"下書き ・ {pull.Repository}" : pull.Repository) : "");

    public string WebUrl => entry.WorkItem?.WebUrl ?? entry.PullRequest?.WebUrl ?? "";

    /// <summary>コピーする ID（番号だけ。貼る先で番号として使う）。</summary>
    public string IdText => entry.WorkItem?.Id.ToString() ?? entry.PullRequest?.Id.ToString() ?? "";

    /// <summary>人に見せる短い名前（ブラウザのタブ名・リンク文字）。</summary>
    public string Label => entry.WorkItem is { } item
        ? $"{ShortType(item.WorkItemType)} {item.Id}"
        : entry.PullRequest is { } pull ? $"PR {pull.Id}" : "";

    public string MarkdownLink => $"[{Label}: {Title}]({WebUrl})";
    public string HtmlLink => $"<a href=\"{WebUtility.HtmlEncode(WebUrl)}\">{WebUtility.HtmlEncode(Label)}</a>: {WebUtility.HtmlEncode(Title)}";

    public System.Windows.Thickness Indent => new(entry.Depth * 14, 0, 0, 0);

    /// <summary>自分の担当ではない親（文脈のために出している Story など）は薄く出す。</summary>
    public double Opacity => entry.IsAssigned ? 1.0 : 0.55;

    public string TypeColor => entry.Kind switch
    {
        WorkItemListEntryKind.PullRequest => PullRequestColor,
        WorkItemListEntryKind.WorkItem => ColorFor(entry.WorkItem!.WorkItemType),
        _ => "Transparent",
    };

    public string ToolTip => entry.Kind switch
    {
        WorkItemListEntryKind.WorkItem when entry.WorkItem is { } item =>
            $"{item.WorkItemType} {item.Id}: {item.Title}\n{item.State} ・ {item.IterationPath}"
            + (entry.IsAssigned ? "" : "\n（自分の担当の親。自分には割り当たっていない）"),
        WorkItemListEntryKind.PullRequest when entry.PullRequest is { } pull =>
            $"PR {pull.Id}: {pull.Title}\n{pull.Project} / {pull.Repository}" + (pull.IsDraft ? "（下書き）" : ""),
        _ => "",
    };

    private static string ShortType(string type) => type switch
    {
        "User Story" => "Story",
        "Product Backlog Item" => "PBI",
        _ => type,
    };

    /// <summary>種類の色（Azure DevOps の配色に寄せる）。</summary>
    internal static string ColorFor(string type) => type switch
    {
        "Bug" => "#CC293D",
        "User Story" or "Product Backlog Item" or "Requirement" => "#009CCC",
        "Task" => "#F2CB1D",
        "Feature" => "#773B93",
        "Epic" => "#FF7B00",
        "Issue" or "Impediment" => "#B4009E",
        "Test Case" => "#004B50",
        _ => "#888888",
    };
}

/// <summary>
/// ActivityBar の Work Items（⌨ の上のアイコン）。自分に割り当たっている未完了の Work Item を Story の下に
/// Task が並ぶ形で出し、自分が作った進行中の PR を紐づく Work Item の下に添える（TaskAzure と同じ並べ方）。
/// 認証は TaskAzure と同じ PAT（<see cref="AzureDevOpsPatStore"/>：環境変数 ADO_PAT → 資格情報マネージャー ADO_PAT）。
/// <para>組織は設定（<see cref="AzureDevOpsSettings.Organization"/>）が空なら、ワークスペースの各フォルダーの
/// git リモートから見つける。PR はプロジェクト単位で引く（Work Item のプロジェクト＋リモートのプロジェクト）。</para>
/// </summary>
public sealed partial class WorkItemsViewModel : ObservableObject
{
    /// <summary>コンボボックスの「絞らない」。null を項目にすると WPF の ComboBox は選べない値として扱うので文字で持つ。</summary>
    public const string AllOption = "すべて";

    /// <summary>開き直したときに取り直すまでの間隔。開くたびに取りに行くと、ちらっと覗くだけでも待たされる。</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly LoomoSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly AzureDevOpsPatStore _pats;
    private readonly AzureDevOpsOrganizationLocator _locator;
    private readonly AzureDevOpsWorkItemClient _client;
    private IReadOnlyList<WorkItemListEntry> _entries = [];
    private CancellationTokenSource? _loading;
    private DateTime _loadedAt = DateTime.MinValue;

    public WorkItemsViewModel(IWorkspaceService workspace, LoomoSettings settings, SettingsStore settingsStore,
        AzureDevOpsPatStore pats, AzureDevOpsOrganizationLocator locator, AzureDevOpsWorkItemClient client)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _pats = pats;
        _locator = locator;
        _client = client;
        _organizationInput = settings.AzureDevOps.Organization;
        // 部屋を移ったら組織が変わり得るので、次に開いたとき取り直す。
        workspace.FoldersChanged += (_, _) => _loadedAt = DateTime.MinValue;
    }

    /// <summary>いま見せている行（フィルター後）。</summary>
    public ObservableCollection<WorkItemListRowViewModel> Items { get; } = new();

    /// <summary>状態の選択肢（取れた Work Item に現れたものだけ）。</summary>
    public ObservableCollection<string> StateOptions { get; } = [AllOption];

    /// <summary>種類の選択肢。</summary>
    public ObservableCollection<string> TypeOptions { get; } = [AllOption];

    /// <summary>一覧（ポップアップ）を開いているか。</summary>
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty] private bool _isLoading;

    /// <summary>見出しに出す組織名。</summary>
    [ObservableProperty] private string _organizationName = "";

    /// <summary>件数や更新時刻などの一行。</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>失敗の理由（空なら出さない）。</summary>
    [ObservableProperty] private string _errorText = "";

    /// <summary>組織の入力欄を出すか（見つからなかったとき・人が切り替えたいとき）。</summary>
    [ObservableProperty] private bool _isOrganizationEditorVisible;

    [ObservableProperty] private string _organizationInput;

    /// <summary>文字で絞る（タイトル・状態・種類・ID・プロジェクト、PR はタイトル・リポジトリ・ID）。</summary>
    [ObservableProperty] private string _filterText = "";

    [ObservableProperty] private string _selectedState = AllOption;

    [ObservableProperty] private string _selectedType = AllOption;

    /// <summary>取れたが絞り込みで1件も残らない／そもそも0件のときの一言。</summary>
    [ObservableProperty] private string _emptyText = "";

    /// <summary>行が選ばれた（ブラウザペインで開く）。ホストが購読する。</summary>
    public event Action<string, string>? OpenRequested;

    partial void OnIsOpenChanged(bool value)
    {
        if (value && !IsLoading && DateTime.UtcNow - _loadedAt > StaleAfter)
            _ = RefreshAsync();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedStateChanged(string value) => ApplyFilter();
    partial void OnSelectedTypeChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ToggleOpen() => IsOpen = !IsOpen;

    /// <summary>絞り込みを外す。</summary>
    [RelayCommand]
    private void ClearFilter()
    {
        FilterText = "";
        SelectedState = AllOption;
        SelectedType = AllOption;
    }

    /// <summary>ブラウザペインで開く（既定の動き。部屋の中で読む）。</summary>
    [RelayCommand]
    private void Open(WorkItemListRowViewModel? row)
    {
        if (row is not { CanOpen: true }) return;
        IsOpen = false;
        OpenRequested?.Invoke(row.WebUrl, row.Label);
    }

    /// <summary>既定のブラウザで開く（サインイン済みの普段のブラウザで操作したいとき）。</summary>
    [RelayCommand]
    private void OpenExternal(WorkItemListRowViewModel? row)
    {
        if (row is not { CanOpen: true }) return;
        try { Process.Start(new ProcessStartInfo(row.WebUrl) { UseShellExecute = true }); }
        catch (Exception ex) { ToastService.Error($"ブラウザで開けませんでした: {ex.Message}"); }
    }

    [RelayCommand]
    private void CopyId(WorkItemListRowViewModel? row) => ClipboardText.Set(row?.IdText);

    [RelayCommand]
    private void CopyTitle(WorkItemListRowViewModel? row) => ClipboardText.Set(row?.Title);

    [RelayCommand]
    private void CopyUrl(WorkItemListRowViewModel? row) => ClipboardText.Set(row?.WebUrl);

    /// <summary>リンクをコピー：貼る先がリッチテキストならリンク、プレーンテキストなら Markdown（TaskAzure と同じ）。</summary>
    [RelayCommand]
    private void CopyLink(WorkItemListRowViewModel? row)
    {
        if (row is not { CanOpen: true }) return;
        ClipboardText.SetLink(row.MarkdownLink, row.HtmlLink);
    }

    [RelayCommand]
    private void EditOrganization()
    {
        OrganizationInput = _settings.AzureDevOps.Organization.Length > 0
            ? _settings.AzureDevOps.Organization
            : OrganizationName;
        IsOrganizationEditorVisible = !IsOrganizationEditorVisible;
    }

    /// <summary>入力された組織を保存して取り直す。空で保存すると「リモートから見つける」へ戻る。</summary>
    [RelayCommand]
    private async Task SaveOrganizationAsync()
    {
        var text = OrganizationInput?.Trim() ?? "";
        if (text.Length > 0 && !AzureDevOpsOrganization.TryParseUserInput(text, out _))
        {
            ErrorText = "組織名か、組織の URL（https://dev.azure.com/{組織}・https://{サーバー}/tfs/{コレクション}）を入力してください。";
            return;
        }
        _settings.AzureDevOps.Organization = text;
        try { _settingsStore.Save(_settings); }
        catch { /* 保存に失敗しても、この起動中は効かせる */ }
        IsOrganizationEditorVisible = false;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        var token = cts.Token;
        IsLoading = true;
        ErrorText = "";
        StatusText = "読み込み中…";
        try
        {
            var remotes = AzureDevOpsOrganization.TryParseUserInput(_settings.AzureDevOps.Organization, out var configured)
                ? new AzureDevOpsWorkspaceRemotes(configured, [])
                : await _locator.FindAsync(token);
            if (token.IsCancellationRequested) return;
            if (remotes.Organization is not { } organization)
            {
                OrganizationName = "";
                SetEntries([]);
                StatusText = "";
                ErrorText = "ワークスペースに Azure DevOps のリモートが見つかりません。組織の URL を入力してください。";
                IsOrganizationEditorVisible = true;
                return;
            }
            OrganizationName = organization.Name;

            var pat = _pats.Get();
            if (pat is null)
            {
                StatusText = "";
                ErrorText = $"PAT が見つかりません。Windows 資格情報マネージャーの {AzureDevOpsPatStore.CredentialTarget}"
                    + $"（TaskAzure と同じ）か、環境変数 {AzureDevOpsPatStore.EnvironmentVariable} に設定してください。";
                return;
            }
            var auth = AzureDevOpsPatStore.CreateHeader(pat);

            var assignedTask = _client.GetAssignedToMeAsync(organization, auth, token);
            var userTask = _client.GetCurrentUserIdAsync(organization, auth, token);
            var assigned = await assignedTask;
            // 自分の Task の親（Story など）が自分の担当でなくても、見出しとして取ってくる。
            var assignedIds = assigned.Select(i => i.Id).ToHashSet();
            var missingParents = assigned.Select(i => i.ParentId)
                .Where(id => id != 0 && !assignedIds.Contains(id)).Distinct().ToList();
            var parentsTask = missingParents.Count > 0
                ? _client.GetByIdsAsync(organization, missingParents, auth, token)
                : Task.FromResult<IReadOnlyList<AzureDevOpsWorkItem>>([]);
            var pullsTask = LoadPullRequestsAsync(organization, auth, await userTask,
                assigned.Select(i => i.Project).Concat(remotes.Projects), token);
            var parents = await parentsTask;
            var (pulls, pullError) = await pullsTask;
            if (token.IsCancellationRequested) return;

            _loadedAt = DateTime.UtcNow;
            SetEntries(WorkItemList.Build(WorkItemTree.Arrange(assigned, parents), pulls));
            StatusText = $"Work Item {assigned.Count} 件 ・ PR {pulls.Count} 件 ・ 更新 {DateTime.Now:HH:mm}";
            if (pullError is not null)
                ErrorText = pullError;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (AzureDevOpsException ex) when (ex.IsAuthentication)
        {
            StatusText = "";
            ErrorText = ex.Message + $" 資格情報マネージャーの {AzureDevOpsPatStore.CredentialTarget} の PAT"
                + "（期限切れ・Work Items 読み取り権限）と組織 URL を確かめてください。";
        }
        catch (Exception ex) when (ex is AzureDevOpsException or System.Net.Http.HttpRequestException or TaskCanceledException
                                       or System.Text.Json.JsonException)
        {
            StatusText = "";
            ErrorText = ex is TaskCanceledException ? "Azure DevOps への接続がタイムアウトしました。" : ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loading, cts))
                IsLoading = false;
        }
    }

    /// <summary>プロジェクトごとに自分の進行中の PR を引く。PR が取れなくても Work Item は出したいので、
    /// 失敗は例外にせず一言にして返す（PAT に Code の読み取り権限が無い、など）。</summary>
    private async Task<(IReadOnlyList<AzureDevOpsPullRequest> Pulls, string? Error)> LoadPullRequestsAsync(
        AzureDevOpsOrganization organization, System.Net.Http.Headers.AuthenticationHeaderValue auth, string userId,
        IEnumerable<string> projects, CancellationToken token)
    {
        var names = projects.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = await Task.WhenAll(names.Select(async project =>
        {
            try
            {
                return (Pulls: await _client.GetMyActivePullRequestsAsync(organization, project, userId, auth, token),
                    Error: (string?)null);
            }
            catch (AzureDevOpsException ex)
            {
                return (Pulls: (IReadOnlyList<AzureDevOpsPullRequest>)[],
                    Error: (string?)$"PR を取得できませんでした（{project}）: {ex.Message}");
            }
        }));
        return (results.SelectMany(r => r.Pulls).ToList(), results.Select(r => r.Error).FirstOrDefault(e => e is not null));
    }

    private void SetEntries(IReadOnlyList<WorkItemListEntry> entries)
    {
        _entries = entries;
        var items = entries.Where(e => e.WorkItem is not null).Select(e => e.WorkItem!).ToList();
        ResetOptions(StateOptions, items.Select(i => i.State), SelectedState, v => SelectedState = v);
        ResetOptions(TypeOptions, items.Select(i => i.WorkItemType), SelectedType, v => SelectedType = v);
        ApplyFilter();
    }

    /// <summary>選択肢を取れた値で作り直す。選んでいた値が消えたら「すべて」へ戻す。</summary>
    private static void ResetOptions(ObservableCollection<string> options, IEnumerable<string> values,
        string selected, Action<string> select)
    {
        var distinct = values.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.CurrentCulture).ToList();
        if (options.Skip(1).SequenceEqual(distinct))
            return;
        options.Clear();
        options.Add(AllOption);
        foreach (var value in distinct) options.Add(value);
        select(distinct.Contains(selected) ? selected : AllOption);
    }

    private void ApplyFilter()
    {
        var filter = new WorkItemListFilter(FilterText,
            SelectedState == AllOption ? null : SelectedState,
            SelectedType == AllOption ? null : SelectedType);
        var visible = WorkItemList.Visible(_entries, filter);
        Items.Clear();
        for (var i = 0; i < _entries.Count; i++)
            if (visible[i])
                Items.Add(new WorkItemListRowViewModel(_entries[i]));
        EmptyText = Items.Count > 0 ? ""
            : _entries.Count > 0 ? "条件に合うものはありません。"
            : _loadedAt != DateTime.MinValue ? "割り当たっている Work Item も進行中の PR もありません。" : "";
    }
}
