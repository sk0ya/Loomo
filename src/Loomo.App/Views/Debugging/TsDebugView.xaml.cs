using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>TS IDE（TypeScript / Node.js デバッグ）ペインのシェル。dotnet 用 <see cref="DebugView"/> の
/// クローンで、普段の「実行」（スクリプト一覧＋出力）と、デバッグ中だけ現れる検査タブ、問題/テスト/構成を
/// 束ねる。ここは出力コンソールのドキュメント追記と、停止/実行・実行系コマンド押下に応じたタブ自動切り替え
/// だけを持つ。DataContext は <see cref="TsDebugViewModel"/>（基底 <see cref="DebugManagerViewModelBase"/>
/// 経由で扱う）。</summary>
public partial class TsDebugView : UserControl
{
    private DebugPaneLayoutController _scriptPaneLayout = null!;
    private DebugLaunchListController<TsDebugViewModel, TsScriptEntry> _scriptLauncher = null!;
    private DebugInspectionNavigationController? _inspectionNavigation;
    private readonly DebugOutputConsoleController _outputConsole;

    public TsDebugView()
    {
        InitializeComponent();
        _outputConsole = new DebugOutputConsoleController(ConsoleBox,
            () =>
            {
                SelectTab(DebugTabItem);
                InspectionTabs.SelectedItem = VariablesTabItem;
            },
            () => SelectTab(ExecutionTab));
        _scriptPaneLayout = new DebugPaneLayoutController(
            ScriptPaneContent, ScriptSplitter, ScriptSplitterColumn, ScriptColumn,
            ScriptPaneRail, ScriptPaneToggle, "スクリプト領域を展開", "スクリプト領域を折りたたむ");
        _scriptLauncher = new DebugLaunchListController<TsDebugViewModel, TsScriptEntry>(
            () => DataContext as TsDebugViewModel, vm => vm.Launch.RunScriptCommand);
        _inspectionNavigation = new DebugInspectionNavigationController(
            DebugTabs,
            () => ReferenceEquals(DebugTabs.SelectedItem, TestsTabItem),
            () => { if (DataContext is TsDebugViewModel vm) vm.Tests.EnsureTestsDiscovered(); },
            () => DataContext as DebugManagerViewModelBase);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => _outputConsole.Attach(DataContext as DebugManagerViewModelBase);

    private void SelectTab(TabItem tab)
    {
        if (tab.Visibility == Visibility.Visible)
            DebugTabs.SelectedItem = tab;
    }

    private void OnDebugTabChanged(object sender, SelectionChangedEventArgs e)
        => _inspectionNavigation?.OnOuterTabChanged(e);

    private void OnCallStackDoubleClick(object sender, MouseButtonEventArgs e)
        => _inspectionNavigation?.ActivateSelectedFrame(e);

    // スクリプトタブのダブルクリック：その行のスクリプトをデバッグ実行（行の ▶ ボタンと同じ）。
    // 余白のダブルクリックでは発火させない（行＝ListBoxItem 上のときだけ）。
    private void OnScriptDoubleClick(object sender, MouseButtonEventArgs e) => _scriptLauncher.RunDoubleClickedEntry(e);

    private void OnScriptKeyDown(object sender, KeyEventArgs e) => _scriptLauncher.RunSelectedEntryOnEnter(sender, e);

    // インラインタブ（自動・コールスタック）の右クリック「コピー」。
    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);

    private void OnClearOutputClick(object sender, RoutedEventArgs e) => _outputConsole.ClearOutput();

    private void OnScriptPaneToggleClick(object sender, RoutedEventArgs e) => _scriptPaneLayout.Toggle();
}
