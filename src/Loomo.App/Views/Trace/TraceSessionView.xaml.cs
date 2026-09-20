using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// トレース閲覧セッションペイン。traces/*.jsonl をターンごとのタイムラインとして表示する（読み取り専用）。
/// ロジックは <see cref="ViewModels.TraceSessionViewModel"/> に集約する。
/// </summary>
public partial class TraceSessionView : UserControl
{
    public TraceSessionView()
    {
        InitializeComponent();
    }
}
