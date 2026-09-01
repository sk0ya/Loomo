using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.CSharp.Projects;

/// <summary>solution／csprojを発見し、各csprojをMSBuild評価して共有モデルへ変換する。</summary>
public sealed class SolutionModelService : ISolutionModelService, IDisposable
{
    // MSBuildを無制限に起動すると大規模solutionでCPU／メモリを奪い合うため、
    // プロジェクト単位だけを小さく並列化する。TFM単位の評価はLoadProjectAsync内で順序を保つ。
    private const int MaxConcurrentProjectLoads = 4;
    private readonly IWorkspaceService _workspace;
    private readonly IProjectEvaluator _evaluator;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _selectedTargetFrameworks =
        new(StringComparer.OrdinalIgnoreCase);
    private string _selectedConfiguration = "Debug";
    private CancellationTokenSource? _reloadCts;
    private CancellationTokenSource? _scheduledReloadCts;
    private readonly List<FileSystemWatcher> _configurationWatchers = [];
    private SolutionModel _current;
    private bool _disposed;

    public SolutionModelService(IWorkspaceService workspace, IProjectEvaluator evaluator)
    {
        _workspace = workspace;
        _evaluator = evaluator;
        _current = workspace.PrimaryFolder is { } root
            ? SolutionModel.NotConfigured(root)
            : SolutionModel.NotConfigured(Environment.CurrentDirectory);
        _workspace.FoldersChanged += OnFoldersChanged;
        ConfigureConfigurationWatchers();
    }

    public SolutionModel Current { get { lock (_gate) return _current; } }
    public event EventHandler<SolutionModel>? Changed;

    public async Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            _reloadCts?.Cancel();
            _reloadCts?.Dispose();
            _reloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked = _reloadCts;
        }

        var roots = _workspace.Folders.ToArray();
        var discoveries = roots.Select(SolutionProjectDiscovery.Find).ToArray();
        var firstSolution = discoveries.Select(d => d.SolutionPath).FirstOrDefault(p => p is not null);
        var configurations = SolutionProjectDiscovery.ReadConfigurations(firstSolution);
        string selectedConfiguration;
        lock (_gate)
        {
            selectedConfiguration = configurations.FirstOrDefault(c =>
                string.Equals(c, _selectedConfiguration, StringComparison.OrdinalIgnoreCase))
                ?? configurations.FirstOrDefault(c =>
                    string.Equals(c, "Debug", StringComparison.OrdinalIgnoreCase))
                ?? configurations[0];
            _selectedConfiguration = selectedConfiguration;
        }
        var loading = new SolutionModel(null, "ワークスペース", roots.FirstOrDefault() ?? Environment.CurrentDirectory,
            Array.Empty<ProjectModel>(), roots.Length == 0 ? ProjectLoadState.NotConfigured : ProjectLoadState.Loading,
            Configurations: configurations, SelectedConfiguration: selectedConfiguration);
        Publish(loading);
        if (roots.Length == 0) return loading;

        try
        {
            var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var projectPaths = new List<string>();
            foreach (var discovery in discoveries)
            {
                linked.Token.ThrowIfCancellationRequested();
                foreach (var projectPath in discovery.ProjectPaths)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (seenProjects.Add(Path.GetFullPath(projectPath))) projectPaths.Add(projectPath);
                }
            }

            using var loadGate = new SemaphoreSlim(Math.Min(MaxConcurrentProjectLoads, projectPaths.Count));
            var loadTasks = projectPaths.Select(async projectPath =>
            {
                await loadGate.WaitAsync(linked.Token);
                try
                {
                    return await LoadProjectAsync(projectPath, firstSolution, selectedConfiguration, linked.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return FailedProject(projectPath, ex.Message, selectedConfiguration);
                }
                finally
                {
                    loadGate.Release();
                }
            }).ToArray();
            // WhenAllの戻り値は入力順なので、並列評価してもSolution Explorerの順序を変えない。
            var projects = (await Task.WhenAll(loadTasks)).ToList();

            var rootDirectory = roots[0];
            var name = firstSolution is null
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(rootDirectory))
                : Path.GetFileNameWithoutExtension(firstSolution);
            var state = projects.Any(p => p.State == ProjectLoadState.Failed)
                ? ProjectLoadState.Failed : ProjectLoadState.Ready;
            var error = projects.FirstOrDefault(p => p.State == ProjectLoadState.Failed)?.Error;
            var ready = new SolutionModel(firstSolution, name, rootDirectory, projects, state, error,
                configurations, selectedConfiguration);
            Publish(ready);
            return ready;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = new SolutionModel(null, "ワークスペース", roots[0], Array.Empty<ProjectModel>(),
                ProjectLoadState.Failed, ex.Message, configurations, selectedConfiguration);
            Publish(failed);
            return failed;
        }
    }

    public ProjectModel? ProjectForFile(string filePath) => Current.ProjectForFile(filePath);

    public ProjectLoadState FileState(string filePath) => Current.ResolveFileState(filePath);

    public Task<bool> SelectTargetFrameworkAsync(
        string projectPath, string targetFramework, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullProjectPath = Path.GetFullPath(projectPath);
        SolutionModel current;
        ProjectModel? project;
        lock (_gate)
        {
            current = _current;
            project = current.Projects.FirstOrDefault(p =>
                string.Equals(p.FullPath, fullProjectPath, StringComparison.OrdinalIgnoreCase));
            if (project is null || project.TargetFrameworks.All(t =>
                    !string.Equals(t.Name, targetFramework, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(false);
            var selected = project.TargetFrameworks.First(t =>
                string.Equals(t.Name, targetFramework, StringComparison.OrdinalIgnoreCase)).Name;
            _selectedTargetFrameworks[fullProjectPath] = selected;
            var updated = project with { SelectedTargetFramework = selected };
            current = current with
            {
                Projects = current.Projects.Select(p => ReferenceEquals(p, project) ? updated : p).ToList()
            };
        }
        Publish(current);
        return Task.FromResult(true);
    }

    public async Task<bool> SelectConfigurationAsync(
        string configuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuration)) return false;
        lock (_gate)
        {
            if (!_current.ConfigurationOptions.Any(c =>
                    string.Equals(c, configuration, StringComparison.OrdinalIgnoreCase)))
                return false;
            _selectedConfiguration = _current.ConfigurationOptions.First(c =>
                string.Equals(c, configuration, StringComparison.OrdinalIgnoreCase));
        }

        await ReloadAsync(cancellationToken);
        return true;
    }

    private async Task<ProjectModel> LoadProjectAsync(
        string projectPath, string? solutionPath, string solutionConfiguration, CancellationToken ct)
    {
        var configuration = SolutionProjectDiscovery.ResolveProjectConfiguration(
            solutionPath, projectPath, solutionConfiguration);
        var first = await _evaluator.EvaluateAsync(projectPath, null, configuration, ct);
        var tfms = Split(first.TargetFrameworks).ToList();
        if (tfms.Count == 0 && !string.IsNullOrWhiteSpace(first.TargetFramework)) tfms.Add(first.TargetFramework!);
        if (tfms.Count == 0) tfms.Add("");
        IReadOnlyList<ProjectEvaluation> evaluations = tfms.Count == 1 && string.IsNullOrEmpty(first.TargetFrameworks)
            ? [first]
            : await EvaluateTargetFrameworksAsync(projectPath, tfms, configuration, ct);

        var targetModels = evaluations
            .Select((e, i) => ToTargetFramework(e, tfms[Math.Min(i, tfms.Count - 1)], projectPath))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var references = first.ProjectReferences
            .Select(i => ResolveItemPath(projectPath, i))
            .Where(p => p is not null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var fullProjectPath = Path.GetFullPath(projectPath);
        string? selectedTargetFramework;
        lock (_gate)
            selectedTargetFramework = _selectedTargetFrameworks.TryGetValue(fullProjectPath, out var selected) &&
                targetModels.Any(t => string.Equals(t.Name, selected, StringComparison.OrdinalIgnoreCase))
                ? targetModels.First(t => string.Equals(t.Name, selected, StringComparison.OrdinalIgnoreCase)).Name
                : null;
        selectedTargetFramework ??= targetModels.FirstOrDefault()?.Name;
        return new ProjectModel(Path.GetFileNameWithoutExtension(projectPath), fullProjectPath,
            Path.GetDirectoryName(Path.GetFullPath(projectPath))!, references, targetModels,
            selectedTargetFramework, first.IsTestProject, ProjectLoadState.Ready)
        {
            PackageReferences = (first.PackageReferences ?? Array.Empty<ProjectItemEvaluation>())
                .Select(i => i.Include).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Configuration = configuration,
        };
    }

    private async Task<List<ProjectEvaluation>> EvaluateTargetFrameworksAsync(
        string projectPath, IReadOnlyList<string> tfms, string configuration, CancellationToken ct)
    {
        var result = new List<ProjectEvaluation>(tfms.Count);
        foreach (var tfm in tfms)
            result.Add(await _evaluator.EvaluateAsync(projectPath, tfm, configuration, ct));
        return result;
    }

    private static ProjectModel FailedProject(string projectPath, string error, string configuration)
    {
        var full = Path.GetFullPath(projectPath);
        return new ProjectModel(Path.GetFileNameWithoutExtension(full), full,
            Path.GetDirectoryName(full)!, Array.Empty<string>(), Array.Empty<TargetFrameworkModel>(),
            null, false, ProjectLoadState.Failed, error) { Configuration = configuration };
    }

    private static TargetFrameworkModel ToTargetFramework(ProjectEvaluation e, string tfm, string projectPath)
        => new TargetFrameworkModel(tfm.Length == 0 ? "(既定)" : tfm, Split(e.DefineConstants).ToList(), e.LangVersion ?? "default",
            ToItems(e.Compile, projectPath), ToItems(e.Analyzers, projectPath),
            ToItems(e.AdditionalFiles, projectPath), ToItems(e.None, projectPath))
            with
            {
                ProjectReferences = e.ProjectReferences
                    .Select(i => ResolveItemPath(projectPath, i))
                    .Where(path => path is not null)
                    .Select(path => path!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                References = ToItems(e.References ?? [], projectPath),
                Nullable = e.Nullable,
            };

    private static IReadOnlyList<ProjectItem> ToItems(IEnumerable<ProjectItemEvaluation> items, string projectPath)
        => items.Select(i => new ProjectItem(i.Include, ResolveItemPath(projectPath, i) ?? i.Include, i.Link)).ToList();

    private static string? ResolveItemPath(string projectPath, ProjectItemEvaluation item)
    {
        if (Path.IsPathRooted(item.FullPath ?? item.Include)) return Path.GetFullPath(item.FullPath ?? item.Include);
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        return Path.GetFullPath(Path.Combine(dir, item.FullPath ?? item.Include));
    }

    private static IEnumerable<string> Split(string? value)
        => (value ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void Publish(SolutionModel model)
    {
        lock (_gate) _current = model;
        Changed?.Invoke(this, model);
    }

    private void OnFoldersChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        ConfigureConfigurationWatchers();
        _ = ReloadAfterWorkspaceChangeAsync();
    }

    /// <summary>MSBuild／Roslynの評価結果に影響する設定ファイルを監視する。
    /// FileSystemWatcherは通知をまとめてくれないため、書き込み中の中間状態を評価しないよう
    /// 300msのデバウンスを入れる。監視はC#プロジェクトサービス内に置き、UIやLSPが個別に
    /// 同じファイルを監視して再評価を競合させない。</summary>
    private void ConfigureConfigurationWatchers()
    {
        if (_disposed) return;
        var roots = _workspace.Folders
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            foreach (var watcher in _configurationWatchers) watcher.Dispose();
            _configurationWatchers.Clear();
            foreach (var root in roots)
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                                       NotifyFilters.Size | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true,
                    };
                    watcher.Changed += OnConfigurationFileChanged;
                    watcher.Created += OnConfigurationFileChanged;
                    watcher.Deleted += OnConfigurationFileChanged;
                    watcher.Renamed += OnConfigurationFileRenamed;
                    _configurationWatchers.Add(watcher);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void OnConfigurationFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsConfigurationFile(e.FullPath)) ScheduleConfigurationReload();
    }

    private void OnConfigurationFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsConfigurationFile(e.FullPath) || IsConfigurationFile(e.OldFullPath))
            ScheduleConfigurationReload();
    }

    /// <summary>評価に影響するファイルか。テスト可能な純粋判定にして、無関係なソース変更で
    /// MSBuildを起動しない。</summary>
    internal static bool IsConfigurationFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("stylecop.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".stylecop.json", StringComparison.OrdinalIgnoreCase)) return true;

        var extension = Path.GetExtension(name);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ruleset", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleConfigurationReload()
    {
        CancellationTokenSource scheduled;
        lock (_gate)
        {
            if (_disposed) return;
            _scheduledReloadCts?.Cancel();
            _scheduledReloadCts?.Dispose();
            _scheduledReloadCts = new CancellationTokenSource();
            scheduled = _scheduledReloadCts;
        }
        _ = ReloadAfterConfigurationChangeAsync(scheduled);
    }

    private async Task ReloadAfterConfigurationChangeAsync(CancellationTokenSource scheduled)
    {
        try
        {
            await Task.Delay(300, scheduled.Token);
            await ReloadAsync(scheduled.Token);
        }
        catch (OperationCanceledException) when (scheduled.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_scheduledReloadCts, scheduled))
                {
                    _scheduledReloadCts.Dispose();
                    _scheduledReloadCts = null;
                }
            }
        }
    }

    private async Task ReloadAfterWorkspaceChangeAsync()
    {
        try { await ReloadAsync(); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.FoldersChanged -= OnFoldersChanged;
        lock (_gate)
        {
            _reloadCts?.Cancel();
            _reloadCts?.Dispose();
            _reloadCts = null;
            _scheduledReloadCts?.Cancel();
            _scheduledReloadCts?.Dispose();
            _scheduledReloadCts = null;
            foreach (var watcher in _configurationWatchers) watcher.Dispose();
            _configurationWatchers.Clear();
        }
    }
}

/// <summary>solution形式を、プロジェクトファイル一覧へ落とす純粋な発見器。</summary>
internal static class SolutionProjectDiscovery
{
    private static readonly string[] Ignored = ["bin", "obj", ".git", ".vs", "node_modules"];
    private static readonly Regex SlnProject = new(
        """^Project\("\{[^"]+\}"\)\s*=\s*"[^"]+",\s*"(?<path>[^"]+\.csproj)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SlnProjectGuid = new(
        @"(?<guid>[0-9a-f-]{36})\}?""\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SlnActiveConfiguration = new(
        @"^\s*\{?(?<guid>[0-9a-f-]{36})\}?\.(?<solution>[^|]+)\|[^.]+\.ActiveCfg\s*=\s*(?<project>[^|]+)\|",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static (string? SolutionPath, IReadOnlyList<string> ProjectPaths) Find(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return (null, Array.Empty<string>());

        try
        {
            var solution = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (solution is not null)
            {
                var paths = Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                    ? ParseSlnx(solution) : ParseSln(solution);
                if (paths.Count > 0) return (solution, paths);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Xml.XmlException) { }
        catch (ArgumentException) { }

        // A partially-written/malformed solution, or a workspace folder that changed
        // while the watcher was handling the event, must not abort the whole C# model
        // reload. Fall back to the same bounded scan used for a solution-less folder.
        try
        {
            return (null, ScanProjects(root));
        }
        catch (IOException) { return (null, Array.Empty<string>()); }
        catch (UnauthorizedAccessException) { return (null, Array.Empty<string>()); }
    }

    /// <summary>.slnのSolutionConfigurationPlatformsから構成名だけを取り出す。
    /// csproj単独／slnxで構成情報が取れない場合はdotnet標準のDebug／Releaseへ戻す。</summary>
    public static IReadOnlyList<string> ReadConfigurations(string? solutionPath)
    {
        var result = new List<string>();
        if (solutionPath is not null && File.Exists(solutionPath))
        {
            try
            {
                var inSection = false;
                foreach (var line in File.ReadLines(solutionPath))
                {
                    if (line.Contains("GlobalSection(SolutionConfigurationPlatforms)", StringComparison.OrdinalIgnoreCase))
                    {
                        inSection = true;
                        continue;
                    }
                    if (!inSection) continue;
                    if (line.Contains("EndGlobalSection", StringComparison.OrdinalIgnoreCase)) break;
                    var separator = line.IndexOf('|');
                    var equals = line.IndexOf('=');
                    if (separator <= 0 || equals < separator) continue;
                    var name = line[..separator].Trim();
                    if (name.Length > 0 && !result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
                }
            }
            catch (IOException) { result.Clear(); }
            catch (UnauthorizedAccessException) { result.Clear(); }
            catch (ArgumentException) { result.Clear(); }
        }

        if (result.Count == 0) result.AddRange(["Debug", "Release"]);
        return result;
    }

    /// <summary>solutionの選択構成を、各プロジェクトへ割り当てられた実構成へ変換する。
    /// Visual StudioのProjectConfigurationPlatformsを尊重し、マッピングが無い形式は
    /// 選択構成名へ安全にフォールバックする。</summary>
    public static string ResolveProjectConfiguration(
        string? solutionPath, string projectPath, string selectedConfiguration)
    {
        if (string.IsNullOrWhiteSpace(solutionPath) ||
            !Path.GetExtension(solutionPath).Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(solutionPath) || string.IsNullOrWhiteSpace(selectedConfiguration))
            return selectedConfiguration;

        try
        {
            var fullProjectPath = Path.GetFullPath(projectPath);
            string? projectGuid = null;
            var lines = File.ReadLines(solutionPath).ToArray();
            foreach (var line in lines)
            {
                var project = SlnProject.Match(line);
                if (!project.Success) continue;
                var relative = project.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar);
                var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(solutionPath)!, relative));
                if (!string.Equals(candidate, fullProjectPath, StringComparison.OrdinalIgnoreCase)) continue;
                projectGuid = SlnProjectGuid.Match(line).Groups["guid"].Value;
                break;
            }
            if (string.IsNullOrWhiteSpace(projectGuid)) return selectedConfiguration;

            foreach (var line in lines)
            {
                var mapping = SlnActiveConfiguration.Match(line);
                if (!mapping.Success || !mapping.Groups["guid"].Value.Equals(projectGuid,
                        StringComparison.OrdinalIgnoreCase) ||
                    !mapping.Groups["solution"].Value.Trim().Equals(selectedConfiguration.Trim(),
                        StringComparison.OrdinalIgnoreCase)) continue;
                var projectConfiguration = mapping.Groups["project"].Value.Trim();
                return projectConfiguration.Length == 0 ? selectedConfiguration : projectConfiguration;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }

        return selectedConfiguration;
    }

    private static List<string> ParseSln(string path)
    {
        var result = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var match = SlnProject.Match(line);
            if (!match.Success) continue;
            var relative = match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative));
            if (File.Exists(full)) result.Add(full);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ParseSlnx(string path)
    {
        var result = new List<string>();
        var doc = System.Xml.Linq.XDocument.Load(path);
        foreach (var project in doc.Descendants().Where(e => e.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = (string?)project.Attribute("Path") ?? (string?)project.Attribute("path");
            if (relative is null || !relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative));
            if (File.Exists(full)) result.Add(full);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ScanProjects(string root)
    {
        var result = new List<string>();
        Scan(root, 8, result);
        return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Scan(string dir, int depth, List<string> result)
    {
        if (depth < 0) return;
        try
        {
            result.AddRange(Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly));
            if (depth == 0) return;
            foreach (var child in Directory.EnumerateDirectories(dir))
                if (!Ignored.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)
                    && !Path.GetFileName(child).StartsWith(".", StringComparison.Ordinal))
                    Scan(child, depth - 1, result);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
