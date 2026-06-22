using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

public partial class WorkflowView : UserControl
{
    // 実行ログの末尾追従中か。ユーザーが上へスクロールすると false、最下部まで戻すと true。
    private bool _logAtBottom = true;

    public WorkflowView()
    {
        InitializeComponent();
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
}
