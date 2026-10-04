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
public sealed class WorkItemListRowViewModel(WorkItemListEntry entry, bool hasChildren = false, bool isCollapsed = false)
{
    /// <summary>PR の色（Azure DevOps の PR アイコンの青）。</summary>
    private const string PullRequestColor = "#0078D4";

    public WorkItemListEntry Entry => entry;
    public bool IsWorkItem => entry.Kind == WorkItemListEntryKind.WorkItem;
    public bool IsPullRequest => entry.Kind == WorkItemListEntryKind.PullRequest;
    public bool IsSection => entry.Kind == WorkItemListEntryKind.Section;
    public bool CanOpen => !IsSection;

    /// <summary>見えている子を持つ（▸／▾ を出して畳める）。</summary>
    public bool HasChildren => hasChildren;

    public bool IsCollapsed => isCollapsed;

    public string Chevron => !hasChildren ? "" : isCollapsed ? "▸" : "▾";

    public string ChevronToolTip => isCollapsed ? "展開" : "折りたたむ";

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

/// <summary>状態・種類の絞り込みの選択肢1つ（☑ で複数選ぶ）。</summary>
public sealed partial class WorkItemFilterOption(string value, bool isChecked, Action changed) : ObservableObject
{
    public string Value => value;

    [ObservableProperty] private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// ActivityBar の Work Items（⌨ の上のアイコン）。<b>TaskAzure と同じ方式</b>：設定の組織 URL・プロジェクトで
/// 自分に割り当たっている未完了の Work Item を引き、設定した PR 対象（プロジェクト/リポジトリ）から自分の進行中の PR を引いて
/// 紐づく Work Item の下に添える。PAT は環境変数 ADO_PAT → 資格情報マネージャー ADO_PAT を読むだけ（<see cref="AzureDevOpsPatStore"/>）。
/// </summary>
public sealed partial class WorkItemsViewModel : ObservableObject
{
    /// <summary>開き直したときに取り直すまでの間隔。開くたびに取りに行くと、ちらっと覗くだけでも待たされる。</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly LoomoSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly AzureDevOpsPatStore _pats;
    private readonly AzureDevOpsWorkItemClient _client;
    private IReadOnlyList<WorkItemListEntry> _entries = [];
    private CancellationTokenSource? _loading;
    private DateTime _loadedAt = DateTime.MinValue;

    /// <summary>畳んでいる行の鍵（<see cref="WorkItemList.CollapseKey"/>）。更新で一覧を作り直しても畳んだままにする。</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    /// <summary>☑ をまとめて変えている最中（1つ変わるごとに絞り込み直さない）。</summary>
    private bool _batchingOptions;

    public WorkItemsViewModel(LoomoSettings settings, SettingsStore settingsStore,
        AzureDevOpsPatStore pats, AzureDevOpsWorkItemClient client)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _pats = pats;
        _client = client;
        LoadSettingsInputs();
    }

    /// <summary>いま見せている行（フィルター後）。</summary>
    public ObservableCollection<WorkItemListRowViewModel> Items { get; } = new();

    /// <summary>状態の選択肢（取れた Work Item に現れたものだけ）。1つも ☑ が無ければ絞らない。</summary>
    public ObservableCollection<WorkItemFilterOption> StateOptions { get; } = new();

    /// <summary>種類の選択肢。</summary>
    public ObservableCollection<WorkItemFilterOption> TypeOptions { get; } = new();

    /// <summary>状態の絞り込みボタンの文字（選んだものを並べる）。</summary>
    public string StateFilterLabel => FilterLabel("状態", StateOptions);

    public string TypeFilterLabel => FilterLabel("種類", TypeOptions);

    public bool IsStateFiltered => StateOptions.Any(o => o.IsChecked);

    public bool IsTypeFiltered => TypeOptions.Any(o => o.IsChecked);

    /// <summary>絞り込み（文字・状態・種類のどれか）が効いている。× を出す。</summary>
    public bool HasAnyFilter => FilterText.Length > 0 || IsStateFiltered || IsTypeFiltered;

    [ObservableProperty] private bool _isStateFilterOpen;

    [ObservableProperty] private bool _isTypeFilterOpen;

    /// <summary>一覧（ポップアップ）を開いているか。</summary>
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty] private bool _isLoading;

    /// <summary>見出しに出す組織名。</summary>
    [ObservableProperty] private string _organizationName = "";

    /// <summary>件数や更新時刻などの一行。</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>失敗の理由（空なら出さない）。</summary>
    [ObservableProperty] private string _errorText = "";

    /// <summary>設定欄（組織 URL・プロジェクト・PR 対象＝TaskAzure の設定と同じ項目）を出すか。</summary>
    [ObservableProperty] private bool _isSettingsVisible;

    [ObservableProperty] private string _organizationUrlInput = "";

    [ObservableProperty] private string _projectInput = "";

    /// <summary>PR 対象。1行に「プロジェクト/リポジトリ」。</summary>
    [ObservableProperty] private string _prTargetsInput = "";

    /// <summary>文字で絞る（タイトル・状態・種類・ID・プロジェクト、PR はタイトル・リポジトリ・ID）。</summary>
    [ObservableProperty] private string _filterText = "";

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

    [RelayCommand]
    private void ToggleOpen() => IsOpen = !IsOpen;

    /// <summary>絞り込みを外す。</summary>
    [RelayCommand]
    private void ClearFilter()
    {
        _batchingOptions = true;
        try
        {
            foreach (var option in StateOptions.Concat(TypeOptions)) option.IsChecked = false;
        }
        finally { _batchingOptions = false; }
        FilterText = "";
        ApplyFilter();
    }

    /// <summary>1行の開閉（▸／▾）。</summary>
    [RelayCommand]
    private void ToggleCollapse(WorkItemListRowViewModel? row)
    {
        if (row is not { HasChildren: true } || WorkItemList.CollapseKey(row.Entry) is not { } key) return;
        if (!_collapsed.Remove(key)) _collapsed.Add(key);
        ApplyFilter();
    }

    /// <summary>すべて折りたたむ：子を持つ行をすべて畳む（いちばん上の段だけが並ぶ）。</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        var hasChildren = WorkItemList.HasVisibleChildren(_entries, Enumerable.Repeat(true, _entries.Count).ToArray());
        for (var i = 0; i < _entries.Count; i++)
            if (hasChildren[i] && WorkItemList.CollapseKey(_entries[i]) is { } key)
                _collapsed.Add(key);
        ApplyFilter();
    }

    /// <summary>すべて展開。</summary>
    [RelayCommand]
    private void ExpandAll()
    {
        _collapsed.Clear();
        ApplyFilter();
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
    private void ToggleSettings()
    {
        if (!IsSettingsVisible) LoadSettingsInputs();
        IsSettingsVisible = !IsSettingsVisible;
    }

    private void LoadSettingsInputs()
    {
        var a = _settings.AzureDevOps;
        OrganizationUrlInput = a.OrganizationUrl;
        ProjectInput = a.Project;
        PrTargetsInput = string.Join(Environment.NewLine, a.PrTargets.Select(t => $"{t.Project}/{t.Repository}"));
    }

    /// <summary>設定を保存して取り直す。</summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var a = _settings.AzureDevOps;
        a.OrganizationUrl = (OrganizationUrlInput ?? "").Trim().TrimEnd('/');
        a.Project = (ProjectInput ?? "").Trim();
        a.PrTargets.Clear();
        foreach (var line in (PrTargetsInput ?? "").Split('\n'))
        {
            var slash = line.IndexOf('/');
            if (slash <= 0) continue;
            var project = line[..slash].Trim();
            var repository = line[(slash + 1)..].Trim();
            if (project.Length > 0 && repository.Length > 0)
                a.PrTargets.Add(new AzureDevOpsPrTargetSettings { Project = project, Repository = repository });
        }
        try { _settingsStore.Save(_settings); }
        catch { /* 保存に失敗しても、この起動中は効かせる */ }
        IsSettingsVisible = false;
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
            var settings = _settings.AzureDevOps;
            var organizationUrl = settings.OrganizationUrl.Trim().TrimEnd('/');
            var project = settings.Project.Trim();
            if (organizationUrl.Length == 0 || project.Length == 0)
            {
                OrganizationName = "";
                SetEntries([]);
                StatusText = "";
                ErrorText = "組織 URL とプロジェクトを設定してください（TaskAzure の設定と同じ値）。";
                LoadSettingsInputs();
                IsSettingsVisible = true;
                return;
            }
            OrganizationName = project;

            var pat = _pats.Get();
            if (pat is null)
            {
                StatusText = "";
                ErrorText = $"PAT が取得できませんでした。環境変数 {AzureDevOpsPatStore.EnvironmentVariable} または"
                    + $" Windows 資格情報マネージャー（{AzureDevOpsPatStore.CredentialTarget}）に PAT を設定してください。";
                return;
            }
            var auth = AzureDevOpsPatStore.CreateHeader(pat);
            var targets = settings.PrTargets.Select(t => new AzureDevOpsPrTarget(t.Project, t.Repository)).ToList();

            // TaskAzure と同じく Work Item と PR を並べて引く。PR が取れなくても Work Item は出す。
            var itemsTask = _client.GetMyWorkItemsAsync(organizationUrl, project, auth, token);
            var prsTask = LoadPullRequestsAsync(organizationUrl, targets, auth, token);
            var items = await itemsTask;
            // 自分の Task の親（Story など）が自分の担当でなくても、見出しとして取ってくる。
            var itemIds = items.Select(i => i.Id).ToHashSet();
            var missingParents = items.Select(i => i.ParentId)
                .Where(id => id != 0 && !itemIds.Contains(id)).Distinct().ToList();
            IReadOnlyList<AzureDevOpsWorkItem> parents = [];
            if (missingParents.Count > 0)
            {
                try { parents = await _client.GetWorkItemsByIdsAsync(organizationUrl, project, missingParents, auth, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* 親が取れなくても一覧は出す */ }
            }
            var (prs, prError) = await prsTask;
            if (token.IsCancellationRequested) return;

            _loadedAt = DateTime.UtcNow;
            SetEntries(WorkItemList.Build(WorkItemTree.Arrange(items, parents), prs));
            var prPart = targets.Count > 0 ? $" ・ PR {prs.Count} 件" : "";
            StatusText = $"{items.Count} 件{prPart} ・ 更新 {DateTime.Now:HH:mm}";
            if (prError is not null)
                ErrorText = prError;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // どんな失敗でもアプリは落とさない（⟳・F5 の非同期コマンドから例外が漏れると落ちる）。
            StatusText = "";
            ErrorText = ex is TaskCanceledException ? "Azure DevOps への接続がタイムアウトしました。" : ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loading, cts))
                IsLoading = false;
        }
    }

    /// <summary>PR を引く（TaskAzure と同じ：設定した PR 対象ごと）。失敗しても Work Item は出したいので、例外にせず一言にして返す。</summary>
    private async Task<(IReadOnlyList<AzureDevOpsPullRequest> Prs, string? Error)> LoadPullRequestsAsync(
        string organizationUrl, IReadOnlyList<AzureDevOpsPrTarget> targets,
        System.Net.Http.Headers.AuthenticationHeaderValue auth, CancellationToken token)
    {
        try
        {
            return (await _client.GetMyPullRequestsAsync(organizationUrl, targets, auth, token), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], $"PR を取得できませんでした: {ex.Message}");
        }
    }

    private void SetEntries(IReadOnlyList<WorkItemListEntry> entries)
    {
        _entries = entries;
        var items = entries.Where(e => e.WorkItem is not null).Select(e => e.WorkItem!).ToList();
        ResetOptions(StateOptions, items.Select(i => i.State));
        ResetOptions(TypeOptions, items.Select(i => i.WorkItemType));
        ApplyFilter();
    }

    /// <summary>選択肢を取れた値で作り直す。☑ は値で引き継ぎ、消えた値の ☑ は捨てる（見えない条件で全部消えないように）。</summary>
    private void ResetOptions(ObservableCollection<WorkItemFilterOption> options, IEnumerable<string> values)
    {
        var distinct = values.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.CurrentCulture).ToList();
        if (options.Select(o => o.Value).SequenceEqual(distinct))
            return;
        var checkedValues = options.Where(o => o.IsChecked).Select(o => o.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        options.Clear();
        foreach (var value in distinct)
            options.Add(new WorkItemFilterOption(value, checkedValues.Contains(value), OnFilterOptionChanged));
    }

    private void OnFilterOptionChanged()
    {
        if (!_batchingOptions) ApplyFilter();
    }

    private static string FilterLabel(string name, IEnumerable<WorkItemFilterOption> options)
    {
        var selected = options.Where(o => o.IsChecked).Select(o => o.Value).ToList();
        return selected.Count == 0 ? name : $"{name}: {string.Join(", ", selected)}";
    }

    private void ApplyFilter()
    {
        var filter = new WorkItemListFilter(FilterText,
            StateOptions.Where(o => o.IsChecked).Select(o => o.Value).ToList(),
            TypeOptions.Where(o => o.IsChecked).Select(o => o.Value).ToList());
        var visible = WorkItemList.Visible(_entries, filter);
        var hasChildren = WorkItemList.HasVisibleChildren(_entries, visible);
        // 文字で探している間は畳みを無視する——畳んだ中に合う行があっても見つからない、を避ける。
        var searching = FilterText.Trim().Length > 0;
        bool IsCollapsed(int i) => !searching && WorkItemList.CollapseKey(_entries[i]) is { } key && _collapsed.Contains(key);
        var shown = WorkItemList.Collapse(_entries, visible, IsCollapsed);
        Items.Clear();
        for (var i = 0; i < _entries.Count; i++)
            if (shown[i])
                Items.Add(new WorkItemListRowViewModel(_entries[i], hasChildren[i], hasChildren[i] && IsCollapsed(i)));
        OnPropertyChanged(nameof(StateFilterLabel));
        OnPropertyChanged(nameof(TypeFilterLabel));
        OnPropertyChanged(nameof(IsStateFiltered));
        OnPropertyChanged(nameof(IsTypeFiltered));
        OnPropertyChanged(nameof(HasAnyFilter));
        EmptyText = Items.Count > 0 ? ""
            : _entries.Count > 0 ? "条件に合うものはありません。"
            : _loadedAt != DateTime.MinValue ? "割り当たっている Work Item も進行中の PR もありません。" : "";
    }
}
