using System.Windows.Threading;
using sk0ya.Loomo.App.Input;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 連鎖キーの続きの案内（§24.2.2）。Ctrl+W などの 1 打目を押したまま少し迷うと、
/// 2 打目に押せるキーとコマンド名を右下（通知欄の真上）に出す。</summary>
public partial class ShellWindow {
    /// <summary>1 打目から案内を出すまでの間。慣れた人が続けて打つ間は出さない（ちらつかせない）。</summary>
    private static readonly TimeSpan KeyChordHintDelay = TimeSpan.FromMilliseconds(450);

    private DispatcherTimer? _keyChordHintTimer;
    private KeyChord? _keyChordHintPrefix;

    private void OnKeyChordPendingChanged(KeyChord? prefix) {
        _keyChordHintPrefix = prefix;
        if (prefix is null) {
            _keyChordHintTimer?.Stop();
            KeyChordHint.Visibility = Visibility.Collapsed;
            return;
        }
        // 待ち中にプレフィックスを打ち直した（待ち継続）ときも出し直す。
        KeyChordHint.Visibility = Visibility.Collapsed;
        _keyChordHintTimer ??= CreateKeyChordHintTimer();
        _keyChordHintTimer.Stop();
        _keyChordHintTimer.Start();
    }

    private DispatcherTimer CreateKeyChordHintTimer() {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = KeyChordHintDelay };
        timer.Tick += (_, _) => {
            timer.Stop();
            if (_keyChordHintPrefix is { } prefix) ShowKeyChordHint(prefix);
        };
        return timer;
    }

    private void ShowKeyChordHint(KeyChord prefix) {
        if (_keyboard is null) return;
        var continuations = _keyboard.ContinuationsOf(prefix);
        if (continuations.Count == 0) return;

        KeyChordHintHeader.Text = $"{prefix.Format()} の続き　・　Esc でやめる";
        var rows = KeyChordHintRows;
        rows.Children.Clear();
        rows.RowDefinitions.Clear();
        rows.ColumnDefinitions.Clear();
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < continuations.Count; i++) {
            var (second, id) = continuations[i];
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = second.Format(), Margin = new Thickness(0, 1, 12, 1) };
            key.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            key.SetResourceReference(TextBlock.FontSizeProperty, "Fs12");
            var title = new TextBlock {
                Text = CommandCatalog.Find(id)?.Title ?? id, Margin = new Thickness(0, 1, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            title.SetResourceReference(TextBlock.FontSizeProperty, "Fs12");
            Grid.SetRow(key, i);
            Grid.SetRow(title, i);
            Grid.SetColumn(title, 1);
            rows.Children.Add(key);
            rows.Children.Add(title);
        }
        KeyChordHint.Visibility = Visibility.Visible;
    }
}
