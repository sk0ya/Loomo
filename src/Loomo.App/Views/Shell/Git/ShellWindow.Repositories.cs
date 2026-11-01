using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// リポジトリの印（§40.7・<see cref="RepositoryChipViewModel"/>）から頼まれた、画面の切替が要る操作の受け手。
/// fetch／pull は <see cref="RepositoryActions"/> 自身が実行するのでここには来ない。
/// <para>印を押すのは<b>そのリポジトリを指した明示の操作</b>なので、Git の対象をそのリポジトリへ切り替えてよい
/// （§40.3 規約4 が禁じるのは、ブレームを開いただけで黙って入れ替わる類の暗黙の切替）。</para>
/// </summary>
public partial class ShellWindow {
    private void OnRepositoryActionRequested(object? sender, RepositoryActionRequest request) {
        switch (request.Kind) {
            case RepositoryActionKind.SwitchBranch:
                SelectGitRepository(request.RepositoryRoot);
                TogglePopup(BranchPopup, () => {
                    _vm.GitSession.EnsureLoaded();
                    BranchSwitcher.PrepareForOpen();
                });
                break;
            case RepositoryActionKind.OpenChanges:
                SelectGitRepository(request.RepositoryRoot);
                _vm.RevealGitPanel();
                break;
            case RepositoryActionKind.OpenLog:
                SelectGitRepository(request.RepositoryRoot);
                _vm.GitSession.EnsureLoaded();
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
                FocusPane(PaneKind.Git);
                break;
            case RepositoryActionKind.OpenTerminal:
                OnSetInTerminalRequested(this, new TerminalSetRequest(request.RepositoryRoot, IsDirectory: true));
                break;
            case RepositoryActionKind.OpenInFiles:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Files);
                _vm.Files.Reveal(request.RepositoryRoot);
                FocusPane(PaneKind.Files);
                break;
        }
    }

    /// <summary>Git パネル・Git ペインの対象をそのリポジトリへ（明示の操作から）。</summary>
    private void SelectGitRepository(string repositoryRoot) => _git.SetActiveRoot(repositoryRoot);
}
