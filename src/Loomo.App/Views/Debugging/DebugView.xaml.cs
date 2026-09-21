using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>IDE（デバッグ）ペインのシェル。タブ（実行/問題/デバッグ/テスト/構成）を束ね、デバッグタブ内に
/// 変数・自動・コールスタック・スレッド・ブレークポイント・イミディエイト・モジュールを配置する。実行タブはプロジェクト一覧と出力を持ち、ここは出力コンソールの
/// ドキュメント追記と、停止/実行・実行系コマンド押下に応じたタブ自動切り替えだけを持つ。</summary>
public partial class DebugView : UserControl
{
    // 外側タブのインデックス（XAML の並び順と一致させる）。
    // 並び：実行0 / 問題1 / デバッグ2 / テスト3 / 構成4。
    private const int OutputTab = 0;
    private const int DebugTab = 2;
    private const int TestTab = 3;

    private DebugPaneLayoutController _projectPaneLayout = null!;
    private DebugLaunchListController<DebugViewModel, DebugProjectDiscovery.ProjectEntry> _projectLauncher = null!;
    private DebugInspectionNavigationController? _inspectionNavigation;
    private readonly DebugOutputConsoleController _outputConsole;

    public DebugView()
    {
        InitializeComponent();
        _outputConsole = new DebugOutputConsoleController(ConsoleBox,
            () =>
            {
                DebugTabs.SelectedIndex = DebugTab;
                InspectionTabs.SelectedIndex = 0;
            },
            () => DebugTabs.SelectedIndex = OutputTab);
        _projectPaneLayout = new DebugPaneLayoutController(
            ProjectPaneContent, ProjectSplitter, ProjectSplitterColumn, ProjectColumn,
            ProjectPaneRail, ProjectPaneToggle, "プロジェクト領域を展開", "プロジェクト領域を折りたたむ");
        _projectLauncher = new DebugLaunchListController<DebugViewModel, DebugProjectDiscovery.ProjectEntry>(
            () => DataContext as DebugViewModel, vm => vm.Launch.RunProjectCommand);
        _inspectionNavigation = new DebugInspectionNavigationController(
            DebugTabs,
            () => DebugTabs.SelectedIndex == TestTab,
            () => { if (DataContext is DebugViewModel vm) vm.Tests.EnsureTestsDiscovered(); },
            () => DataContext as DebugManagerViewModelBase);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => _outputConsole.Attach(DataContext as DebugManagerViewModelBase);

    private void OnClearOutputClick(object sender, RoutedEventArgs e) => _outputConsole.ClearOutput();

    // プロジェクト一覧のダブルクリック：行の ▶ と同じく、そのプロジェクトをデバッグ実行する。
    private void OnProjectDoubleClick(object sender, MouseButtonEventArgs e) => _projectLauncher.RunDoubleClickedEntry(e);

    private void OnProjectKeyDown(object sender, KeyEventArgs e) => _projectLauncher.RunSelectedEntryOnEnter(sender, e);

    private void OnProjectPaneToggleClick(object sender, RoutedEventArgs e) => _projectPaneLayout.Toggle();

    private void OnDebugTabChanged(object sender, SelectionChangedEventArgs e)
        => _inspectionNavigation?.OnOuterTabChanged(e);

    private void OnCallStackDoubleClick(object sender, MouseButtonEventArgs e)
        => _inspectionNavigation?.ActivateSelectedFrame(e);

    // インラインタブ（自動・コールスタック）の右クリック「コピー」。
    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);
}
