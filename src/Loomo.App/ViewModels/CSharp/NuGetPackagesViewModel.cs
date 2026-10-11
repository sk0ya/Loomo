using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.CSharp.Packages;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>NuGet 画面の見え方（Rider の Packages／Installed／Updates に当たる）。</summary>
public enum NuGetBrowseMode { Browse, Installed, Updates }

/// <summary>一覧の 1 行（検索結果か導入済みパッケージ）。</summary>
public sealed partial class NuGetPackageRow : ObservableObject
{
    public NuGetPackageRow(string id) => Id = id;

    public string Id { get; }
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _authors;
    /// <summary>行の右に出す状態（「導入済み 1.2」「更新 1.2 → 1.3」「版が不揃い」）。</summary>
    [ObservableProperty] private string? _badge;
    /// <summary>フィードから分かった全版（古い→新しい）。未取得なら空。</summary>
    public List<string> KnownVersions { get; } = new();
}

/// <summary>プロジェクト 1 行。選んだパッケージがこのプロジェクトに入っていれば <see cref="InstalledVersion"/>。</summary>
public sealed partial class NuGetProjectRow : ObservableObject
{
    public NuGetProjectRow(string name, string path) { Name = name; Path = path; }

    public string Name { get; }
    public string Path { get; }
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string? _installedVersion;

    public string InstalledLabel => InstalledVersion is { Length: > 0 } v ? v : "—";
    partial void OnInstalledVersionChanged(string? value) => OnPropertyChanged(nameof(InstalledLabel));
}

/// <summary>
/// NuGet パッケージ管理（設計書 §36）。検索して選び、チェックしたプロジェクトへ入れる／版を変える／外す。
/// 同じパッケージの版がプロジェクト間で不揃いなら「統合」で 1 つの版へ揃える。ファイルの書き換えは
/// すべて <see cref="NuGetPackageService"/>（＝<c>dotnet add|remove package</c>）に任せる。
/// </summary>
public sealed partial class NuGetPackagesViewModel : ObservableObject
{
    private readonly NuGetPackageService _service;
    private readonly ISolutionModelService? _solutionModel;
    /// <summary>一覧を取る相手。.sln があればそれ 1 回、無ければ（フォルダーだけのワークスペース）
    /// プロジェクトごと——<c>dotnet list</c> にフォルダーを渡すと、プロジェクトが複数あるだけで失敗する。</summary>
    private readonly IReadOnlyList<string> _listTargets;
    private readonly string? _workingDirectory;
    private IReadOnlyList<string> _sources = [];
    private IReadOnlyList<NuGetInstalledPackage> _installed = [];
    private CancellationTokenSource? _searchCts;

    public NuGetPackagesViewModel(
        NuGetPackageService service, ISolutionModelService? solutionModel, SolutionModel solution, string? focusProjectPath)
    {
        _service = service;
        _solutionModel = solutionModel;
        _workingDirectory = solution.RootDirectory;
        foreach (var project in solution.Projects.Where(p =>
                     p.FullPath.EndsWith("proj", StringComparison.OrdinalIgnoreCase)))
            Projects.Add(new NuGetProjectRow(project.Name, project.FullPath)
            {
                IsChecked = focusProjectPath is null ||
                            string.Equals(project.FullPath, focusProjectPath, StringComparison.OrdinalIgnoreCase),
            });
        _listTargets = solution.FullPath is { Length: > 0 } sln && File.Exists(sln)
            ? [sln]
            : Projects.Select(p => p.Path).ToList();
        Title = $"NuGet — {solution.Name}";
    }

    public string Title { get; }
    public ObservableCollection<NuGetProjectRow> Projects { get; } = new();
    public ObservableCollection<NuGetPackageRow> Packages { get; } = new();
    public ObservableCollection<string> Versions { get; } = new();

    [ObservableProperty] private NuGetBrowseMode _mode = NuGetBrowseMode.Installed;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _includePrerelease;
    [ObservableProperty] private NuGetPackageRow? _selectedPackage;
    [ObservableProperty] private string? _selectedVersion;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private bool _canConsolidate;

    public bool IsBrowse { get => Mode == NuGetBrowseMode.Browse; set { if (value) Mode = NuGetBrowseMode.Browse; } }
    public bool IsInstalled { get => Mode == NuGetBrowseMode.Installed; set { if (value) Mode = NuGetBrowseMode.Installed; } }
    public bool IsUpdates { get => Mode == NuGetBrowseMode.Updates; set { if (value) Mode = NuGetBrowseMode.Updates; } }

    /// <summary>画面を開いたときに 1 回。フィードの一覧と導入済みパッケージを読む。</summary>
    public async Task InitializeAsync()
    {
        await RunBusyAsync("読み込み中…", async () =>
        {
            _sources = await _service.GetSourcesAsync(_workingDirectory);
            await ReloadInstalledAsync();
        });
        await RefreshListAsync();
    }

    partial void OnModeChanged(NuGetBrowseMode value)
    {
        OnPropertyChanged(nameof(IsBrowse));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsUpdates));
        _ = RefreshListAsync();
    }

    partial void OnIncludePrereleaseChanged(bool value) => _ = RefreshListAsync();

    partial void OnSearchTextChanged(string value)
    {
        if (Mode == NuGetBrowseMode.Browse) _ = RefreshListAsync(debounce: true);
        else ApplyLocalFilter();
    }

    partial void OnSelectedPackageChanged(NuGetPackageRow? value) => _ = LoadSelectedAsync(value);

    // ── 一覧 ────────────────────────────────────────────────────────────────

    private async Task ReloadInstalledAsync()
    {
        var packages = new List<NuGetInstalledPackage>();
        foreach (var target in _listTargets)
        {
            var result = await _service.ListInstalledAsync(target);
            packages.AddRange(result.Packages);
            if (result.Problems.Count > 0)
                AppendLog(string.Join(Environment.NewLine, result.Problems));
        }
        _installed = packages;
    }

    private async Task RefreshListAsync(bool debounce = false)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            if (debounce) await Task.Delay(300, cts.Token);
            switch (Mode)
            {
                case NuGetBrowseMode.Browse:
                    if (string.IsNullOrWhiteSpace(SearchText)) { ReplacePackages([]); Status = "パッケージ名を入力して検索します。"; return; }
                    Status = "検索中…";
                    var results = await _service.SearchAsync(_sources, SearchText.Trim(), IncludePrerelease, cts.Token);
                    if (cts.IsCancellationRequested) return;
                    ReplacePackages(results.Select(r =>
                    {
                        var row = new NuGetPackageRow(r.Id) { Version = r.LatestVersion, Description = r.Description, Authors = r.Authors };
                        row.KnownVersions.AddRange(r.Versions);
                        row.Badge = InstalledBadge(r.Id);
                        return row;
                    }));
                    Status = $"{Packages.Count} 件";
                    break;
                case NuGetBrowseMode.Installed:
                    ReplacePackages(InstalledRows());
                    ApplyLocalFilter();
                    Status = $"導入済み {Packages.Count} 件";
                    break;
                case NuGetBrowseMode.Updates:
                    Status = "更新を確認中…";
                    var rows = InstalledRows().ToList();
                    var updates = new List<NuGetPackageRow>();
                    foreach (var row in rows)
                    {
                        var versions = await _service.GetVersionsAsync(_sources, row.Id, IncludePrerelease, cts.Token);
                        if (cts.IsCancellationRequested) return;
                        if (versions.Count == 0) continue;
                        row.KnownVersions.AddRange(versions);
                        var latest = versions[^1];
                        var lowest = InstallsOf(row.Id).Select(VersionOf).OrderBy(v => v, VersionComparer.Instance).First();
                        if (NuGetPackageService.CompareVersions(latest, lowest) > 0)
                        {
                            row.Badge = $"更新 {lowest} → {latest}";
                            row.Version = latest;
                            updates.Add(row);
                        }
                    }
                    ReplacePackages(updates);
                    Status = updates.Count == 0 ? "すべて最新です。" : $"更新あり {updates.Count} 件";
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"失敗しました: {ex.Message}";
        }
    }

    private IEnumerable<NuGetPackageRow> InstalledRows()
        => _installed.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var versions = g.Select(VersionOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new NuGetPackageRow(g.Key)
                {
                    Version = string.Join(" / ", versions),
                    Badge = NuGetPackageService.NeedsConsolidation(g) ? "版が不揃い" : $"{g.Count()} プロジェクト",
                };
            });

    private void ApplyLocalFilter()
    {
        if (Mode != NuGetBrowseMode.Installed) return;
        var query = SearchText.Trim();
        var rows = InstalledRows().Where(r => query.Length == 0 || r.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
        ReplacePackages(rows);
    }

    private void ReplacePackages(IEnumerable<NuGetPackageRow> rows)
    {
        var selectedId = SelectedPackage?.Id;
        Packages.Clear();
        foreach (var row in rows) Packages.Add(row);
        SelectedPackage = selectedId is null ? null
            : Packages.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    private string? InstalledBadge(string id)
    {
        var installs = InstallsOf(id).ToList();
        return installs.Count == 0 ? null : $"導入済み {string.Join(" / ", installs.Select(VersionOf).Distinct())}";
    }

    // ── 選択中のパッケージ ──────────────────────────────────────────────────

    private async Task LoadSelectedAsync(NuGetPackageRow? row)
    {
        foreach (var project in Projects)
            project.InstalledVersion = row is null ? null
                : InstallsOf(row.Id).Where(i => string.Equals(i.ProjectPath, project.Path, StringComparison.OrdinalIgnoreCase))
                    .Select(VersionOf).FirstOrDefault();
        CanConsolidate = row is not null && NuGetPackageService.NeedsConsolidation(InstallsOf(row.Id));
        Versions.Clear();
        SelectedVersion = null;
        if (row is null) return;

        if (row.KnownVersions.Count == 0)
        {
            try { row.KnownVersions.AddRange(await _service.GetVersionsAsync(_sources, row.Id, IncludePrerelease)); }
            catch (Exception ex) { Status = $"版の一覧を取得できません: {ex.Message}"; }
        }
        if (!ReferenceEquals(SelectedPackage, row)) return;
        foreach (var version in row.KnownVersions.Where(v => IncludePrerelease || !NuGetPackageService.IsPrerelease(v)).Reverse())
            Versions.Add(version);
        SelectedVersion = Versions.FirstOrDefault();
    }

    // ── 操作 ────────────────────────────────────────────────────────────────

    /// <summary>チェックしたプロジェクトへ、選んだ版で入れる（入っていれば版を変える）。</summary>
    [RelayCommand]
    private Task InstallAsync() => ApplyToCheckedAsync(remove: false);

    /// <summary>チェックしたプロジェクトから外す。</summary>
    [RelayCommand]
    private Task UninstallAsync() => ApplyToCheckedAsync(remove: true);

    /// <summary>このパッケージを参照している全プロジェクトを、選んだ版へ揃える。</summary>
    [RelayCommand]
    private async Task ConsolidateAsync()
    {
        if (SelectedPackage is not { } row || SelectedVersion is not { } version) return;
        var targets = InstallsOf(row.Id).Select(i => i.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await RunOperationsAsync(targets.Select(path => (path, row.Id, (string?)version, false)).ToList());
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunBusyAsync("読み込み中…", ReloadInstalledAsync);
        await RefreshListAsync();
    }

    private async Task ApplyToCheckedAsync(bool remove)
    {
        if (SelectedPackage is not { } row) return;
        var checkedProjects = Projects.Where(p => p.IsChecked).ToList();
        if (checkedProjects.Count == 0) { Status = "対象のプロジェクトにチェックを付けてください。"; return; }
        var operations = checkedProjects
            .Where(p => !remove || p.InstalledVersion is not null)
            .Select(p => (p.Path, row.Id, remove ? null : SelectedVersion, remove))
            .ToList();
        if (operations.Count == 0) { Status = "チェックしたプロジェクトにはこのパッケージが入っていません。"; return; }
        await RunOperationsAsync(operations);
    }

    private async Task RunOperationsAsync(IReadOnlyList<(string Project, string Id, string? Version, bool Remove)> operations)
    {
        await RunBusyAsync("実行中…", async () =>
        {
            var failures = 0;
            // 同じ Directory.Packages.props や obj を触るので、並べずに 1 つずつ。
            foreach (var (project, id, version, remove) in operations)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(project);
                AppendLog(remove ? $"> {name}: {id} を外す" : $"> {name}: {id} {version ?? "（最新）"} を入れる");
                var result = remove
                    ? await _service.RemoveAsync(project, id)
                    : await _service.InstallAsync(project, id, version);
                if (!result.Succeeded) failures++;
                AppendLog(result.Output);
            }
            await ReloadInstalledAsync();
            Status = failures == 0 ? "完了しました。" : $"{failures} 件失敗しました（下のログを参照）。";
            // プロジェクトの意味モデル（参照・Analyzer）を読み直す。言語サーバーは csproj の変更を自分で拾う。
            if (_solutionModel is not null) _ = _solutionModel.ReloadAsync();
        });
        await RefreshListAsync();
        if (SelectedPackage is { } row) await LoadSelectedAsync(row);
    }

    private async Task RunBusyAsync(string status, Func<Task> work)
    {
        IsBusy = true;
        Status = status;
        try { await work(); }
        catch (Exception ex) { Status = $"失敗しました: {ex.Message}"; AppendLog(ex.Message); }
        finally { IsBusy = false; }
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Log = Log.Length == 0 ? text.Trim() : Log + Environment.NewLine + text.Trim();
    }

    private IEnumerable<NuGetInstalledPackage> InstallsOf(string id)
        => _installed.Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string VersionOf(NuGetInstalledPackage package) => package.ResolvedVersion ?? package.RequestedVersion;

    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();
        public int Compare(string? x, string? y) => NuGetPackageService.CompareVersions(x ?? "", y ?? "");
    }
}
