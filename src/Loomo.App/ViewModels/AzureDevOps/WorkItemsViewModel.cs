using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>Work Item 一覧の1行。</summary>
public sealed class WorkItemRowViewModel(WorkItemTreeRow row)
{
    public int Id => row.Item.Id;
    public string IdText => $"#{row.Item.Id}";
    public string Title => row.Item.Title;
    public string WorkItemType => row.Item.WorkItemType;
    public string State => row.Item.State;
    public string WebUrl => row.Item.WebUrl;
    public bool IsAssigned => row.IsAssigned;

    /// <summary>親子の字下げ。</summary>
    public System.Windows.Thickness Indent => new(row.Depth * 14, 0, 0, 0);

    /// <summary>文脈のためだけに出している親（自分の担当ではない）は薄く出す。</summary>
    public double Opacity => row.IsAssigned ? 1.0 : 0.6;

    public string ToolTip => row.IsAssigned
        ? $"{row.Item.WorkItemType} {IdText}: {row.Item.Title}\n{row.Item.State} ・ {row.Item.IterationPath}"
        : $"{row.Item.WorkItemType} {IdText}: {row.Item.Title}\n（自分の担当の親。自分には割り当たっていない）";

    /// <summary>種類の短い表記（一覧の幅を取らないように）。</summary>
    public string TypeShort => row.Item.WorkItemType switch
    {
        "User Story" => "Story",
        "Product Backlog Item" => "PBI",
        "Test Case" => "Test",
        var s when s.Length > 7 => s[..7],
        var s => s,
    };

    /// <summary>種類の色（Azure DevOps の配色に寄せる）。</summary>
    public string TypeColor => row.Item.WorkItemType switch
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
/// ActivityBar の Work Items（⌨ の上のアイコン）。自分に割り当たっている未完了の Work Item を、
/// Story の下に Task が並ぶ形で出す。認証は Git Credential Manager に任せる（<see cref="GitCredentialTokenProvider"/>）。
/// <para>組織は設定（<see cref="AzureDevOpsSettings.Organization"/>）が空なら、ワークスペースの各フォルダーの
/// git リモートから見つける——Azure Repos を開いていれば何も設定せずに出る。</para>
/// </summary>
public sealed partial class WorkItemsViewModel : ObservableObject
{
    /// <summary>開き直したときに取り直すまでの間隔。開くたびに取りに行くと、ちらっと覗くだけでも待たされる。</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private readonly IWorkspaceService _workspace;
    private readonly LoomoSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly GitCredentialTokenProvider _credentials;
    private readonly AzureDevOpsWorkItemClient _client;
    private CancellationTokenSource? _loading;
    private DateTime _loadedAt = DateTime.MinValue;

    public WorkItemsViewModel(IWorkspaceService workspace, LoomoSettings settings, SettingsStore settingsStore,
        GitCredentialTokenProvider credentials, AzureDevOpsWorkItemClient client)
    {
        _workspace = workspace;
        _settings = settings;
        _settingsStore = settingsStore;
        _credentials = credentials;
        _client = client;
        _organizationInput = settings.AzureDevOps.Organization;
        // 部屋を移ったら組織が変わり得るので、次に開いたとき取り直す。
        _workspace.FoldersChanged += (_, _) => _loadedAt = DateTime.MinValue;
    }

    public ObservableCollection<WorkItemRowViewModel> Items { get; } = new();

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

    /// <summary>行が選ばれた（ブラウザペインで開く）。ホストが購読する。</summary>
    public event Action<string, string>? OpenRequested;

    partial void OnIsOpenChanged(bool value)
    {
        if (value && !IsLoading && DateTime.UtcNow - _loadedAt > StaleAfter)
            _ = RefreshAsync();
    }

    [RelayCommand]
    private void ToggleOpen() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Open(WorkItemRowViewModel? row)
    {
        if (row is null) return;
        IsOpen = false;
        OpenRequested?.Invoke(row.WebUrl, $"{row.TypeShort} {row.IdText}");
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
            ErrorText = "組織名か、https://dev.azure.com/{組織} の形で入力してください。";
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
            var organization = await ResolveOrganizationAsync(token);
            if (token.IsCancellationRequested) return;
            if (organization is null)
            {
                OrganizationName = "";
                Items.Clear();
                StatusText = "";
                ErrorText = "ワークスペースに Azure Repos のリモートが見つかりません。組織を入力してください。";
                IsOrganizationEditorVisible = true;
                return;
            }
            OrganizationName = organization.Name;

            var credential = await _credentials.GetAsync(organization, token);
            if (token.IsCancellationRequested) return;
            if (!credential.Success)
            {
                StatusText = "";
                ErrorText = credential.Message;
                return;
            }

            var assigned = await _client.GetAssignedToMeAsync(organization, credential.Header!, token);
            // 自分の Task の親（Story など）が自分の担当でなくても、見出しとして取ってくる。
            var assignedIds = assigned.Select(i => i.Id).ToHashSet();
            var missingParents = assigned.Select(i => i.ParentId)
                .Where(id => id != 0 && !assignedIds.Contains(id)).Distinct().ToList();
            var parents = missingParents.Count > 0
                ? await _client.GetByIdsAsync(organization, missingParents, credential.Header!, token)
                : [];
            if (token.IsCancellationRequested) return;

            Items.Clear();
            foreach (var row in WorkItemTree.Arrange(assigned, parents))
                Items.Add(new WorkItemRowViewModel(row));
            StatusText = $"{assigned.Count} 件 ・ 更新 {DateTime.Now:HH:mm}";
            _loadedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (AzureDevOpsException ex) when (ex.IsAuthentication)
        {
            StatusText = "";
            ErrorText = ex.Message + " ターミナルで一度 git fetch してサインインし直すか、"
                + "git-credential-manager azure-repos bind で組織に使うアカウントを確かめてください。";
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

    /// <summary>設定にあればそれ。無ければワークスペースの各フォルダーの git リモートから最初に見つかった組織。</summary>
    private async Task<AzureDevOpsOrganization?> ResolveOrganizationAsync(CancellationToken token)
    {
        if (AzureDevOpsOrganization.TryParseUserInput(_settings.AzureDevOps.Organization, out var configured))
            return configured;
        foreach (var folder in _workspace.Folders.ToList())
        {
            foreach (var url in await GitCredentialTokenProvider.GetRemoteUrlsAsync(folder, token))
                if (AzureDevOpsOrganization.TryParseRemote(url, out var found))
                    return found;
        }
        return null;
    }
}
