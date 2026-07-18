
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ペイン活動インジケータ（袖＝周辺視野）。OSC133 シェル統合
/// （sk0ya.Terminal.Controls 1.0.8 の <see cref="TerminalTabView.ShellCommandActivity"/>）で
/// 可視ターミナルのコマンド実行を検知し、実行中／未確認の成功・失敗を袖・俯瞰カードの
/// バッジで知らせる。長いビルドを袖に置いたまま、終わったことを目の端で気づける。
/// 未確認の結果はターミナルが舞台に立つ（＝目に入る）と消える。
/// </summary>
public partial class ShellWindow
{
    private enum PaneActivityKind { None, Running, Succeeded, Failed }

    private sealed class TerminalActivityState
    {
        // OSC133 C（実行開始）〜 D（完了）の間 true。
        public bool Running;

        // まだユーザーが見ていない直近の終了コード（null=結果なし）。
        public int? UnseenExitCode;
    }

    // ターミナルタブ毎の活動状態（タブを閉じたら除去）。
    private readonly Dictionary<Guid, TerminalActivityState> _terminalActivity = new();

    // 現在表示中の袖/俯瞰カードのバッジ（RebuildStage で作り直される）。
    private readonly Dictionary<PaneKind, (Border Chip, TextBlock Label)> _stageActivityBadges = new();

    // タブ作成時に活動イベントを購読する（CreateTerminalTab から）。
    private void HookTerminalActivity(TerminalTab tab)
        => tab.View.ShellCommandActivity += (_, e) => OnTerminalShellActivity(tab.Id, e);

    // タブクローズ時の後始末（CloseTerminalTabAsync から）。
    private void ForgetTerminalActivity(Guid tabId)
    {
        if (_terminalActivity.Remove(tabId))
            UpdatePaneActivityBadge(PaneKind.Terminal);
    }

    private void OnTerminalShellActivity(Guid tabId, ShellCommandActivityEventArgs e)
    {
        if (!_terminalActivity.TryGetValue(tabId, out var state))
            _terminalActivity[tabId] = state = new TerminalActivityState();

        switch (e.Phase)
        {
            case ShellCommandPhase.CommandExecuted:
                state.Running = true;
                // 新しいコマンドが走り出したら、前回の未確認結果は意味を失う。
                state.UnseenExitCode = null;
                break;

            case ShellCommandPhase.CommandDone:
                // 実行開始（C）を見ていない D は記録しない：シェル統合スクリプトの初期化や
                // プロンプト再描画が D を発することがあり、起動直後に偽の「完了」が出る。
                if (!state.Running)
                    return;
                state.Running = false;
                // ターミナルが目に入っている間の完了は「見た」扱い（バッジ不要）。
                state.UnseenExitCode = IsTerminalPaneWatched() ? null : (e.ExitCode ?? 0);
                break;

            default:
                return; // PromptStart / CommandStart は表示に影響しない
        }

        UpdatePaneActivityBadge(PaneKind.Terminal);
    }

    // ターミナルペインがいまユーザーの目に入っているか。
    private bool IsTerminalPaneWatched()
        => _stageActive
            ? _stagePane == PaneKind.Terminal && !_overviewActive
            : IsPaneVisible(PaneKind.Terminal);

    // 対象ペインが舞台に立った（＝目に入った）ので未確認の結果を流す。実行中表示は保つ。
    private void MarkPaneActivitySeen(PaneKind kind)
    {
        if (kind != PaneKind.Terminal)
            return;
        foreach (var state in _terminalActivity.Values)
            state.UnseenExitCode = null;
        UpdatePaneActivityBadge(kind);
    }

    // 全タブの状態を1つのバッジへ集約する（実行中 ＞ 失敗 ＞ 成功 ＞ なし）。
    private PaneActivityKind AggregateTerminalActivity(out int exitCode)
    {
        exitCode = 0;
        if (_terminalActivity.Values.Any(s => s.Running))
            return PaneActivityKind.Running;

        var failed = _terminalActivity.Values.FirstOrDefault(s => s.UnseenExitCode is > 0);
        if (failed is not null)
        {
            exitCode = failed.UnseenExitCode!.Value;
            return PaneActivityKind.Failed;
        }

        return _terminalActivity.Values.Any(s => s.UnseenExitCode == 0)
            ? PaneActivityKind.Succeeded
            : PaneActivityKind.None;
    }

    // カード上のバッジへ現在の活動状態を反映する（カードが無ければ何もしない）。
    private void UpdatePaneActivityBadge(PaneKind kind)
    {
        if (kind != PaneKind.Terminal
            || !_stageActivityBadges.TryGetValue(kind, out var badge))
            return;

        var (chip, label) = badge;
        switch (AggregateTerminalActivity(out var exitCode))
        {
            case PaneActivityKind.Running:
                chip.Visibility = Visibility.Visible;
                chip.Background = (Brush)FindResource("Accent");
                label.Text = "● 実行中";
                break;
            case PaneActivityKind.Failed:
                chip.Visibility = Visibility.Visible;
                chip.Background = PaneActivityFailedBrush;
                label.Text = $"✗ 失敗 {exitCode}";
                break;
            case PaneActivityKind.Succeeded:
                chip.Visibility = Visibility.Visible;
                chip.Background = PaneActivitySucceededBrush;
                label.Text = "✓ 完了";
                break;
            default:
                chip.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private static readonly Brush PaneActivitySucceededBrush =
        new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B));
    private static readonly Brush PaneActivityFailedBrush =
        new SolidColorBrush(Color.FromRgb(0xD9, 0x53, 0x4D));

    // 袖/俯瞰カードの右上に活動バッジを生やす（BuildSessionCard から）。
    private void AttachActivityBadge(Grid cardRoot, PaneKind kind, bool isOverview)
    {
        if (kind != PaneKind.Terminal)
            return;

        var label = new TextBlock
        {
            FontSize = isOverview ? 12 : 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        };
        var chip = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 5, 0),
            Padding = new Thickness(7, 2, 7, 2),
            CornerRadius = new CornerRadius(9),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = label,
        };
        cardRoot.Children.Add(chip);
        _stageActivityBadges[kind] = (chip, label);
        UpdatePaneActivityBadge(kind);
    }
}
