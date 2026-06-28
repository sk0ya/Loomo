using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.Views;

/// <summary>サイドバー「デバッグ」パネル（Phase 1）。出力追加時に末尾へ自動スクロールする。
/// 検査内容は「出力／変数／コールスタック」のタブに分け、停止時は自動で「変数」タブへ切り替える。</summary>
public partial class DebugView : UserControl
{
    // タブのインデックス（XAML の並び順と一致させる）。
    private const int OutputTab = 0;
    private const int VariablesTab = 1;

    private INotifyCollectionChanged? _observed;
    private DebugViewModel? _vm;

    public DebugView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null) _observed.CollectionChanged -= OnOutputChanged;
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        if (DataContext is DebugViewModel vm)
        {
            _observed = vm.Output;
            _observed.CollectionChanged += OnOutputChanged;
            _vm = vm;
            _vm.PropertyChanged += OnVmPropertyChanged;
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

    // 停止したら「変数」タブへ、実行を再開／終了したら「出力」タブへ自動で切り替える。
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DebugViewModel.IsStopped) && _vm is not null)
            DebugTabs.SelectedIndex = _vm.IsStopped ? VariablesTab : OutputTab;
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (DebugOutputLine l in e.NewItems!) AppendConsoleLine(l);
                ConsoleBox.ScrollToEnd();
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

    private void OnRefreshClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is DebugViewModel vm) vm.Refresh();
    }

    // コールスタックのダブルクリック：選択フレームのソースへジャンプ（通常タブ＋フォーカス）。
    private void OnCallStackDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 余白のダブルクリックでは発火させない（行＝ListBoxItem 上のときだけ）。
        for (var d = e.OriginalSource as System.Windows.DependencyObject; d is not null;
             d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.ListBoxItem)
            {
                if (DataContext is DebugViewModel vm)
                    vm.ActivateFrame(vm.SelectedFrame);
                return;
            }
        }
    }

    // 失敗テストのダブルクリック：スタックトレースから拾った位置へジャンプ（行＝ListBoxItem 上のときだけ）。
    private void OnTestDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as System.Windows.DependencyObject; d is not null;
             d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.ListBoxItem item)
            {
                if (DataContext is DebugViewModel vm && item.DataContext is TestResultViewModel t)
                    vm.NavigateToTestSource(t);
                return;
            }
        }
    }

    // 右クリックメニュー「コピー」：その項目 1 件だけをテキスト化してクリップボードへ。
    // ContextMenu は配置先要素の DataContext（＝その項目）を引き継ぐので、それを種別で振り分ける。
    private void OnCopyItemClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var text = (sender as FrameworkElement)?.DataContext switch
        {
            DebugFrameViewModel f => string.IsNullOrEmpty(f.Location) ? f.Name : $"{f.Name}  {f.Location}",
            DebugVariableViewModel v => string.IsNullOrEmpty(v.Value) ? v.Name : $"{v.Name} = {v.Value}",
            WatchItemViewModel w => $"{w.Expression} = {w.Value}",
            TestResultViewModel t => string.IsNullOrEmpty(t.Message) ? t.Name : $"{t.Name}  {t.Message}",
            _ => null,
        };
        if (text is not null)
            try { System.Windows.Clipboard.SetText(text); } catch { /* 占有中は無視 */ }
    }

    // プロセス一覧のダブルクリック：その行のプロセスへ即アタッチ（行＝ListBoxItem 上のときだけ）。
    private void OnProcessDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as System.Windows.DependencyObject; d is not null;
             d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.ListBoxItem)
            {
                if (DataContext is DebugViewModel vm && vm.AttachCommand.CanExecute(null))
                    vm.AttachCommand.Execute(null);
                return;
            }
        }
    }

    private void OnWatchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && DataContext is DebugViewModel vm
            && vm.AddWatchCommand.CanExecute(null))
        {
            vm.AddWatchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
