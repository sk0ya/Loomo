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

/// <summary>TS IDE（TypeScript / Node.js デバッグ）ペインのシェル。dotnet 用 <see cref="DebugView"/> の
/// クローンで、普段の「実行」（スクリプト一覧＋出力）と、デバッグ中だけ現れる検査タブ、問題/テスト/構成を
/// 束ねる。ここは出力コンソールのドキュメント追記と、停止/実行・実行系コマンド押下に応じたタブ自動切り替え
/// だけを持つ。DataContext は <see cref="TsDebugViewModel"/>（基底 <see cref="DebugManagerViewModelBase"/>
/// 経由で扱う）。</summary>
public partial class TsDebugView : UserControl
{
    private INotifyCollectionChanged? _observed;
    private DebugManagerViewModelBase? _vm;
    private bool _scriptPaneExpanded = true;
    private double _expandedScriptPaneWidth = 220;

    public TsDebugView()
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
        if (DataContext is DebugManagerViewModelBase vm)
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

    /// <summary>コンソールが末尾（追従してよい位置）にあるか。スクロールできない短い出力も末尾扱い。</summary>
    private bool IsConsoleAtBottom
        => ConsoleBox.ExtentHeight <= ConsoleBox.ViewportHeight
        || ConsoleBox.VerticalOffset + ConsoleBox.ViewportHeight >= ConsoleBox.ExtentHeight - 4;

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

    // ブレークポイント等で停止したら「変数」へ、続行・終了したら「実行」へ自動で切り替える。
    // 開始/アタッチ/型チェック押下時も、スクリプト一覧を残した「実行」を表示する。
    // Output はセッション切替で参照先の ObservableCollection ごと差し替わるので、そのたびに購読と表示を作り直す。
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DebugManagerViewModelBase.IsStopped) && _vm is not null)
        {
            if (_vm.IsStopped)
            {
                SelectTab(DebugTabItem);
                InspectionTabs.SelectedItem = VariablesTabItem;
            }
            else
                SelectTab(ExecutionTab);
        }

        if (e.PropertyName == nameof(DebugManagerViewModelBase.IsBusy) && _vm is not null && !_vm.IsBusy)
            SelectTab(ExecutionTab);

        if (e.PropertyName == nameof(DebugManagerViewModelBase.Output) && _vm is not null)
        {
            if (_observed is not null) _observed.CollectionChanged -= OnOutputChanged;
            _observed = _vm.Output;
            _observed.CollectionChanged += OnOutputChanged;
            RebuildConsole();
        }
    }

    // 実行系コマンド（開始/アタッチ/型チェック）押下で、スクリプト一覧＋出力の「実行」を表示する。
    private void OnOutputRequested() => SelectTab(ExecutionTab);

    private void SelectTab(TabItem tab)
    {
        if (tab.Visibility == Visibility.Visible)
            DebugTabs.SelectedItem = tab;
    }

    // テストタブを開いたら（まだ一覧が無ければ）バックグラウンド収集を起こす保険。e.Source で内側の
    // 選択イベント（TreeView/ListBox の SelectionChanged のバブリング）を弾く。
    private void OnDebugTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DebugTabs) && ReferenceEquals(DebugTabs.SelectedItem, TestsTabItem)
            && DataContext is TsDebugViewModel vm)
            vm.Tests.EnsureTestsDiscovered();
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                // 追従は「もう末尾を見ているとき」だけ。実行中に上へ遡ってエラーを読んでいる最中に
                // 新しい行が来るたび末尾へ飛ばされると、長い出力は事実上読めない。
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

    // コールスタック（インラインタブ）のダブルクリック：選択フレームのソースへジャンプ（通常タブ＋フォーカス）。
    // 余白のダブルクリックでは発火させない（行＝ListBoxItem 上のときだけ）。
    private void OnCallStackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem)
            {
                if (DataContext is DebugManagerViewModelBase { Inspection: { } insp })
                    insp.ActivateFrame(insp.SelectedFrame);
                return;
            }
        }
    }

    // スクリプトタブのダブルクリック：その行のスクリプトをデバッグ実行（行の ▶ ボタンと同じ）。
    // 余白のダブルクリックでは発火させない（行＝ListBoxItem 上のときだけ）。
    private void OnScriptDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem { DataContext: TsScriptEntry entry })
            {
                if (DataContext is TsDebugViewModel vm && vm.Launch.RunScriptCommand.CanExecute(entry))
                    vm.Launch.RunScriptCommand.Execute(entry);
                return;
            }
        }
    }

    private void OnScriptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is ListBox { SelectedItem: TsScriptEntry entry }
            && DataContext is TsDebugViewModel vm
            && vm.Launch.RunScriptCommand.CanExecute(entry))
        {
            vm.Launch.RunScriptCommand.Execute(entry);
            e.Handled = true;
        }
    }

    // インラインタブ（自動・コールスタック）の右クリック「コピー」。
    private void OnCopyItemClick(object sender, RoutedEventArgs e) => DebugItemClipboard.Copy(sender);

    private void OnClearOutputClick(object sender, RoutedEventArgs e) => _vm?.ClearOutput();

    private void OnScriptPaneToggleClick(object sender, RoutedEventArgs e)
    {
        if (_scriptPaneExpanded)
        {
            if (ScriptColumn.ActualWidth > 40)
                _expandedScriptPaneWidth = ScriptColumn.ActualWidth;
            _scriptPaneExpanded = false;
            ScriptPaneContent.Visibility = Visibility.Collapsed;
            ScriptSplitter.Visibility = Visibility.Collapsed;
            ScriptSplitterColumn.Width = new GridLength(0);
            ScriptColumn.Width = new GridLength(28);
            ScriptPaneRail.Visibility = Visibility.Visible;
            ScriptPaneToggle.Content = "›";
            ScriptPaneToggle.ToolTip = "スクリプト領域を展開";
        }
        else
        {
            _scriptPaneExpanded = true;
            ScriptPaneContent.Visibility = Visibility.Visible;
            ScriptSplitter.Visibility = Visibility.Visible;
            ScriptSplitterColumn.Width = new GridLength(6);
            ScriptColumn.Width = new GridLength(Math.Max(170, _expandedScriptPaneWidth));
            ScriptPaneRail.Visibility = Visibility.Collapsed;
            ScriptPaneToggle.Content = "‹";
            ScriptPaneToggle.ToolTip = "スクリプト領域を折りたたむ";
        }
    }
}
