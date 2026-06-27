using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class WorkflowView : UserControl
{
    // 実行ログの末尾追従中か。ユーザーが上へスクロールすると false、最下部まで戻すと true。
    private bool _logAtBottom = true;
    private WorkflowViewModel? _workflow;

    public WorkflowView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>「ステップを追加」パレットで候補を選んだら、追加後にパレットを閉じる。</summary>
    private void OnAddCandidateClick(object sender, RoutedEventArgs e)
    {
        AddPaletteToggle.IsChecked = false;
    }

    /// <summary>実行ログを末尾追従させる。コンテンツ増加（ExtentHeightChange != 0）のときは、
    /// 直前まで最下部にいた場合だけ末尾へ送る。ユーザーが上を見ている間は追従しない。
    /// 承認カードなど末尾に出る要素を見逃さないための配慮。</summary>
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            const double tolerance = 1.0;
            _logAtBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - tolerance;
        }
        else if (_logAtBottom)
        {
            LogScrollViewer.ScrollToEnd();
        }
    }

    private void OnCopyFinalOutputClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_workflow?.FinalOutput))
            Clipboard.SetText(_workflow.FinalOutput);
    }

    /// <summary>テキストファイルを選んで読み込み、ワークフロー入力（{{input}}）に流し込む。</summary>
    private void OnLoadInputFromFileClick(object sender, RoutedEventArgs e)
    {
        if (_workflow is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "入力に読み込むファイル",
            Filter = "テキスト系ファイル|*.txt;*.md;*.cs;*.json;*.xml;*.log;*.csv;*.yml;*.yaml|すべてのファイル|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _workflow.RunInput = System.IO.File.ReadAllText(dialog.FileName);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"ファイルを読み込めませんでした:\n{ex.Message}", "Loomo",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>ワークフロー入力を空にする。</summary>
    private void OnClearInputClick(object sender, RoutedEventArgs e)
    {
        if (_workflow is not null) _workflow.RunInput = "";
    }

    private void OnWorkflowListItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PromptRenameWorkflow((sender as FrameworkElement)?.DataContext as WorkflowSummary);
    }

    private void OnRenameWorkflowClick(object sender, RoutedEventArgs e)
    {
        var menu = (sender as FrameworkElement)?.Parent as ContextMenu;
        PromptRenameWorkflow(menu?.PlacementTarget is FrameworkElement target
            ? target.DataContext as WorkflowSummary
            : null);
    }

    private void PromptRenameWorkflow(WorkflowSummary? summary)
    {
        if (_workflow is null || summary is null) return;
        var name = InputDialog.Prompt(
            Window.GetWindow(this),
            "ワークフロー名の変更",
            "ワークフロー名を入力:",
            summary.Name);
        if (name is null) return;
        _workflow.RenameWorkflow(summary, name);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_workflow is not null)
            _workflow.PropertyChanged -= OnWorkflowPropertyChanged;

        _workflow = e.NewValue as WorkflowViewModel;

        if (_workflow is not null)
        {
            _workflow.PropertyChanged += OnWorkflowPropertyChanged;
            ResetProgressRows();
        }
        else
        {
            ResetProgressRows();
        }
    }

    private void OnWorkflowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowViewModel.HasFinalOutput)
            || e.PropertyName == nameof(WorkflowViewModel.IsProgressDetailsExpanded)
            || e.PropertyName == nameof(WorkflowViewModel.IsWarmingUp)
            || e.PropertyName == nameof(WorkflowViewModel.IsProgressVisible))
            Dispatcher.InvokeAsync(ResetProgressRows);
    }

    private void ResetProgressRows()
    {
        var hasFinalOutput = _workflow?.HasFinalOutput == true;
        var isProgressVisible = _workflow?.IsProgressVisible == true;
        var isWarmingUp = _workflow?.IsWarmingUp == true;
        var showProgressDetails = _workflow?.IsProgressDetailsExpanded == true || _workflow?.IsWarmingUp == true;

        // 外側（パイプライン↔進捗エリア）の分割。
        // ここを詳細トグルのたびに Star へ再代入すると、ユーザーがドラッグで広げた分割や
        // 外側 GridSplitter の状態とぶつかり、進捗エリアが 0 高さに潰れる／画面外へ押し出されて
        // 戻らなくなる（＝「出力を出したあと詳細を表示/非表示すると全部閉じられる」不具合）。
        // そこで“種類”が変わるとき（非表示↔表示↔ウォームアップ）だけ設定し、表示中の Star は
        // ドラッグした高さを尊重してそのまま残す。さらに MinHeight の床でどの状態でも消さない。
        var targetOuter = !isProgressVisible
            ? new GridLength(0)
            : isWarmingUp && !hasFinalOutput
                ? GridLength.Auto
                : new GridLength(1.4, GridUnitType.Star);
        if (ProgressAreaRow.Height.GridUnitType != targetOuter.GridUnitType
            || (targetOuter.GridUnitType != GridUnitType.Star
                && ProgressAreaRow.Height.Value != targetOuter.Value))
            ProgressAreaRow.Height = targetOuter;
        ProgressAreaRow.MinHeight = isProgressVisible ? 96 : 0;

        // 内側（進捗ログ↔出力）は詳細トグルで切替える。必ず両行をそろえて設定し、
        // 片方だけ残って潰れることがないようにする。
        ProgressLogRow.MinHeight = showProgressDetails ? 72 : 38;
        ProgressLogRow.Height = showProgressDetails
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;

        FinalOutputRow.Height = hasFinalOutput
            ? new GridLength(showProgressDetails ? 0.7 : 1.0, GridUnitType.Star)
            : new GridLength(0);
    }
}
