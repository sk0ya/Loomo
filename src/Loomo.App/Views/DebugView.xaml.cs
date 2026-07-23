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

/// <summary>IDE（デバッグ）ペインのシェル。タブ（構成/出力/問題/変数/自動/コールスタック/テスト/スレッド/
/// ブレークポイント/イミディエイト/モジュール）を束ねる。大きなタブはサブビューに分割し、ここは出力コンソールの
/// ドキュメント追記と、停止/実行・実行系コマンド押下に応じたタブ自動切り替えだけを持つ。</summary>
public partial class DebugView : UserControl
{
    // タブのインデックス（XAML の並び順と一致させる）。
    // 並び：構成0 / 出力1 / 問題2 / 変数3 / 自動4 / コールスタック5 / テスト6 / スレッド7 /
    //       ブレークポイント8 / イミディエイト9 / モジュール10。
    private const int OutputTab = 1;
    private const int VariablesTab = 3;
    private const int TestTab = 6;

    private INotifyCollectionChanged? _observed;
    private DebugViewModel? _vm;

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

    // ブレークポイント等で停止したら「変数」へ、続行したら「出力」へ自動で切り替える。
    // 開始/ビルド/テスト押下時の「出力」表示は OutputRequested（押下と同期）で行う。
    // Output はセッション切替で参照先の ObservableCollection ごと差し替わるので、そのたびに購読と表示を作り直す。
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DebugViewModel.IsStopped) && _vm is not null)
            DebugTabs.SelectedIndex = _vm.IsStopped ? VariablesTab : OutputTab;

        if (e.PropertyName == nameof(DebugViewModel.Output) && _vm is not null)
        {
            if (_observed is not null) _observed.CollectionChanged -= OnOutputChanged;
            _observed = _vm.Output;
            _observed.CollectionChanged += OnOutputChanged;
            RebuildConsole();
        }
    }

    // 実行系コマンド（開始/アタッチ/ビルド/テスト）押下で「出力」タブを即表示する。
    private void OnOutputRequested() => DebugTabs.SelectedIndex = OutputTab;

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

    // テストタブを開いたら（まだ一覧が無ければ）バックグラウンド収集を起こす保険。e.Source で内側の
    // 選択イベント（TreeView/ListBox の SelectionChanged のバブリング）を弾く。
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
