using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_workflow is not null)
            _workflow.PropertyChanged -= OnWorkflowPropertyChanged;

        _workflow = e.NewValue as WorkflowViewModel;

        if (_workflow is not null)
        {
            _workflow.PropertyChanged += OnWorkflowPropertyChanged;
            ResetFinalOutputRow(_workflow.HasFinalOutput);
        }
        else
        {
            ResetFinalOutputRow(false);
        }
    }

    private void OnWorkflowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowViewModel.HasFinalOutput))
            Dispatcher.InvokeAsync(() => ResetFinalOutputRow(_workflow?.HasFinalOutput == true));
    }

    private void ResetFinalOutputRow(bool hasFinalOutput)
    {
        FinalOutputRow.Height = hasFinalOutput
            ? new GridLength(0.65, GridUnitType.Star)
            : new GridLength(0);
    }
}
