using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 左列下段の種類切替（タグ／リモート／サブモジュール）を、実際にビューを組んで確かめる。
/// 固定したいのは<b>選択中のボタンを押し直したときに外れない</b>こと——ToggleButton は Click が
/// 届く前に自分で IsChecked を反転させるので、何もしないと「ボタンは OFF・一覧はその種類のまま」に
/// なる（VM の値は変わらず通知も飛ばないので、片方向バインディングは二度と押し戻さない）。
/// </summary>
[Collection(WpfViewTests.Name)]
public sealed class GitReferenceTabViewTests
{
    private readonly WpfViewHost _host;

    public GitReferenceTabViewTests(WpfViewHost host) => _host = host;

    [Fact]
    public void 選択中のタブを押し直しても外れない()
    {
        _host.Run(() =>
        {
            var view = CreateView(CreateVm());
            var tags = FindTab(view, "Tags");
            Assert.True(tags.IsChecked);   // 既定はタグ

            // 実物の押下と同じ順番・同じ書き方：ToggleButton.OnToggle は SetCurrentValue で反転して
            // から Click を飛ばす。ここを素の代入にするとバインディング自体が外れてしまい、
            // 結び直しで直るかどうかを確かめられない（＝実物より壊れた状況を試すことになる）。
            tags.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            tags.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.True(tags.IsChecked);
        });
    }

    [Fact]
    public void 別のタブを押すと選択が移る()
    {
        _host.Run(() =>
        {
            var vm = CreateVm();
            var view = CreateView(vm);
            var tags = FindTab(view, "Tags");
            var remotes = FindTab(view, "Remotes");

            remotes.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            remotes.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(GitReferenceTab.Remotes, vm.ReferenceTab);
            Assert.True(remotes.IsChecked);
            Assert.False(tags.IsChecked);
        });
    }

    /// <summary>ビューを組んでレイアウトまで走らせる。レイアウト前はバインディングが
    /// <c>Unattached</c> のままで、押し直しの結び直しを確かめられない。</summary>
    private static GitSessionView CreateView(GitSessionViewModel vm)
    {
        var view = new GitSessionView { DataContext = vm };
        view.Measure(new Size(1200, 800));
        view.Arrange(new Rect(0, 0, 1200, 800));
        view.UpdateLayout();
        return view;
    }

    private static ToggleButton FindTab(GitSessionView view, string name) =>
        (ToggleButton)view.FindName($"RefTab{name}")!;

    private static GitSessionViewModel CreateVm()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomo-git-ref-tab-view", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        var git = new GitService(workspace);
        var query = new GitSessionQuery(git);
        return new GitSessionViewModel(git, new FakeEditorService(), query,
            new GitSessionCommandHandler(git), new GitHistoryViewModel(query),
            new GitRootSwitchViewModel(git, workspace));
    }
}
