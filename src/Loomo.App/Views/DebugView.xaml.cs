using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Debug;

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

    private INotifyCollectionChanged? _observed;
    private DebugViewModel? _vm;
    private bool _projectPaneExpanded = true;
    private double _expandedProjectPaneWidth = 220;

    public DebugView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null) _observed.CollectionChanged -= OnOutputChanged;
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.OutputRequested -= OnOutputRequested;
        }
        if (DataContext is DebugViewModel vm)
        {
            _observed = vm.Output;
            _observed.CollectionChanged += OnOutputChanged;
            _vm = vm;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.OutputRequested += OnOutputRequested;
        }
        RebuildConsole();
    }

    // Output コレクションを RichTextBox のドキュメントへ写し直す（DataContext 差し替え時）。
    private void RebuildConsole()
    {
        ConsoleBox.Document.Blocks.Clear();
        if (_vm is null) return;
        foreach (var line in _vm.Output) AppendConsoleLine(line);
        ConsoleBox.ScrollToEnd();
    }

    // 1 行を色分け（Category）した段落として末尾へ追加する。色はテーマ追従（SetResourceReference）。
    private void AppendConsoleLine(DebugOutputLine line)
    {
        var run = new Run(line.Text);
        switch (line.Category)
        {
            case DebugOutputCategory.Stderr:
                run.SetResourceReference(TextElement.ForegroundProperty, "DebugStderr");
                break;
            case DebugOutputCategory.Console:
                run.SetResourceReference(TextElement.ForegroundProperty, "FgDim");
                break;
            case DebugOutputCategory.Important:
                run.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                run.FontWeight = FontWeights.SemiBold;
                break;
            default:
                run.SetResourceReference(TextElement.ForegroundProperty, "Fg");
                break;
        }
        ConsoleBox.Document.Blocks.Add(new Paragraph(run) { Margin = new Thickness(0) });
    }

    // ブレークポイント等で停止したら「デバッグ＞変数」へ、続行したら「実行」へ自動で切り替える。
    // 開始/ビルド/テスト押下時の「出力」表示は OutputRequested（押下と同期）で行う。
    // Output はセッション切替で参照先の ObservableCollection ごと差し替わるので、そのたびに購読と表示を作り直す。
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DebugViewModel.IsStopped) && _vm is not null)
        {
            if (_vm.IsStopped)
            {
                DebugTabs.SelectedIndex = DebugTab;
                InspectionTabs.SelectedIndex = 0;
            }
            else
                DebugTabs.SelectedIndex = OutputTab;
        }

        if (e.PropertyName == nameof(DebugViewModel.IsBusy) && _vm is not null && !_vm.IsBusy)
            DebugTabs.SelectedIndex = OutputTab;

        if (e.PropertyName == nameof(DebugViewModel.Output) && _vm is not null)
        {
            if (_observed is not null) _observed.CollectionChanged -= OnOutputChanged;
            _observed = _vm.Output;
            _observed.CollectionChanged += OnOutputChanged;
            RebuildConsole();
        }
    }

    // 実行系コマンド（開始/アタッチ/ビルド/テスト）押下で「実行」タブを即表示する。
    private void OnOutputRequested() => DebugTabs.SelectedIndex = OutputTab;

    private void OnClearOutputClick(object sender, RoutedEventArgs e) => _vm?.ClearOutput();

    // プロジェクト一覧のダブルクリック：行の ▶ と同じく、そのプロジェクトをデバッグ実行する。
    private void OnProjectDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem { DataContext: DebugProjectDiscovery.ProjectEntry project })
            {
                if (DataContext is DebugViewModel vm && vm.Launch.RunProjectCommand.CanExecute(project))
                    vm.Launch.RunProjectCommand.Execute(project);
                return;
            }
        }
    }

    private void OnProjectKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is ListBox { SelectedItem: DebugProjectDiscovery.ProjectEntry project }
            && DataContext is DebugViewModel vm
            && vm.Launch.RunProjectCommand.CanExecute(project))
        {
            vm.Launch.RunProjectCommand.Execute(project);
            e.Handled = true;
        }
    }

    private void OnProjectPaneToggleClick(object sender, RoutedEventArgs e)
    {
        if (_projectPaneExpanded)
        {
            if (ProjectColumn.ActualWidth > 40)
                _expandedProjectPaneWidth = ProjectColumn.ActualWidth;
            _projectPaneExpanded = false;
            ProjectPaneContent.Visibility = Visibility.Collapsed;
            ProjectSplitter.Visibility = Visibility.Collapsed;
            ProjectSplitterColumn.Width = new GridLength(0);
            ProjectColumn.Width = new GridLength(28);
            ProjectPaneRail.Visibility = Visibility.Visible;
            ProjectPaneToggle.Content = "›";
            ProjectPaneToggle.ToolTip = "プロジェクト領域を展開";
        }
        else
        {
            _projectPaneExpanded = true;
            ProjectPaneContent.Visibility = Visibility.Visible;
            ProjectSplitter.Visibility = Visibility.Visible;
            ProjectSplitterColumn.Width = new GridLength(6);
            ProjectColumn.Width = new GridLength(Math.Max(170, _expandedProjectPaneWidth));
            ProjectPaneRail.Visibility = Visibility.Collapsed;
            ProjectPaneToggle.Content = "‹";
            ProjectPaneToggle.ToolTip = "プロジェクト領域を折りたたむ";
        }
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                var atBottom = IsConsoleAtBottom;
                foreach (DebugOutputLine l in e.NewItems!) AppendConsoleLine(l);
                if (atBottom) ConsoleBox.ScrollToEnd();
                break;
            case NotifyCollectionChangedAction.Remove:
                // VM の 2000 行キャップ（先頭から除去）をドキュメントにも反映する。
                for (var i = 0; i < (e.OldItems?.Count ?? 0); i++)
                    if (ConsoleBox.Document.Blocks.FirstBlock is { } b)
                        ConsoleBox.Document.Blocks.Remove(b);
                break;
            case NotifyCollectionChangedAction.Reset:
                ConsoleBox.Document.Blocks.Clear();
                break;
        }
    }

    private bool IsConsoleAtBottom
        => ConsoleBox.ExtentHeight <= ConsoleBox.ViewportHeight
        || ConsoleBox.VerticalOffset + ConsoleBox.ViewportHeight >= ConsoleBox.ExtentHeight - 4;

    // テストタブを開いたら（まだ一覧が無ければ）バックグラウンド収集を起こす保険。e.Source で内側の
    // 選択イベント（TreeView/ListBox/検査タブの SelectionChanged のバブリング）を弾く。
    private void OnDebugTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DebugTabs) && DebugTabs.SelectedIndex == TestTab
            && DataContext is DebugViewModel vm)
            vm.Tests.EnsureTestsDiscovered();
    }

    // コールスタック（インラインタブ）のダブルクリック：選択フレームのソースへジャンプ（通常タブ＋フォーカス）。
    // 余白のダブルクリックでは発火させない（行＝ListBoxItem 上のときだけ）。
    private void OnCallStackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem)
            {
                if (DataContext is DebugViewModel { Inspection: { } insp })
                    insp.ActivateFrame(insp.SelectedFrame);
                return;
            }
        }
    }

    // インラインタブ（自動・コールスタック）の右クリック「コピー」。
    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);
}
