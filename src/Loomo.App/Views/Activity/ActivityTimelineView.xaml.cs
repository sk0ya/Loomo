using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>「進行状況」エントリ（<see cref="ViewModels.TranscriptEntry"/>）の構造化タイムライン表示。
/// チャットの AIバーとワークフローのステップ実行ログで共用する。</summary>
public partial class ActivityTimelineView : UserControl
{
    public ActivityTimelineView()
    {
        InitializeComponent();
    }
}
