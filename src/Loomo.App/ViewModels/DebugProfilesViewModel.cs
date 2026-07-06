using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
        nameof(DebugLaunchViewModel.TargetProgram), nameof(DebugLaunchViewModel.BuildFirst),
        nameof(DebugLaunchViewModel.LaunchArgs), nameof(DebugLaunchViewModel.LaunchEnv),
        nameof(DebugLaunchViewModel.JustMyCode), nameof(DebugLaunchViewModel.BreakOnAllExceptions),
        nameof(DebugLaunchViewModel.BreakOnUncaughtExceptions),
    };

    private readonly IWorkspaceService _workspace;
    private readonly DebugLaunchProfileStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveDebounce;
    private DebugLaunchViewModel? _launch;

    /// <summary>ApplySelectedProfileToLaunch 中は Launch.PropertyChanged による保存を止める（読み込み⇄保存の無限往復防止）。</summary>
    private bool _applying;

    public ObservableCollection<DebugLaunchProfileItem> Profiles { get; } = new();

    [ObservableProperty] private DebugLaunchProfileItem? _selectedProfile;

    /// <summary>起動プロジェクト候補（先頭は <see cref="DebugProjectDiscovery.AutoDetect"/> センチネル。
    /// テストプロジェクトは除外）。</summary>
    public ObservableCollection<DebugProjectDiscovery.ProjectEntry> AvailableProjects { get; } = new();

    [ObservableProperty] private DebugProjectDiscovery.ProjectEntry _selectedProject = DebugProjectDiscovery.AutoDetect;

    /// <summary>選択中プロジェクトの絶対パス（自動検出センチネルなら null）。<see cref="DebugLaunchViewModel.StartAsync"/> が
    /// <see cref="DebugTargetResolver.ResolveProgramAsync"/> の explicitProjectPath 引数へそのまま渡す。</summary>
    public string? SelectedProjectPath
        => ReferenceEquals(SelectedProject, DebugProjectDiscovery.AutoDetect) ? null : SelectedProject.FullPath;

    internal bool CanDeleteSelectedProfile => Profiles.Count > 1 && SelectedProfile is not null;

    internal DebugProfilesViewModel(IWorkspaceService workspace, DebugLaunchProfileStore store)
    {
        _workspace = workspace;
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveCurrentToSelectedProfile(); };

        _workspace.RootChanged += OnWorkspaceRootChanged;
        ReloadForWorkspace();
    }

    public void Dispose()
    {
        _workspace.RootChanged -= OnWorkspaceRootChanged;
        _saveDebounce.Stop();
        if (_launch is not null) _launch.PropertyChanged -= OnLaunchPropertyChanged;
    }

    /// <summary>Launch と接続する。<c>DebugViewModel</c> が Profiles/Launch 両方を構築した直後に 1 回だけ呼ぶ。</summary>
    internal void AttachLaunch(DebugLaunchViewModel launch)
    {
        _launch = launch;
        _launch.PropertyChanged += OnLaunchPropertyChanged;
        ApplySelectedProfileToLaunch();
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
            _launch.JustMyCode = m.JustMyCode;
            _launch.BreakOnAllExceptions = m.BreakOnAllExceptions;
            _launch.BreakOnUncaughtExceptions = m.BreakOnUncaughtExceptions;
            SelectedProject = string.IsNullOrEmpty(m.ProjectPath)
                ? DebugProjectDiscovery.AutoDetect
                : AvailableProjects.FirstOrDefault(p =>
                    string.Equals(p.RelativePath, m.ProjectPath, StringComparison.OrdinalIgnoreCase))
                  ?? DebugProjectDiscovery.AutoDetect;
        }
        finally { _applying = false; }
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
            JustMyCode = _launch.JustMyCode,
            BreakOnAllExceptions = _launch.BreakOnAllExceptions,
            BreakOnUncaughtExceptions = _launch.BreakOnUncaughtExceptions,
        };
        PersistAll();
    }

    private void OnWorkspaceRootChanged(object? sender, string? root)
        => _dispatcher.InvokeAsync(ReloadForWorkspace, DispatcherPriority.Background);

    /// <summary>ワークスペース（切替含む）に合わせてプロジェクト候補とプロファイル一覧を読み直す。
    /// 保存済みプロファイルが無ければ「既定」を1件シードする（＝これまでの自動検出のみの体験を維持）。</summary>
    private void ReloadForWorkspace()
    {
        var root = _workspace.RootPath;
        Profiles.Clear();
        AvailableProjects.Clear();
        AvailableProjects.Add(DebugProjectDiscovery.AutoDetect);

        if (root is null)
        {
            SelectedProfile = null;
            return;
        }

        foreach (var p in DebugProjectDiscovery.Discover(root).Where(p => !p.IsTest))
            AvailableProjects.Add(p);

        var (loaded, selectedId) = _store.Load(root);
        var seeded = loaded.Count == 0;
        var profiles = seeded ? new List<DebugLaunchProfile> { DebugLaunchProfile.CreateDefault("既定") } : loaded;
        foreach (var p in profiles) Profiles.Add(new DebugLaunchProfileItem(p));

        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles[0];
        if (seeded) PersistAll();
    }

    private void PersistAll()
    {
        var root = _workspace.RootPath;
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
                _launch.JustMyCode, _launch.BreakOnAllExceptions, _launch.BreakOnUncaughtExceptions);
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
