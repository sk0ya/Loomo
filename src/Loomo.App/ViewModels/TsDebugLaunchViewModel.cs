using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug.Js;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>TypeScript / Node.js デバッグの起動・停止・再起動・ステップ実行・型チェック（tsc）を扱うサブ ViewModel。
/// dotnet 側の <see cref="DebugLaunchViewModel"/> に相当し、ヘッダの TS デバッグツールバーの窓口。
/// 実行対象は「プログラム（.ts/.js ファイル）」「npm スクリプト」「ブラウザ（Chrome）」の 3 モードで、プロファイル
/// （<see cref="ILaunchConfigurationOwner.TargetProgram"/>）には <see cref="TsLaunchTarget"/> の
/// <c>npm:スクリプト名</c> / <c>chrome:URL</c> エンコードで区別して格納する。未設定なら検出した既定スクリプト
/// （dev → start → 先頭）を、その<b>実体コマンドの分類</b>（<see cref="TsScriptClassifier"/>）で振り分けて既定にする
/// （<see cref="ApplyDefaultTargetIfEmpty"/>）：フロント開発サーバーはブラウザ（Chrome）モード、それ以外は npm モード。
/// フロントの「開始」は<b>可視ターミナルでサーバー起動 → ポート確定 → pwa-chrome</b>の複合起動になる
/// （<see cref="PrepareFrontendServerAsync"/>）。「開始」は必ず新しいセッションを作る。</summary>
public sealed partial class TsDebugLaunchViewModel : ObservableObject, ILaunchConfigurationOwner
{
    private readonly TsDebugViewModel _manager;
    private readonly IWorkspaceService _workspace;
    private readonly ITerminalService _terminal;
    private readonly IBrowserService _browser;
    private readonly TsDebugAttachViewModel _attach;
    private readonly DebugProfilesViewModel _profiles;

    /// <summary>実行対象（プロファイル格納形式）。ファイルパス、または <c>npm:スクリプト名</c>。</summary>
    [ObservableProperty] private string _targetProgram = "";

    /// <summary>開始前に tsc 型チェック（<c>npx tsc --noEmit</c>）を実行するか（エラーがあれば起動中止）。
    /// tsconfig.json が見つからないワークスペースでは何もしない。</summary>
    [ObservableProperty] private bool _buildFirst = true;

    /// <summary>プログラムへ渡すコマンドライン引数（空白区切り・二重引用符でグループ化）。npm モードでは未使用。</summary>
    [ObservableProperty] private string _launchArgs = "";

    /// <summary>起動時に追加する環境変数（1 行 1 件 <c>KEY=VALUE</c>）。</summary>
    [ObservableProperty] private string _launchEnv = "";

    /// <summary>自分のコードのみデバッグ（<c>&lt;node_internals&gt;</c> をスキップ）。次回起動から反映。</summary>
    [ObservableProperty] private bool _justMyCode = true;

    /// <summary>例外ブレーク：スローされたすべての例外で中断（js-debug フィルタ <c>all</c>）。</summary>
    [ObservableProperty] private bool _breakOnAllExceptions;

    /// <summary>例外ブレーク：キャッチされない例外で中断（js-debug フィルタ <c>uncaught</c>）。</summary>
    [ObservableProperty] private bool _breakOnUncaughtExceptions;

    /// <summary>選択中パッケージの npm スクリプト名一覧（構成タブのコンボ用）。</summary>
    public ObservableCollection<string> AvailableScripts { get; } = new();

    /// <summary>選択中パッケージの npm スクリプト一覧（名前＋実体コマンド。スクリプトタブの一覧用）。</summary>
    public ObservableCollection<TsScriptEntry> ScriptItems { get; } = new();

    /// <summary>スクリプトが 1 件以上あるか（スクリプトタブの空表示の出し分け）。</summary>
    public bool HasScripts => ScriptItems.Count > 0;

    /// <summary>ワークスペースに package.json が複数あるか（スクリプトタブのパッケージコンボの出し分け。
    /// 候補コレクションは先頭に「自動検出」センチネルを含むため 2 超で判定）。</summary>
    public bool HasMultiplePackages => _profiles.AvailableProjects.Count > 2;

    /// <summary>js-debug の導入コマンド（促しバーのボタン用）。node 自体は手動導入。</summary>
    public string AdapterInstallCommand => JsDebugAdapterLocator.InstallCommand;

    /// <summary>node が PATH 上にあるか（未導入バーの文言分岐に使う）。</summary>
    public bool IsNodeInstalled => JsDebugAdapterLocator.IsNodeInstalled;

    // --- 起動モード（TargetProgram のエンコードを構成タブ向けに分解した派生プロパティ） ---

    private enum LaunchMode { Program, Npm, Chrome }

    /// <summary>モード切替時に他モードの入力値を保持しておく（切替で消えないように）。</summary>
    private string _savedProgramPath = "";
    private string _savedNpmScript = "";
    private string _savedChromeUrl = "";

    private LaunchMode CurrentMode =>
        TsLaunchTarget.TryParseNpmScript(TargetProgram, out _) ? LaunchMode.Npm
        : TsLaunchTarget.TryParseChromeUrl(TargetProgram, out _) ? LaunchMode.Chrome
        : LaunchMode.Program;

    /// <summary>3 モードのラジオボタン用（true が来た側へ切り替える。false は無視——別ラジオの true が処理する）。</summary>
    public bool IsProgramMode { get => CurrentMode == LaunchMode.Program; set { if (value) SwitchTo(LaunchMode.Program); } }
    public bool IsNpmMode { get => CurrentMode == LaunchMode.Npm; set { if (value) SwitchTo(LaunchMode.Npm); } }
    public bool IsChromeMode { get => CurrentMode == LaunchMode.Chrome; set { if (value) SwitchTo(LaunchMode.Chrome); } }

    private void SwitchTo(LaunchMode mode)
    {
        if (mode == CurrentMode) return;
        SaveCurrentModeValue();
        TargetProgram = mode switch
        {
            LaunchMode.Npm => TsLaunchTarget.FormatNpmScript(string.IsNullOrEmpty(_savedNpmScript)
                ? TsProjectDiscovery.PickDefaultScript(AvailableScripts) ?? "start" : _savedNpmScript),
            LaunchMode.Chrome => TsLaunchTarget.FormatChromeUrl(string.IsNullOrEmpty(_savedChromeUrl)
                ? "http://localhost:5173" : _savedChromeUrl),
            _ => _savedProgramPath,
        };
    }

    private void SaveCurrentModeValue()
    {
        switch (CurrentMode)
        {
            case LaunchMode.Npm: TsLaunchTarget.TryParseNpmScript(TargetProgram, out _savedNpmScript); break;
            case LaunchMode.Chrome: TsLaunchTarget.TryParseChromeUrl(TargetProgram, out _savedChromeUrl); break;
            default: _savedProgramPath = TargetProgram; break;
        }
    }

    /// <summary>プログラムモードの実行ファイルパス（.ts/.js。ワークスペース相対可）。</summary>
    public string ProgramPath
    {
        get => CurrentMode == LaunchMode.Program ? TargetProgram : _savedProgramPath;
        set { if (CurrentMode == LaunchMode.Program) TargetProgram = value; else _savedProgramPath = value; }
    }

    /// <summary>npm モードのスクリプト名。</summary>
    public string NpmScript
    {
        get => TsLaunchTarget.TryParseNpmScript(TargetProgram, out var s) ? s : _savedNpmScript;
        set
        {
            if (CurrentMode == LaunchMode.Npm) TargetProgram = TsLaunchTarget.FormatNpmScript(value ?? "");
            else _savedNpmScript = value ?? "";
        }
    }

    /// <summary>ブラウザモードの URL（開発サーバー等。Chrome を js-debug が起動して接続する）。</summary>
    public string ChromeUrl
    {
        get => TsLaunchTarget.TryParseChromeUrl(TargetProgram, out var u) ? u : _savedChromeUrl;
        set
        {
            if (CurrentMode == LaunchMode.Chrome) TargetProgram = TsLaunchTarget.FormatChromeUrl(value ?? "");
            else _savedChromeUrl = value ?? "";
        }
    }

    partial void OnTargetProgramChanged(string value)
    {
        OnPropertyChanged(nameof(IsNpmMode));
        OnPropertyChanged(nameof(IsProgramMode));
        OnPropertyChanged(nameof(IsChromeMode));
        OnPropertyChanged(nameof(ProgramPath));
        OnPropertyChanged(nameof(NpmScript));
        OnPropertyChanged(nameof(ChromeUrl));
    }

    internal TsDebugLaunchViewModel(TsDebugViewModel manager, IWorkspaceService workspace, ITerminalService terminal,
        IBrowserService browser, TsDebugAttachViewModel attach, DebugProfilesViewModel profiles)
    {
        _manager = manager;
        _workspace = workspace;
        _terminal = terminal;
        _browser = browser;
        _attach = attach;
        _profiles = profiles;
        _manager.SessionStateChanged += OnSessionStateChanged;
        _profiles.PropertyChanged += OnProfilesPropertyChanged;
        ReloadScripts();
    }

    private void OnSessionStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        RunScriptCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        StepOverCommand.NotifyCanExecuteChanged();
        StepIntoCommand.NotifyCanExecuteChanged();
        StepOutCommand.NotifyCanExecuteChanged();
        TypeCheckCommand.NotifyCanExecuteChanged();
    }

    private void OnProfilesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SelectedProfile はワークスペース切替（ReloadForWorkspace）とプロファイル切替の両方で変わる。
        // どちらもスクリプト候補を読み直し、実行対象が未設定なら npm 既定を適用する。
        if (e.PropertyName is nameof(DebugProfilesViewModel.SelectedProject)
            or nameof(DebugProfilesViewModel.SelectedProfile))
        {
            ReloadScripts();
            ApplyDefaultTargetIfEmpty();
        }
    }

    /// <summary>実行対象が未設定（新規の「既定」プロファイル等）なら、既定スクリプト（dev → start → 先頭）を選び、
    /// その<b>実体コマンドの分類</b>で振り分けて既定にする：フロント開発サーバーなら Chrome モード
    /// （URL はフレームワークの既定ポート）、それ以外は npm モード。候補が無ければ何もしない。</summary>
    internal void ApplyDefaultTargetIfEmpty()
    {
        if (!string.IsNullOrWhiteSpace(TargetProgram)) return;
        if (PickDefaultScriptEntry() is not { } entry) return;

        if (entry.Kind == TsScriptKind.FrontendDevServer)
        {
            var port = TsScriptClassifier.DefaultPort(TsScriptClassifier.DetectFramework(entry.Command));
            TargetProgram = TsLaunchTarget.FormatChromeUrl($"http://localhost:{port}");
            _savedNpmScript = entry.Name;   // モードを npm に戻したとき元スクリプトが出るように・複合起動のサーバー指定に使う
        }
        else
        {
            TargetProgram = TsLaunchTarget.FormatNpmScript(entry.Name);
        }
    }

    /// <summary>既定スクリプト（dev → start → 先頭）を実体コマンド付きで選ぶ。候補が空なら null。</summary>
    private TsScriptEntry? PickDefaultScriptEntry()
        => ScriptItems.Count == 0 ? null
        : ScriptItems.FirstOrDefault(s => s.Name == "dev")
        ?? ScriptItems.FirstOrDefault(s => s.Name == "start")
        ?? ScriptItems[0];

    /// <summary>複合起動で実際に走らせるフロント開発サーバースクリプトを選ぶ。直近の意図（<see cref="_savedNpmScript"/>）が
    /// フロント種ならそれを優先し、無ければ dev → start → 最初のフロント種。フロント種が無ければ null（＝複合しない）。</summary>
    private TsScriptEntry? PickFrontendDevScript()
    {
        var frontend = ScriptItems.Where(s => s.Kind == TsScriptKind.FrontendDevServer).ToList();
        if (frontend.Count == 0) return null;
        if (!string.IsNullOrEmpty(_savedNpmScript) &&
            frontend.FirstOrDefault(s => s.Name == _savedNpmScript) is { } hinted)
            return hinted;
        return frontend.FirstOrDefault(s => s.Name == "dev")
            ?? frontend.FirstOrDefault(s => s.Name == "start")
            ?? frontend[0];
    }

    /// <summary>選択中パッケージ（未選択なら最初に見つかった package.json）の npm スクリプト一覧を読み直す。</summary>
    internal void ReloadScripts()
    {
        AvailableScripts.Clear();
        ScriptItems.Clear();
        if (SelectedPackageJsonPath() is { } pkg)
        {
            foreach (var e in TsProjectDiscovery.ReadScriptEntries(pkg))
            {
                AvailableScripts.Add(e.Name);
                ScriptItems.Add(e);
            }
        }
        OnPropertyChanged(nameof(HasScripts));
        OnPropertyChanged(nameof(HasMultiplePackages));
    }

    /// <summary>選択中パッケージの package.json 絶対パス（自動検出なら最初の候補）。無ければ null。</summary>
    private string? SelectedPackageJsonPath()
    {
        if (_profiles.SelectedProjectPath is { } explicitPath) return explicitPath;
        foreach (var folder in _workspace.Folders)
        {
            var first = TsProjectDiscovery.Discover(folder).FirstOrDefault();
            if (first is not null) return first.FullPath;
        }
        return null;
    }

    partial void OnBreakOnAllExceptionsChanged(bool value) => _ = ApplyExceptionFiltersAsync();
    partial void OnBreakOnUncaughtExceptionsChanged(bool value) => _ = ApplyExceptionFiltersAsync();

    private IReadOnlyList<string> CurrentExceptionFilterIds()
    {
        var ids = new List<string>();
        if (BreakOnAllExceptions) ids.Add("all");
        if (BreakOnUncaughtExceptions) ids.Add("uncaught");
        return ids;
    }

    private Task ApplyExceptionFiltersAsync()
    {
        var ids = CurrentExceptionFilterIds();
        return Task.WhenAll(_manager.Sessions.Select(s => s.DebugService.SetExceptionBreakpointsAsync(ids, CancellationToken.None)));
    }

    private bool CanStep() => _manager.IsStopped;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task Continue() => ActiveDebugServiceOrNull()?.ContinueAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepOver() => ActiveDebugServiceOrNull()?.StepOverAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepInto() => ActiveDebugServiceOrNull()?.StepInAsync() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepOut() => ActiveDebugServiceOrNull()?.StepOutAsync() ?? Task.CompletedTask;

    private bool CanPause() => _manager.IsBusy && !_manager.IsStopped;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task Pause() => ActiveDebugServiceOrNull()?.PauseAsync() ?? Task.CompletedTask;

    private bool CanRestart() => _manager.IsBusy;

    /// <summary>アクティブなセッションを停止して、同じ対象で再起動する（直前が launch なら再 launch、
    /// attach なら同じポートへ再 attach）。新しいセッションは作らない。</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task Restart()
    {
        var session = _manager.ActiveSession;
        if (session is null) return;
        await session.DebugService.StopAsync();
        if (session.Kind == DebugSessionKind.Attach)
            await _attach.RelaunchIntoAsync(session);
        else
            await RelaunchIntoAsync(session);
    }

    private bool CanStart() => !_manager.IsTaskRunning;

    /// <summary>デバッグを開始する。既存セッションは止めず、常に新しいセッションを作って始める。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _manager.RequestOutput();  // 押下時に即「出力」へ
        _manager.Refresh();
        if (_manager.IsAdapterMissing)
        {
            _manager.StatusMessage = "アダプタ未導入";
            _manager.Append(DebugOutputCategory.Important, IsNodeInstalled
                ? "デバッグアダプタ（vscode-js-debug）が未導入です。下のバーから導入できます。"
                : "Node.js（node）が PATH 上に見つかりません。Node.js をインストールしてください。");
            return;
        }

        if (!await PreflightTypeCheckAsync()) return;

        var target = ResolveTarget();
        if (target is null) return;

        // フロント（Chrome）は複合起動：可視ターミナルでサーバーを立て、ペインを dev URL へ出し、そのペインへ CDP アタッチ。
        if (await PrepareFrontendServerAsync(target) is not { } prepared) return;

        var session = _manager.CreateSession(BuildDisplayName(prepared.Program), DebugSessionKind.Launch);
        await session.DebugService.SetExceptionBreakpointsAsync(CurrentExceptionFilterIds(), CancellationToken.None);
        await LaunchIntoAsync(session, prepared.Program, prepared.BrowserDebugPort);
    }

    /// <summary>スクリプトタブの行実行。実行対象をそのスクリプト（<c>npm:名前</c>）へ切り替えてから開始する
    /// （プロファイルにも保存されるので、以降ヘッダの「▶ 開始」は同じスクリプトの再実行になる）。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RunScript(TsScriptEntry? entry)
    {
        if (entry is null) return Task.CompletedTask;
        // 既定選択と同じ振り分け：フロント開発サーバーは Chrome（複合起動）、それ以外は npm（pwa-node）。
        if (entry.Kind == TsScriptKind.FrontendDevServer)
        {
            var port = TsScriptClassifier.DefaultPort(TsScriptClassifier.DetectFramework(entry.Command));
            _savedNpmScript = entry.Name;   // 複合起動で走らせるサーバースクリプトの指定
            TargetProgram = TsLaunchTarget.FormatChromeUrl($"http://localhost:{port}");
        }
        else
        {
            TargetProgram = TsLaunchTarget.FormatNpmScript(entry.Name);
        }
        return StartAsync();
    }

    /// <summary>同じ対象で、既存セッション（同じタブ）へ再度 launch する（Restart 用）。</summary>
    private async Task RelaunchIntoAsync(DebugSessionViewModel session)
    {
        _manager.RequestOutput();
        if (!await PreflightTypeCheckAsync()) return;
        var target = ResolveTarget();
        if (target is null) return;
        if (await PrepareFrontendServerAsync(target) is not { } prepared) return;
        await LaunchIntoAsync(session, prepared.Program, prepared.BrowserDebugPort);
    }

    /// <summary>実行対象（プロファイル格納形式のまま）を検証・解決する。プログラムモードは相対パスを
    /// ワークスペース基準で絶対化する。解決できなければ null（理由はコンソールへ）。</summary>
    private string? ResolveTarget()
    {
        if (TsLaunchTarget.TryParseNpmScript(TargetProgram, out var script))
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                _manager.Append(DebugOutputCategory.Important, "npm スクリプト名を指定してください。");
                return null;
            }
            return TargetProgram;
        }

        if (TsLaunchTarget.TryParseChromeUrl(TargetProgram, out var url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                _manager.Append(DebugOutputCategory.Important, $"URL を指定してください（例: http://localhost:5173）。");
                return null;
            }
            return TargetProgram;
        }

        var path = TargetProgram.Trim();
        if (path.Length == 0)
        {
            _manager.Append(DebugOutputCategory.Important,
                "実行対象を指定してください（.ts/.js ファイル、または npm スクリプト）。");
            return null;
        }
        if (!Path.IsPathRooted(path) && _workspace.RootPath is { } root)
            path = Path.GetFullPath(Path.Combine(root, path));
        if (!File.Exists(path))
        {
            _manager.Append(DebugOutputCategory.Important, $"実行対象が見つかりません: {path}");
            return null;
        }
        return path;
    }

    private async Task LaunchIntoAsync(DebugSessionViewModel session, string program, int? browserDebugPort = null)
    {
        var iSession = (IDebugSession)session;
        var token = iSession.BeginSession();
        try
        {
            await session.DebugService.StartAsync(
                new DebugLaunchConfig(program, ResolveWorkingDirectory(program),
                    Args: DebugLaunchArgs.ParseArgs(LaunchArgs),
                    Environment: DebugLaunchArgs.ParseEnv(LaunchEnv),
                    JustMyCode: JustMyCode,
                    BrowserDebugPort: browserDebugPort),
                token);
        }
        catch (OperationCanceledException) { /* 停止操作 */ }
        catch (Exception ex)
        {
            iSession.Append(DebugOutputCategory.Important, $"デバッグ起動でエラー: {ex.Message}");
        }
    }

    /// <summary>直近の複合起動で立てた開発サーバーのポート（0=未起動）。同じスクリプトで生きていれば再利用する。</summary>
    private int _devServerPort;
    /// <summary>そのとき走らせた npm スクリプト名（再利用判定用）。</summary>
    private string? _devServerScript;

    /// <summary>複合起動の解決結果：実効ターゲット（ポート差し替え済み <c>chrome:URL</c> など）と、
    /// 可視ブラウザペインへ CDP アタッチする場合のポート（外部 Chrome フォールバック時は null）。</summary>
    private readonly record struct PreparedTarget(string Program, int? BrowserDebugPort);

    /// <summary>フロント（Chrome）ターゲットの複合起動準備。①可視ターミナルでフロント開発サーバーを立て
    /// （ポートは空きを選んで<b>固定注入</b>＝vite の自動ポートずらしを封じる）→ ②listen したら<b>可視ブラウザペインを
    /// その URL へ出す</b>（CDP アタッチ対象のページを作る）→ ③ペインの CDP ポートを付けて返す（＝そのペインを
    /// デバッグ）。Chrome 以外・複合対象外はそのまま返す（ポート null）。ペインを出せなければ外部 Chrome へフォールバック
    /// （ポート null）。サーバー起動失敗のみ null（呼び出し側は起動中止）。</summary>
    private async Task<PreparedTarget?> PrepareFrontendServerAsync(string program)
    {
        if (!TsLaunchTarget.TryParseChromeUrl(program, out var url)) return new PreparedTarget(program, null); // Chrome 以外
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new PreparedTarget(program, null);

        // このパッケージのフロント開発サーバースクリプトを探す。無ければ複合しない（外部サーバー前提で Chrome だけ）。
        if (PickFrontendDevScript() is not { } devScript)
            return new PreparedTarget(program, await ShowInPaneAsync(url));

        var framework = TsScriptClassifier.DetectFramework(devScript.Command);
        var basePort = uri.Port > 0 ? uri.Port : TsScriptClassifier.DefaultPort(framework);

        // 前回の複合サーバーが同じスクリプトでまだ生きていれば再利用（二重起動を避ける）。
        if (_devServerPort > 0 && _devServerScript == devScript.Name &&
            await DevServerPortUtil.IsListeningAsync(_devServerPort, CancellationToken.None))
        {
            var reuseUrl = WithPort(uri, _devServerPort);
            return new PreparedTarget(TsLaunchTarget.FormatChromeUrl(reuseUrl), await ShowInPaneAsync(reuseUrl));
        }

        // 空きポートを選び、フレームワーク方言でポート固定注入。方言が無ければ注入せず URL のポートを信じる（P1）。
        var pin = TsScriptClassifier.PinnedPortArgs(framework, DevServerPortUtil.FindFreePort(basePort));
        var port = pin.Length > 0 ? ExtractPort(pin, basePort) : basePort;
        var dir = PreferredPackageDir() ?? _workspace.RootPath;
        var command = pin.Length > 0
            ? $"Set-Location \"{dir}\"; npm run {devScript.Name} -- {pin}"
            : $"Set-Location \"{dir}\"; npm run {devScript.Name}";

        _manager.Append(DebugOutputCategory.Important, $"開発サーバーを起動: npm run {devScript.Name}（localhost:{port}）");
        if (!_terminal.TryRunInVisibleTerminal(command))
        {
            _manager.Append(DebugOutputCategory.Important,
                "可視ターミナルが接続されていないため開発サーバーを起動できません。ターミナルを開いてから再試行してください。");
            return null;
        }

        _manager.Append(DebugOutputCategory.Important, $"サーバーの応答を待っています（localhost:{port}）…");
        if (!await DevServerPortUtil.WaitUntilListeningAsync(port, timeoutMs: 30000, CancellationToken.None))
        {
            _manager.Append(DebugOutputCategory.Important,
                $"開発サーバーが localhost:{port} で応答しませんでした（起動失敗かポート競合）。ターミナルの出力を確認してください。");
            return null;
        }

        _devServerPort = port;
        _devServerScript = devScript.Name;
        var effectiveUrl = WithPort(uri, port);
        return new PreparedTarget(TsLaunchTarget.FormatChromeUrl(effectiveUrl), await ShowInPaneAsync(effectiveUrl));
    }

    /// <summary>可視ブラウザペインを URL へ出し、成功したら CDP アタッチ用ポート（WebView2 のリモートデバッグポート）を返す。
    /// ペインが使えなければ null＝外部 Chrome launch へフォールバック。</summary>
    private async Task<int?> ShowInPaneAsync(string url)
    {
        try
        {
            await _browser.ShowAndNavigateAsync(url, CancellationToken.None);
            _manager.Append(DebugOutputCategory.Important, $"ブラウザペインに表示: {url}（CDP:{WebViewDebugPort.Port} でアタッチ）");
            return WebViewDebugPort.Port;
        }
        catch (Exception ex)
        {
            _manager.Append(DebugOutputCategory.Important,
                $"ブラウザペインを開けませんでした（外部 Chrome で起動します）: {ex.Message}");
            return null;
        }
    }

    /// <summary>ポート固定注入文字列（"--port 5175 --strictPort" 等）から実効ポートを取り出す。無ければ既定。</summary>
    private static int ExtractPort(string pinArgs, int fallback)
    {
        var m = System.Text.RegularExpressions.Regex.Match(pinArgs, @"--port\s+(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? p : fallback;
    }

    /// <summary>URL のポートだけ差し替えて文字列化する。</summary>
    private static string WithPort(Uri uri, int port) => new UriBuilder(uri) { Port = port }.Uri.ToString();

    /// <summary>作業ディレクトリ＝選択中パッケージのディレクトリ（npm run の実行場所・Chrome の webRoot）。
    /// 未選択ならプログラムのあるディレクトリ、それも無ければワークスペースルート。</summary>
    private string? ResolveWorkingDirectory(string program)
    {
        if (SelectedPackageJsonPath() is { } pkg) return Path.GetDirectoryName(pkg);
        if (!TsLaunchTarget.TryParseNpmScript(program, out _) && !TsLaunchTarget.TryParseChromeUrl(program, out _))
            return Path.GetDirectoryName(program);
        return _workspace.RootPath;
    }

    private static string BuildDisplayName(string program)
        => TsLaunchTarget.TryParseNpmScript(program, out var script) ? $"npm run {script}"
        : TsLaunchTarget.TryParseChromeUrl(program, out var url) ? url
        : Path.GetFileNameWithoutExtension(program);

    private bool CanStop() => _manager.IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        var session = _manager.ActiveSession;
        if (session is null) return;
        ((IDebugSession)session).CancelSession();
        await session.DebugService.StopAsync();
    }

    private bool CanRunTask() => !_manager.IsTaskRunning;

    /// <summary>tsc 型チェックを実行する（デバッグ起動とは独立した手動実行。dotnet 側の「ビルド」に相当）。
    /// 出力はコンソールへ、診断は「問題」タブへ、結果はステータスへ。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTask))]
    private async Task TypeCheck()
    {
        var dir = _manager.FindBuildTarget();
        if (dir is null) return;

        _manager.RequestOutput();  // 押下時に即「出力」へ
        _manager.IsTaskRunning = true;
        try
        {
            await RunTypeCheckAsync(dir);
        }
        finally { _manager.IsTaskRunning = false; }
    }

    /// <summary>起動前の型チェック（<see cref="BuildFirst"/>）。エラーがあれば起動を中止する（false）。
    /// tsconfig.json が見つからなければ何もせず続行（true）。</summary>
    private async Task<bool> PreflightTypeCheckAsync()
    {
        if (!BuildFirst) return true;
        var dir = TsDebugTargetResolver.FindTsconfigDir(_workspace.Folders, PreferredPackageDir());
        if (dir is null) return true;   // tsconfig 無し＝型チェックの対象外（素の JS など）

        _manager.IsTaskRunning = true;
        try
        {
            return await RunTypeCheckAsync(dir);
        }
        finally { _manager.IsTaskRunning = false; }
    }

    /// <summary>tsc 型チェックの実体。成功（エラーなし）で true。</summary>
    private async Task<bool> RunTypeCheckAsync(string dir)
    {
        _manager.StatusMessage = "型チェック中…";
        _manager.Append(DebugOutputCategory.Important, $"型チェック: {dir}");
        // tsc の診断パスは cwd 相対のため、Set-Location してから実行し dir を基準に絶対化する。
        var result = await _terminal.RunCommandAsync(
            $"Set-Location \"{dir}\"; npx tsc --noEmit --pretty false", CancellationToken.None);
        _manager.WriteConsole(result.Output);
        _manager.ReportBuildOutput(result.Output, baseDir: dir);
        _manager.StatusMessage = result.Success ? "型チェック成功" : $"型エラーあり（{result.ExitCode}）";
        _manager.Append(DebugOutputCategory.Important,
            result.Success ? "型チェックに成功しました。" : "型チェックでエラーが見つかりました（「問題」タブ参照）。");
        return result.Success;
    }

    /// <summary>選択中パッケージのディレクトリ（tsconfig 探索の優先場所）。</summary>
    private string? PreferredPackageDir()
        => SelectedPackageJsonPath() is { } pkg ? Path.GetDirectoryName(pkg) : null;

    /// <summary>促しバーの「インストール」。js-debug の導入コマンドを見えるターミナルで実行する。</summary>
    [RelayCommand]
    private void InstallAdapter()
    {
        if (IsNodeInstalled)
            _terminal.TryRunInVisibleTerminal(AdapterInstallCommand);
    }

    private IDebugService? ActiveDebugServiceOrNull() => _manager.ActiveSession?.DebugService;

    // --- エディタの実行系操作（右クリックメニュー。アクティブなセッションに対して行う） ---

    /// <summary>アダプタが「次のステートメントに設定」に対応しているか（js-debug は非対応 → false）。</summary>
    public bool SupportsSetNextStatement => ActiveDebugServiceOrNull()?.SupportsSetNextStatement ?? false;

    /// <summary>カーソル行（0 始まり）まで実行する（一時ブレークポイントを置いて続行）。停止中のみ有効。</summary>
    public Task RunToCursorAsync(string sourcePath, int line0)
    {
        var session = _manager.ActiveSession;
        return session is { IsStopped: true }
            ? session.DebugService.RunToCursorAsync(sourcePath, line0 + 1, CancellationToken.None)  // エディタ0始まり → DAP1始まり
            : Task.CompletedTask;
    }
}
