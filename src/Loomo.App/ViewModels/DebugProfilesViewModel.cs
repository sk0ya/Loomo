using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.CSharp.Debug;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>名前付きデバッグ構成（プロファイル）の一覧・永続化・起動プロジェクト検出を担うサブ ViewModel。
/// <see cref="DebugLaunchViewModel"/>（起動・引数・環境変数・例外オプションの「現在値」を持つ既存クラス）とは
/// <see cref="AttachLaunch"/> で接続し、プロファイル切替時は現在値へ流し込み、現在値の編集はデバウンスして
/// 選択中プロファイルへ自動保存する仲介役（Launch 側の既存プロパティ・コマンドは無改修）。</summary>
public sealed partial class DebugProfilesViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> WatchedLaunchProperties = new(StringComparer.Ordinal)
    {
        nameof(ILaunchConfigurationOwner.TargetProgram), nameof(ILaunchConfigurationOwner.BuildFirst),
        nameof(ILaunchConfigurationOwner.LaunchArgs), nameof(ILaunchConfigurationOwner.LaunchEnv),
        nameof(ILaunchConfigurationOwner.LaunchWorkingDirectory),
        nameof(ILaunchConfigurationOwner.JustMyCode), nameof(ILaunchConfigurationOwner.BreakOnAllExceptions),
        nameof(ILaunchConfigurationOwner.BreakOnUncaughtExceptions),
    };

    private readonly IWorkspaceService _workspace;
    private readonly DebugLaunchProfileStore _store;
    private readonly Func<string, IEnumerable<DebugProjectDiscovery.ProjectEntry>> _discoverProjects;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveDebounce;
    private ILaunchConfigurationOwner? _launch;

    /// <summary>ApplySelectedProfileToLaunch 中は Launch.PropertyChanged による保存を止める（読み込み⇄保存の無限往復防止）。</summary>
    private bool _applying;

    public ObservableCollection<DebugLaunchProfileItem> Profiles { get; } = new();

    [ObservableProperty] private DebugLaunchProfileItem? _selectedProfile;

    /// <summary>起動プロジェクト候補（先頭は <see cref="DebugProjectDiscovery.AutoDetect"/> センチネル。
    /// テストプロジェクトは除外）。</summary>
    public ObservableCollection<DebugProjectDiscovery.ProjectEntry> AvailableProjects { get; } = new();

    [ObservableProperty] private DebugProjectDiscovery.ProjectEntry _selectedProject = DebugProjectDiscovery.AutoDetect;

    /// <summary>選択したcsprojのlaunchSettings.jsonから読んだ候補。C#固有のJSON解析はLoomo.CSharpが担当する。</summary>
    public ObservableCollection<LaunchSettingsProfile> LaunchSettingsProfiles { get; } = new();

    [ObservableProperty] private LaunchSettingsProfile? _selectedLaunchSettingsProfile;
    [ObservableProperty] private string _launchSettingsStatus = "";

    private string? _launchSettingsProjectPath;

    /// <summary>選択中プロジェクトの絶対パス（自動検出センチネルなら null）。<see cref="DebugLaunchViewModel.StartAsync"/> が
    /// <see cref="DebugTargetResolver.ResolveProgramAsync"/> の explicitProjectPath 引数へそのまま渡す。</summary>
    public string? SelectedProjectPath
        => ReferenceEquals(SelectedProject, DebugProjectDiscovery.AutoDetect) ? null : SelectedProject.FullPath;

    /// <summary>指定プロジェクトの通常実行で使える、現在選択中のlaunchSettingsプロファイル名。
    /// 別プロジェクトの選択状態やdotnet launchで扱えない形式は返さない。</summary>
    public string? SelectedSupportedLaunchProfileNameFor(string projectPath)
        => SelectedRunLaunchProfileFor(projectPath) is { IsSupported: true, Name.Length: > 0 } profile
            ? profile.Name
            : null;

    /// <summary>指定プロジェクトで通常実行に使えるlaunchSettingsプロファイルを返す。
    /// Project／Executableはdotnet run、IIS Expressは専用ランチャーへ渡す。通常実行と
    /// netcoredbg attachの両方で同じプロファイルを利用する窓口。</summary>
    public LaunchSettingsProfile? SelectedRunLaunchProfileFor(string projectPath)
        => SelectedProjectPath is { } selected
            && string.Equals(Path.GetFullPath(selected), Path.GetFullPath(projectPath),
                StringComparison.OrdinalIgnoreCase)
            && SelectedLaunchSettingsProfile is { IsRunSupported: true } profile
            ? profile
            : null;

    internal bool CanDeleteSelectedProfile => Profiles.Count > 1 && SelectedProfile is not null;

    /// <param name="discoverProjects">起動プロジェクト候補の探索（フォルダー → 候補列。テスト除外は呼び出し側）。
    /// 省略時は csproj 探索（dotnet 用の従来挙動）。TS 側は package.json 探索を渡す。</param>
    internal DebugProfilesViewModel(IWorkspaceService workspace, DebugLaunchProfileStore store,
        Func<string, IEnumerable<DebugProjectDiscovery.ProjectEntry>>? discoverProjects = null)
    {
        _workspace = workspace;
        _store = store;
        _discoverProjects = discoverProjects ?? (folder => DebugProjectDiscovery.Discover(folder).Where(p => !p.IsTest));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveCurrentToSelectedProfile(); };

        _workspace.RootChanged += OnWorkspaceRootChanged;
        _workspace.FoldersChanged += OnWorkspaceFoldersChanged;
        ReloadForWorkspace();
    }

    public void Dispose()
    {
        _workspace.RootChanged -= OnWorkspaceRootChanged;
        _workspace.FoldersChanged -= OnWorkspaceFoldersChanged;
        _saveDebounce.Stop();
        if (_launch is not null) _launch.PropertyChanged -= OnLaunchPropertyChanged;
    }

    /// <summary>Launch と接続する。マネージャ VM が Profiles/Launch 両方を構築した直後に 1 回だけ呼ぶ。</summary>
    internal void AttachLaunch(ILaunchConfigurationOwner launch)
    {
        _launch = launch;
        _launch.PropertyChanged += OnLaunchPropertyChanged;
        ApplySelectedProfileToLaunch();
        // ApplySelectedProfileToLaunch は選択中プロファイルのプロジェクトへ切り替えた後、
        // launchSettings候補を読み直して保存済みの選択を復元する。ここで無条件に再読込すると
        // その直後に SelectedLaunchSettingsProfile を null に戻してしまうため、プロファイルが
        // 無い初期状態に限って補完する。
        if (SelectedProfile is null)
            ReloadLaunchSettingsProfiles();
    }

    private void OnLaunchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying) return;
        if (e.PropertyName is null || !WatchedLaunchProperties.Contains(e.PropertyName)) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    partial void OnSelectedProfileChanged(DebugLaunchProfileItem? value)
    {
        ApplySelectedProfileToLaunch();
        PersistAll();
    }

    partial void OnSelectedProjectChanged(DebugProjectDiscovery.ProjectEntry value)
    {
        OnPropertyChanged(nameof(SelectedProjectPath));
        if (_applying) return;
        ReloadLaunchSettingsProfiles();
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    partial void OnSelectedLaunchSettingsProfileChanged(LaunchSettingsProfile? value)
    {
        if (value is null) return;
        if (!value.IsRunSupported)
        {
            if (_launch is ILaunchBrowserTarget browserTarget)
                browserTarget.LaunchBrowserUrl = "";
            LaunchSettingsStatus = $"未対応の起動形式: {value.CommandName}";
            _saveDebounce.Stop();
            _saveDebounce.Start();
            return;
        }
        if (value.IsIisExpress)
        {
            _applying = true;
            try
            {
                // IIS Expressはdotnetのlaunch対象DLLではないため、通常Runでは専用コマンドへ、
                // Debugでは起動したiisexpress.exeへnetcoredbgがattachする。
                if (_launch is not null)
                {
                    _launch.TargetProgram = "";
                    _launch.BuildFirst = true;
                    _launch.LaunchArgs = "";
                    _launch.LaunchEnv = string.Join(Environment.NewLine,
                        value.EnvironmentVariables.Select(p => $"{p.Key}={p.Value}"));
                    _launch.LaunchWorkingDirectory = ResolveLaunchPath(
                        value.WorkingDirectory, Path.GetDirectoryName(_launchSettingsProjectPath!)!);
                }
                if (_launch is ILaunchBrowserTarget browserTarget)
                    browserTarget.LaunchBrowserUrl = value.BrowserUrl ?? "";
                LaunchSettingsStatus = value.BrowserUrl is { } browserUrl
                    ? $"launchSettings: {value.Name} を適用しました（IIS Express／ブラウザ: {browserUrl}）"
                    : $"launchSettings: {value.Name} を適用しました（IIS Express／Run・Debug attach対応）";
            }
            finally { _applying = false; }
            _saveDebounce.Stop();
            _saveDebounce.Start();
            return;
        }

        ApplyLaunchSettingsProfile(value);
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    /// <summary>選択中プロファイルの値を Launch のプロパティへ読み込む。</summary>
    private void ApplySelectedProfileToLaunch()
    {
        if (_launch is null || SelectedProfile is null) return;
        _applying = true;
        try
        {
            var m = SelectedProfile.Model;
            _launch.TargetProgram = m.TargetProgram;
            _launch.BuildFirst = m.BuildFirst;
            _launch.LaunchArgs = m.LaunchArgs;
            _launch.LaunchEnv = m.LaunchEnv;
            _launch.LaunchWorkingDirectory = m.WorkingDirectory;
            _launch.JustMyCode = m.JustMyCode;
            _launch.BreakOnAllExceptions = m.BreakOnAllExceptions;
            _launch.BreakOnUncaughtExceptions = m.BreakOnUncaughtExceptions;
            if (_launch is ILaunchBrowserTarget browserTarget)
                browserTarget.LaunchBrowserUrl = "";
            SelectedProject = string.IsNullOrEmpty(m.ProjectPath)
                ? DebugProjectDiscovery.AutoDetect
                : AvailableProjects.FirstOrDefault(p =>
                    string.Equals(p.RelativePath, m.ProjectPath, StringComparison.OrdinalIgnoreCase))
                  ?? DebugProjectDiscovery.AutoDetect;
        }
        finally { _applying = false; }

        // プロファイル切替では SelectedProject の変更通知を _applying で抑制しているため、
        // OnSelectedProjectChanged の再読込も抑制される。ここで新しい起動プロジェクトの
        // launchSettings.json を明示的に読み直してから、保存済みプロファイル名を解決する。
        // これを省くと、プロジェクトAからBへ切り替えた際にAの起動候補が残り、Bの
        // launchSettingsプロファイルを選び直せない。
        ReloadLaunchSettingsProfiles();

        // launchSettings.json はプロジェクトの再評価後に候補が作られるため、保存名を
        // 一度候補へ解決してから適用する。見つからない場合は現在値を壊さず、未選択のままにする。
        if (!string.IsNullOrWhiteSpace(SelectedProfile?.Model.LaunchSettingsProfileName))
        {
            var stored = LaunchSettingsProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, SelectedProfile.Model.LaunchSettingsProfileName,
                    StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
                SelectedLaunchSettingsProfile = stored;
        }
    }

    private void ReloadLaunchSettingsProfiles()
    {
        LaunchSettingsProfiles.Clear();
        SelectedLaunchSettingsProfile = null;
        if (_launch is ILaunchBrowserTarget browserTarget)
            browserTarget.LaunchBrowserUrl = "";
        _launchSettingsProjectPath = SelectedProjectPath;
        if (string.IsNullOrWhiteSpace(_launchSettingsProjectPath))
        {
            LaunchSettingsStatus = "起動プロジェクトを選ぶとlaunchSettings.jsonを読み込めます";
            return;
        }

        var profiles = LaunchSettingsProfileParser.ParseProject(_launchSettingsProjectPath, out var error);
        foreach (var profile in profiles) LaunchSettingsProfiles.Add(profile);
        LaunchSettingsStatus = error is not null
            ? $"launchSettings.jsonを読めません: {error}"
            : profiles.Count == 0
                ? "launchSettings.jsonの起動プロファイルなし"
                : $"launchSettings.json: {profiles.Count}件（Project／Executable／IIS Express対応）";
    }

    private void ApplyLaunchSettingsProfile(LaunchSettingsProfile profile)
    {
        if (_launch is null || _launchSettingsProjectPath is null) return;
        _applying = true;
        try
        {
            var projectDirectory = Path.GetDirectoryName(_launchSettingsProjectPath)!;
            _launch.TargetProgram = string.Equals(profile.CommandName, "Executable", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(profile.ExecutablePath)
                ? Path.GetFullPath(Path.IsPathRooted(profile.ExecutablePath)
                    ? profile.ExecutablePath
                    : Path.Combine(projectDirectory, profile.ExecutablePath))
                : "";
            _launch.BuildFirst = true;
            _launch.LaunchArgs = profile.CommandLineArgs ?? "";
            _launch.LaunchEnv = string.Join(Environment.NewLine,
                profile.EnvironmentVariables.Select(p => $"{p.Key}={p.Value}"));
            _launch.LaunchWorkingDirectory = ResolveLaunchPath(profile.WorkingDirectory, projectDirectory);
            if (_launch is ILaunchBrowserTarget browserTarget)
                browserTarget.LaunchBrowserUrl = profile.BrowserUrl ?? "";
            LaunchSettingsStatus = profile.BrowserUrl is { } browserUrl
                ? $"launchSettings: {profile.Name} を適用しました（ブラウザ: {browserUrl}）"
                : $"launchSettings: {profile.Name} を適用しました";
        }
        finally { _applying = false; }
    }

    private static string ResolveLaunchPath(string? path, string baseDirectory)
    {
        var value = path?.Trim() ?? "";
        if (value.Length == 0) return "";
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));
    }

    /// <summary>Launch の現在値を選択中プロファイルへ書き戻して保存する（デバウンス満了時）。</summary>
    private void SaveCurrentToSelectedProfile()
    {
        if (_launch is null || SelectedProfile is null) return;
        SelectedProfile.Model = SelectedProfile.Model with
        {
            ProjectPath = SelectedProjectPath is null ? null : SelectedProject.RelativePath,
            TargetProgram = _launch.TargetProgram,
            BuildFirst = _launch.BuildFirst,
            LaunchArgs = _launch.LaunchArgs,
            LaunchEnv = _launch.LaunchEnv,
            WorkingDirectory = _launch.LaunchWorkingDirectory,
            JustMyCode = _launch.JustMyCode,
            BreakOnAllExceptions = _launch.BreakOnAllExceptions,
            BreakOnUncaughtExceptions = _launch.BreakOnUncaughtExceptions,
            LaunchSettingsProfileName = SelectedLaunchSettingsProfile?.Name ?? "",
        };
        PersistAll();
    }

    private void OnWorkspaceRootChanged(object? sender, string? root)
        => _dispatcher.InvokeAsync(ReloadForWorkspace, DispatcherPriority.Background);

    private void OnWorkspaceFoldersChanged(object? sender, EventArgs e)
        => _dispatcher.InvokeAsync(ReloadForWorkspace, DispatcherPriority.Background);

    /// <summary>ワークスペース（切替含む）に合わせてプロジェクト候補とプロファイル一覧を読み直す。
    /// 保存済みプロファイルが無ければ「既定」を1件シードする（＝これまでの自動検出のみの体験を維持）。
    /// 複数フォルダー時は全フォルダーを走査し、候補の相対パスにフォルダー名を前置して区別する。</summary>
    private void ReloadForWorkspace()
    {
        var root = _workspace.PrimaryFolder;
        Profiles.Clear();
        AvailableProjects.Clear();
        AvailableProjects.Add(DebugProjectDiscovery.AutoDetect);

        if (root is null)
        {
            SelectedProfile = null;
            return;
        }

        var folders = _workspace.Folders;
        foreach (var folder in folders)
        {
            var entries = _discoverProjects(folder);
            if (folders.Count <= 1)
            {
                foreach (var p in entries) AvailableProjects.Add(p);
                continue;
            }

            var folderName = Path.GetFileName(folder.TrimEnd('\\', '/'));
            foreach (var p in entries)
                AvailableProjects.Add(p with { RelativePath = folderName + "/" + p.RelativePath });
        }

        var (loaded, selectedId) = _store.Load(root);
        var seeded = loaded.Count == 0;
        var profiles = seeded ? new List<DebugLaunchProfile> { DebugLaunchProfile.CreateDefault("既定") } : loaded;
        foreach (var p in profiles) Profiles.Add(new DebugLaunchProfileItem(p));

        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles[0];
        ReloadLaunchSettingsProfiles();
        if (seeded) PersistAll();
    }

    private void PersistAll()
    {
        var root = _workspace.PrimaryFolder;
        if (root is null) return;
        _store.Save(root, Profiles.Select(p => p.Model).ToList(), SelectedProfile?.Id);
    }

    // --- プロファイル管理（追加・名前変更・削除。名前はビュー側で InputDialog.Prompt から取得して渡す） ---

    /// <summary>現在の Launch/選択中プロジェクトの値を引き継いだ新規プロファイルを追加して選択する。</summary>
    internal void AddProfile(string name)
    {
        var created = _launch is null
            ? DebugLaunchProfile.CreateDefault(name)
            : new DebugLaunchProfile(
                Guid.NewGuid().ToString("N"), name, SelectedProjectPath is null ? null : SelectedProject.RelativePath,
                _launch.TargetProgram, _launch.BuildFirst, _launch.LaunchArgs, _launch.LaunchEnv,
                _launch.JustMyCode, _launch.BreakOnAllExceptions, _launch.BreakOnUncaughtExceptions,
                _launch.LaunchWorkingDirectory);
        var item = new DebugLaunchProfileItem(created);
        Profiles.Add(item);
        SelectedProfile = item;
    }

    internal void RenameSelectedProfile(string name)
    {
        if (SelectedProfile is null) return;
        SelectedProfile.Model = SelectedProfile.Model with { Name = name };
        PersistAll();
    }

    internal void DeleteSelectedProfile()
    {
        if (!CanDeleteSelectedProfile || SelectedProfile is null) return;
        var idx = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles[Math.Min(idx, Profiles.Count - 1)];
    }
}
