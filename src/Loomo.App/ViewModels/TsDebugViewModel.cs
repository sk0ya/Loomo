using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug.Js;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// TypeScript / Node.js 用 IDE（TS IDE）ペインのファサード ViewModel。dotnet 用の <see cref="DebugViewModel"/> と
/// 同じくマネージャ中核は <see cref="DebugManagerViewModelBase"/> が持ち、本クラスは TS 固有のサブ VM
/// （<see cref="Launch"/>=js-debug 起動＋tsc 型チェック / <see cref="Attach"/>=ポートアタッチ /
/// <see cref="Profiles"/>=package.json 起動構成）と対象解決（tsconfig ディレクトリ）だけを合成する。
/// プロファイルの保存先は dotnet と別ファイル（tsLaunchProfiles.json）。
/// </summary>
public sealed class TsDebugViewModel : DebugManagerViewModelBase
{
    private readonly IWorkspaceService _workspace;

    // --- TS 固有のサブ ViewModel（全セッション共有） ---
    public TsDebugAttachViewModel Attach { get; }
    public TsDebugLaunchViewModel Launch { get; }
    public DebugProfilesViewModel Profiles { get; }

    public TsDebugViewModel(JsDebugSessionFactory sessionFactory, IWorkspaceService workspace, ITerminalService terminal,
        DebugLaunchProfileStore profileStore)
        : base(sessionFactory, workspace)
    {
        _workspace = workspace;
        Attach = new TsDebugAttachViewModel(this);
        Profiles = new DebugProfilesViewModel(workspace, profileStore, TsProjectDiscovery.Discover);
        Launch = new TsDebugLaunchViewModel(this, workspace, terminal, Attach, Profiles);
        Profiles.AttachLaunch(Launch);
    }

    /// <summary>型チェック対象＝tsconfig.json のあるディレクトリを解決する。無ければ null（理由はコンソールへ）。</summary>
    public override string? FindBuildTarget()
    {
        var dir = TsDebugTargetResolver.FindTsconfigDir(_workspace.Folders);
        if (dir is null)
            Append(DebugOutputCategory.Important, "ワークスペースに tsconfig.json が見つかりません。");
        return dir;
    }

    public override void Dispose()
    {
        base.Dispose();
        Profiles.Dispose();
    }
}
