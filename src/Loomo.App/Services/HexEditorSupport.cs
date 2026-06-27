using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// テキスト系の提供者が無く、かつ <see cref="BinaryFileDetector"/> がバイナリと判定したファイルの
/// フォールバック表示。拡張子では登録されず（<see cref="EditorSupportRegistry"/> を通らない）、
/// ShellWindow が「対応プロバイダ無し＋バイナリ」のときだけ使う。等幅で
/// <c>オフセット | 16進16バイト | ASCII</c> をダンプする読み取り専用ビューア。
/// 巨大ファイルでも固まらないよう、行は要求された分だけ整形し（遅延 <see cref="HexDumpLines"/>）、
/// ListBox の UI 仮想化で可視行のコンテナだけを実体化する。読み込みバイト数は
/// <see cref="MaxLoadBytes"/> で上限を設ける（超過分は先頭のみ表示）。
/// </summary>
public sealed class HexEditorSupport : IEditorSupportVisualProvider
{
    /// <summary>メモリへ読み込むバイト数の上限。超えたら先頭だけ読みキャプションに注記する。</summary>
    private const long MaxLoadBytes = 64L * 1024 * 1024;

    private Grid? _view;
    private ListBox? _list;
    private TextBlock? _caption;
    private Button? _copyPathButton;
    private Button? _copyButton;
    private string? _filePath;

    // 読み取り専用なので編集の書き戻しは無い（購読は受けるが発火しない）。
    public event EventHandler<EditorSupportContentEdited>? ContentEdited { add { } remove { } }

    // 拡張子では解決させない（フォールバック専用）。registry には登録しない。
    public IReadOnlyCollection<string> SupportedExtensions => Array.Empty<string>();

    public string DescribeTitle(string filePath) => $"Hex: {Path.GetFileName(filePath)}";

    public FrameworkElement GetOrCreateView()
    {
        if (_view is not null)
            return _view;

        _list = new ListBox
        {
            SelectionMode = SelectionMode.Extended,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0)
        };
        _list.SetResourceReference(Control.ForegroundProperty, "Fg");
        _list.SetResourceReference(Control.BackgroundProperty, "Panel");

        // 行は1行＝1文字列。可視分だけコンテナを作るよう UI 仮想化（リサイクル）を効かせる。
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Item);

        var rowTemplate = new DataTemplate();
        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        textFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        rowTemplate.VisualTree = textFactory;
        _list.ItemTemplate = rowTemplate;

        // 余白を詰めて高密度に（プロジェクト方針）。
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        _list.ItemContainerStyle = itemStyle;

        _list.KeyDown += OnListKeyDown;

        _caption = new TextBlock
        {
            Margin = new Thickness(10, 6, 10, 8),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");

        _view = new Grid();
        _view.SetResourceReference(Panel.BackgroundProperty, "Panel");
        _view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = CreateToolbar();
        _view.Children.Add(toolbar);
        Grid.SetRow(toolbar, 0);
        _view.Children.Add(_list);
        Grid.SetRow(_list, 1);
        _view.Children.Add(_caption);
        Grid.SetRow(_caption, 2);

        return _view;
    }

    public Task UpdateAsync(string filePath, string text)
    {
        GetOrCreateView();
        _filePath = filePath;

        try
        {
            var info = new FileInfo(filePath);
            var total = info.Exists ? info.Length : 0;
            var readLength = (int)Math.Min(total, MaxLoadBytes);

            byte[] bytes;
            if (readLength <= 0)
            {
                bytes = Array.Empty<byte>();
            }
            else
            {
                bytes = new byte[readLength];
                using var stream = File.OpenRead(filePath);
                var offset = 0;
                while (offset < readLength)
                {
                    var read = stream.Read(bytes, offset, readLength - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }
                if (offset != readLength)
                    Array.Resize(ref bytes, offset);
            }

            _list!.ItemsSource = bytes.Length == 0 ? null : new HexDumpLines(bytes);

            var truncated = total > bytes.Length;
            _caption!.Text = bytes.Length == 0
                ? $"{Path.GetFileName(filePath)}  （空のファイル）"
                : truncated
                    ? $"{Path.GetFileName(filePath)}  {FormatBytes(total)}  先頭 {FormatBytes(bytes.Length)} を表示"
                    : $"{Path.GetFileName(filePath)}  {FormatBytes(total)}";
        }
        catch (Exception ex)
        {
            if (_list is not null)
                _list.ItemsSource = null;
            if (_caption is not null)
                _caption.Text = $"{Path.GetFileName(filePath)}  読み込み失敗: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    private FrameworkElement CreateToolbar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 6, 8, 4)
        };
        _copyButton = MakeButton("⧉", CopySelection, "選択した行をコピー (Ctrl+C)");
        _copyPathButton = MakeButton("⛓", CopyPath, "パスをコピー");
        bar.Children.Add(_copyButton);
        bar.Children.Add(_copyPathButton);
        return bar;
    }

    private static Button MakeButton(string text, Action action, string tooltip)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = tooltip,
            MinWidth = 34,
            Height = 26,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 12
        };
        button.SetResourceReference(Control.ForegroundProperty, "Fg");
        button.SetResourceReference(Control.BackgroundProperty, "BgAlt");
        button.SetResourceReference(Control.BorderBrushProperty, "Border");
        button.Click += (_, _) => action();
        return button;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopySelection();
            e.Handled = true;
        }
    }

    private void CopySelection()
    {
        if (_list is null || _list.SelectedItems.Count == 0)
            return;

        var sb = new StringBuilder();
        foreach (var item in _list.SelectedItems)
            sb.AppendLine(item?.ToString());

        TrySetClipboard(sb.ToString());
    }

    private void CopyPath()
    {
        if (!string.IsNullOrEmpty(_filePath))
            TrySetClipboard(_filePath);
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* クリップボードが他プロセスに握られている等は黙って無視 */ }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// バイト列を hexdump 風の行（<c>オフセット  16進×16  |ASCII|</c>）へ遅延整形する読み取り専用リスト。
/// 行文字列は要求された添字の分だけその場で組み立てるので、巨大ファイル（数百万行）でも
/// 全行を先に作らない。ListBox の UI 仮想化が可視範囲の添字だけを引く前提。
/// ItemsControl が全列挙でスナップショットを取らないよう、必ず <see cref="IList"/> として渡す
/// （<see cref="Count"/> ＋ インデクサだけで回り、未表示行は整形されない）。
/// </summary>
public sealed class HexDumpLines : IList, IReadOnlyList<string>
{
    private const int BytesPerLine = 16;
    private readonly byte[] _bytes;

    public HexDumpLines(byte[] bytes) => _bytes = bytes;

    public int Count => (_bytes.Length + BytesPerLine - 1) / BytesPerLine;

    public string this[int index] => FormatLine(index);

    private string FormatLine(int index)
    {
        var start = (long)index * BytesPerLine;
        var len = (int)Math.Min(BytesPerLine, _bytes.Length - start);

        // オフセット(8桁) + 区切り + 16進(各2桁+空白, 8バイト目で追加の空白) + 区切り + |ASCII|
        var sb = new StringBuilder(BytesPerLine * 4 + 16);
        sb.Append(start.ToString("X8")).Append("  ");

        for (var i = 0; i < BytesPerLine; i++)
        {
            if (i == BytesPerLine / 2)
                sb.Append(' ');
            if (i < len)
                sb.Append(_bytes[start + i].ToString("x2")).Append(' ');
            else
                sb.Append("   ");
        }

        sb.Append(' ').Append('|');
        for (var i = 0; i < len; i++)
        {
            var b = _bytes[start + i];
            sb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
        }
        sb.Append('|');

        return sb.ToString();
    }

    public IEnumerator<string> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return FormatLine(i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---- IList（読み取り専用・固定サイズ）。仮想化に必要なのは Count とインデクサのみ。----
    object? IList.this[int index]
    {
        get => FormatLine(index);
        set => throw new NotSupportedException();
    }

    bool IList.IsFixedSize => true;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();

    bool IList.Contains(object? value) => IndexOf(value) >= 0;
    int IList.IndexOf(object? value) => IndexOf(value);

    private int IndexOf(object? value)
    {
        if (value is not string s)
            return -1;
        for (var i = 0; i < Count; i++)
            if (FormatLine(i) == s)
                return i;
        return -1;
    }

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
            array.SetValue(FormatLine(i), index + i);
    }
}
