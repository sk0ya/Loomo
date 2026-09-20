using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// <see cref="DiffSessionViewModel"/> をもう1つ立てるための生成器。
///
/// <para>DIFF ペインの VM は Singleton（部屋がひとつ持っている表示状態）なので、そこへ別の対象を
/// 流し込むと<b>ペインで見ていた差分が奪われる</b>。切り離しウィンドウで別のコミット・別のファイルを
/// 並べて見たいときは、ペインとは独立した VM が要る——ここがその入口。</para>
///
/// <para>渡す部品（git・比較基準・ゲートウェイ）は Singleton のまま共有する。比較基準を共有するのは
/// 意図どおりで、部屋の「何と比べているか」はひとつだから（DI 登録のコメントも同じ判断）。
/// 立てた VM は共有 Singleton を購読するので、窓を閉じるときに <see cref="DiffSessionViewModel.Dispose"/>
/// を必ず呼ぶこと。</para>
/// </summary>
public sealed class DiffSessionFactory
{
    private readonly GitService _git;
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;
    private readonly DiffFileGateway _files;
    private readonly DiffSessionQuery _query;
    private readonly DiffSessionCommandHandler _commands;
    private readonly LoomoSettings _settings;
    private readonly GitCompareBaseViewModel _compareBase;

    public DiffSessionFactory(
        GitService git, IEditorService editor, IWorkspaceService workspace,
        DiffFileGateway files, DiffSessionQuery query, DiffSessionCommandHandler commands,
        LoomoSettings settings, GitCompareBaseViewModel compareBase)
    {
        _git = git;
        _editor = editor;
        _workspace = workspace;
        _files = files;
        _query = query;
        _commands = commands;
        _settings = settings;
        _compareBase = compareBase;
    }

    public DiffSessionViewModel Create() =>
        new(_git, _editor, _workspace, _files, _query, _commands, _settings, _compareBase);
}
