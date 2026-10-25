using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コミットメッセージ欄の補完（§24.95）。欄は素の <see cref="TextBox"/> のまま、打ちかけの語の下に候補の一覧を出す。
/// 候補（変えた行の識別子・ファイル名）は欄に入ったときに一度引き、出ている間は打つたびに絞り直すだけ（git は起こさない）。
/// <para>キー：↑↓ で選ぶ・Tab か Enter で入れる・Esc で閉じる・Ctrl+Space で開く。Ctrl+Enter（コミット）は横取りしない。</para>
/// </summary>
internal sealed class CommitMessageCompletionController
{
    private readonly TextBox _box;
    private readonly Func<Task<IReadOnlyList<string>>> _load;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private IReadOnlyList<string> _candidates = [];
    private int _replaceStart;
    private int _loadGeneration;
    private bool _inserting;

    public CommitMessageCompletionController(TextBox box, Func<Task<IReadOnlyList<string>>> load)
    {
        _box = box;
        _load = load;
        _list = new ListBox
        {
            MaxHeight = 180,
            MinWidth = 160,
            Focusable = false,
            BorderThickness = new Thickness(1),
        };
        _list.SetResourceReference(Control.BackgroundProperty, "Panel");
        _list.SetResourceReference(Control.ForegroundProperty, "Fg");
        _list.SetResourceReference(Control.BorderBrushProperty, "Border");
        _list.PreviewMouseLeftButtonUp += (_, _) => Accept();
        _popup = new Popup
        {
            Child = _list,
            PlacementTarget = box,
            Placement = PlacementMode.RelativePoint,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        box.GotKeyboardFocus += async (_, _) => await ReloadAsync();
        box.LostKeyboardFocus += (_, _) => Close();
        box.TextChanged += (_, _) => { if (!_inserting) Refresh(force: false); };
        box.PreviewKeyDown += OnPreviewKeyDown;
    }

    private async Task ReloadAsync()
    {
        var generation = ++_loadGeneration;
        IReadOnlyList<string> loaded;
        try
        {
            loaded = await _load();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            loaded = [];
        }
        if (generation == _loadGeneration)
            _candidates = loaded;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Refresh(force: true);
            e.Handled = true;
            return;
        }
        if (!_popup.IsOpen)
            return;
        switch (e.Key)
        {
            case Key.Down:
                _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Tab:
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>打ちかけの語に合う候補を出す。<paramref name="force"/>（Ctrl+Space）でなければ、2 文字以上打ったときだけ。</summary>
    private void Refresh(bool force)
    {
        if (!_box.IsKeyboardFocusWithin || _candidates.Count == 0)
        {
            Close();
            return;
        }
        var (start, prefix, matches) = CommitMessageCompletion.Suggest(_candidates, _box.Text, _box.CaretIndex);
        if (force && prefix.Length < 2)
            matches = _candidates.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && c.Length > prefix.Length).Take(8).ToList();
        if (matches.Count == 0)
        {
            Close();
            return;
        }
        _replaceStart = start;
        _list.ItemsSource = matches;
        _list.SelectedIndex = 0;
        var rect = _box.GetRectFromCharacterIndex(start);
        if (rect.IsEmpty)
            rect = new Rect(0, 0, 0, _box.ActualHeight);
        _popup.HorizontalOffset = rect.Left;
        _popup.VerticalOffset = rect.Bottom + 2;
        _popup.IsOpen = true;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not string word)
        {
            Close();
            return;
        }
        var caret = _box.CaretIndex;
        var start = Math.Clamp(_replaceStart, 0, caret);
        _inserting = true;
        try
        {
            _box.Text = _box.Text[..start] + word + _box.Text[caret..];
            _box.CaretIndex = start + word.Length;
        }
        finally
        {
            _inserting = false;
        }
        Close();
    }

    private void Close() => _popup.IsOpen = false;
}
