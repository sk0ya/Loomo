using System;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Testing;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// dotnet（C#）用 IDE（デバッグ）ペインのファサード ViewModel。マネージャ中核（セッション管理・出力・
/// エディタ連携・問題・ブレークポイント）は <see cref="DebugManagerViewModelBase"/> が持ち、本クラスは
/// dotnet 固有のサブ VM（<see cref="Launch"/>=dotnet build 起動 / <see cref="Attach"/>=coreclr プロセス /
/// <see cref="Tests"/>=dotnet test / <see cref="Profiles"/>=csproj 起動構成）と対象解決
/// （<see cref="DebugTargetResolver"/>）だけを合成する。
/// </summary>
public sealed class DebugViewModel : DebugManagerViewModelBase
{
    private readonly Func<string?> _findBuildTarget;

    // --- dotnet 固有のサブ ViewModel（全セッション共有） ---
    public DebugAttachViewModel Attach { get; }
    public DebugTestsViewModel Tests { get; }
    public DebugLaunchViewModel Launch { get; }
    public DebugProfilesViewModel Profiles { get; }

    public DebugViewModel(IDebugSessionFactory sessionFactory, IWorkspaceService workspace, ITerminalService terminal,
        ITestDiscoveryService testDiscovery, DebugLaunchProfileStore profileStore,
        ISolutionModelService? solutionModel = null, IBrowserService? browser = null)
        : base(sessionFactory, workspace)
    {
        Attach = new DebugAttachViewModel(this);
        Tests = new DebugTestsViewModel(workspace, terminal, testDiscovery, this, solutionModel);
        Profiles = new DebugProfilesViewModel(workspace, profileStore);
        Launch = new DebugLaunchViewModel(this, workspace, terminal, Attach, Profiles, solutionModel, browser);
        Profiles.AttachLaunch(Launch);
        _findBuildTarget = () => DebugTargetResolver.FindBuildTarget(workspace, this);
    }

    /// <summary>ビルド/テスト対象（.sln 優先、無ければ最初の .csproj）を解決する。無ければ null（理由はコンソールへ）。</summary>
    public override string? FindBuildTarget() => _findBuildTarget();

    /// <summary>C#テストデバッグ用testhostを、通常のattachと同じ新規セッションへ接続する。</summary>
    internal async Task<DebugSessionViewModel?> AttachTestProcessAsync(int processId, string displayName)
    {
        if (processId <= 0 || IsBusy || IsTaskRunning) return null;
        await Attach.AttachToNewSessionAsync(
            new DebugProcessViewModel(processId, "testhost", displayName, isManaged: true));
        return ActiveSession is { } session && session.DebugService.State is DebugSessionState.Launching
            or DebugSessionState.Running or DebugSessionState.Stopped
            ? session : null;
    }

    public override void Dispose()
    {
        Launch.DisposeIisExpress();
        base.Dispose();
        Tests.Dispose();
        Profiles.Dispose();
    }
}
