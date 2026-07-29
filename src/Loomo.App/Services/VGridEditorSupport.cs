using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Models;
using VGrid.Commands;
using VGrid.Editor;
using VGrid.Models;
using VGrid.Services;
using VGrid.ViewModels;
using VGrid.VimEngine;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// CSV/TSV を VGrid.Editor のグリッド（Vim キーバインド付き <see cref="TsvEditorControl"/>）で表示する
/// EditorSupport 提供者。同期は双方向：
/// エディタ本文 → グリッドはデバウンス再パース（Markdown プレビューと同じ流れ）、
/// グリッド編集 → エディタ本文は <see cref="ContentEdited"/> で書き戻す（ShellWindow が SetText する）。
/// 書き戻しのエコー（SetText → BufferChanged → 再パース）は正規化テキスト比較で抑止し、
/// グリッドのカーソル・Undo 履歴を保つ。
/// </summary>
public sealed class VGridEditorSupport : IEditorSupportVisualProvider, IEditorSupportSearchHighlightProvider
{
    /// <summary>グリッド編集をエディタ本文へまとめて書き戻すまでの猶予。</summary>
    private static readonly TimeSpan WriteBackDelay = TimeSpan.FromMilliseconds(500);
    private static readonly string[] Extensions = [".csv", ".tsv"];

    /// <summary>検索ハイライトを塗るセル数の上限（巨大な CSV で通知の嵐にならないように）。</summary>
    private const int MaxHighlightedCells = 5000;

    /// <summary>検索条件の連続変化をまとめてから塗り直すまでの猶予。</summary>
    private static readonly TimeSpan RepaintDelay = TimeSpan.FromMilliseconds(160);

    private readonly AiSettings _settings;
    private TsvEditorControl? _view;
    private bool? _appliedLightTheme;
    private string? _lastPath;
    private string? _lastText;
    private string _newline = Environment.NewLine;
    private bool _trailingNewline;
    private int _updateSeq;
    private DocumentWatcher? _watcher;
    private TsvDocument? _document;
    private DispatcherTimer? _writeBackTimer;
    private DispatcherTimer? _repaintTimer;
    private readonly List<Cell> _highlightedCells = new();
    private string _searchTerm = "";
    private bool _searchCaseSensitive;
    private bool _searchUseRegex;

    public event EventHandler<EditorSupportContentEdited>? ContentEdited;

    public VGridEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"Grid: {Path.GetFileName(filePath)}";

    public FrameworkElement GetOrCreateView()
    {
        _view ??= new TsvEditorControl { IsVimModeEnabled = true };
        ApplyTheme(_view);
        return _view;
    }

    /// <summary>
    /// VGrid.Editor のテーマ辞書（DataGrid*Brush 等）をビュー自身の Resources へマージする。
    /// アプリ全体ではなくビューへスコープすることで、Loomo 側のテーマキーと衝突しない
    /// （ヘッダー背景も VGrid.Editor 1.0.1 から要素ツリー解決になり、このスコープで届く）。
    /// </summary>
    private void ApplyTheme(TsvEditorControl view)
    {
        var light = _settings.Theme.IsLight();
        if (_appliedLightTheme == light)
            return;

        var dict = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/VGrid.Editor;component/Themes/{(light ? "LightTheme" : "DarkTheme")}.xaml")
        };
        view.Resources.MergedDictionaries.Clear(); // ここで入れたテーマ辞書だけが入っている
        view.Resources.MergedDictionaries.Add(dict);
        _appliedLightTheme = light;
    }

    public async Task UpdateAsync(string filePath, string text)
    {
        if (_view is null)
            return;

        // 内容が変わっていなければ再パースしない。書き戻し直後のエコー（SetText → BufferChanged）も
        // ここで吸収され、グリッドのカーソル・Undo 履歴・編集状態が保たれる。
        if (filePath == _lastPath
            && VGridTextSync.NormalizeForCompare(text) == VGridTextSync.NormalizeForCompare(_lastText ?? ""))
            return;

        var seq = ++_updateSeq;
        _writeBackTimer?.Stop();   // エディタ側の変更が勝つ。未送信のグリッド編集は破棄される

        // パースとオブジェクト構築は CPU バウンドなので UI スレッドから外す（TsvFileService.LoadAsync と同じ流儀）。
        var document = await Task.Run(() => VGridTextSync.BuildDocument(filePath, text));

        // 待っている間に新しい更新が始まっていたら古い結果は捨てる。
        if (seq != _updateSeq || _view is null)
            return;

        _watcher?.Detach();

        var history = new CommandHistory();
        var gridViewModel = new TsvGridViewModel(history);
        gridViewModel.LoadDocument(document);
        var vimState = new VimState { CommandHistory = history };

        // グリッドは Tab（DataContext）差し替えのたびに、カーソル位置のセルへキーボードフォーカスを
        // 奪う（VGrid.Editor の DataGridManager.UpdateDataGridSelection → cell.Focus()）。本ビューは
        // エディタ本文の従属プレビューなので、ユーザーがエディタで打鍵 → BufferChanged 由来で再パース
        // されるたびにフォーカスを奪われると、エディタから抜けてしまう。VGrid が用意する
        // IsRestoringSession（＝プログラム的ロード時はフォーカスを取らない）を Tab 差し替えの間だけ
        // 立てて抑止する。DataContextChanged は setter 内で同期発火し、その時点の値が捕捉されるので
        // 直後に戻してよい。
        _view.IsRestoringSession = true;
        try
        {
            _view.Tab = new TabItemViewModel(filePath, document, vimState, gridViewModel);
        }
        finally
        {
            _view.IsRestoringSession = false;
        }
        _document = document;
        _lastPath = filePath;
        _lastText = text;
        // 書き戻し時に元の改行コードと末尾改行の有無を踏襲する。
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        _trailingNewline = text.EndsWith("\n");

        _watcher = new DocumentWatcher(document, ScheduleWriteBack);

        // 塗り直しの控えは前のドキュメントのセルなので捨てる（もう表示されていない）。
        _highlightedCells.Clear();
        RepaintSearchHighlight();
    }

    /// <summary>
    /// 検索パネルの検索ワードに一致するセルを塗る。VGrid はセル単位でハイライトする
    /// （<see cref="Cell.IsSearchMatch"/> → グリッドの <c>SearchHighlightBackgroundBrush</c>）ので、
    /// グリッド自身の検索（<c>/</c>・Find/Replace パネル）と同じ見え方になる。
    /// <para>
    /// 塗り直しは<b>デバウンスする</b>。条件は検索欄の打鍵ごとに届くのに対し、ここでの走査は全セル
    /// （大きな CSV なら数十万セル）を UI スレッドで舐めるので、打鍵ごとに走らせると検索欄が固まる。
    /// </para>
    /// </summary>
    public void ApplySearchHighlight(string term, bool caseSensitive, bool useRegex)
    {
        var normalized = term ?? "";
        if (normalized == _searchTerm && caseSensitive == _searchCaseSensitive && useRegex == _searchUseRegex)
            return;
        _searchTerm = normalized;
        _searchCaseSensitive = caseSensitive;
        _searchUseRegex = useRegex;
        ScheduleRepaint();
    }

    /// <summary>連続入力をまとめてから塗り直す（検索パネル側の再検索デバウンスと同じ間隔）。</summary>
    private void ScheduleRepaint()
    {
        if (_repaintTimer is null)
        {
            _repaintTimer = new DispatcherTimer { Interval = RepaintDelay };
            _repaintTimer.Tick += (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                RepaintSearchHighlight();
            };
        }
        _repaintTimer.Stop();
        _repaintTimer.Start();
    }

    /// <summary>現在の条件で塗り直す。戻すのは<b>自分が塗ったセルだけ</b>なので、グリッド自身の検索が
    /// 塗ったセルを巻き込んで消すことはない。逆向きの干渉は残る：VGrid の Find/Replace パネルは検索時に
    /// 全セルの <see cref="Cell.IsSearchMatch"/> を落とすので、そのときは検索パネル側のハイライトも消える
    /// （次に検索条件が変わるまで戻らない）。</summary>
    private void RepaintSearchHighlight()
    {
        _repaintTimer?.Stop();
        foreach (var cell in _highlightedCells)
            cell.IsSearchMatch = false;
        _highlightedCells.Clear();

        if (_document is null || _searchTerm.Length == 0)
            return;
        if (VGridTextSync.BuildCellMatcher(_searchTerm, _searchCaseSensitive, _searchUseRegex) is not { } matches)
            return;

        foreach (var row in _document.Rows)
        {
            foreach (var cell in row.Cells)
            {
                if (string.IsNullOrEmpty(cell.Value) || !matches(cell.Value))
                    continue;
                cell.IsSearchMatch = true;
                _highlightedCells.Add(cell);
                if (_highlightedCells.Count >= MaxHighlightedCells)
                    return;
            }
        }
    }


    /// <summary>グリッド編集の連続入力をまとめてから書き戻す（UI スレッドで呼ばれる）。</summary>
    private void ScheduleWriteBack()
    {
        if (_writeBackTimer is null)
        {
            _writeBackTimer = new DispatcherTimer { Interval = WriteBackDelay };
            _writeBackTimer.Tick += (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                WriteBack();
            };
        }

        _writeBackTimer.Stop();
        _writeBackTimer.Start();
    }

    private void WriteBack()
    {
        if (_document is null || _lastPath is null)
            return;

        var text = VGridTextSync.Serialize(_document, _newline, _trailingNewline);

        // グリッド余白の自動拡張（空行・空列の追加）だけなら本文は変わらない。発火しない。
        if (VGridTextSync.NormalizeForCompare(text) == VGridTextSync.NormalizeForCompare(_lastText ?? ""))
            return;

        _lastText = text;   // 先に控えておき、SetText のエコーを UpdateAsync の比較で止める
        ContentEdited?.Invoke(this, new EditorSupportContentEdited(_lastPath, text));
    }

    /// <summary>
    /// TsvDocument の内容変更（セル値・行・列）をまとめて1つのコールバックへ流す。
    /// TsvDocument 自身の購読（IsDirty 用）と同じ構造で、後から増えた行・セルにも追従する。
    /// ドキュメント差し替え時は <see cref="Detach"/> で無効化し、購読解除はせず GC に任せる
    /// （古いドキュメントごと到達不能になる）。
    /// </summary>
    private sealed class DocumentWatcher
    {
        private readonly Action _changed;
        private bool _active = true;

        public DocumentWatcher(TsvDocument document, Action changed)
        {
            _changed = changed;
            document.Rows.CollectionChanged += OnRowsChanged;
            foreach (var row in document.Rows)
                WatchRow(row);
        }

        public void Detach() => _active = false;

        private void WatchRow(Row row)
        {
            row.Cells.CollectionChanged += OnCellsChanged;
            foreach (var cell in row.Cells)
                cell.PropertyChanged += OnCellChanged;
        }

        private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
                foreach (Row row in e.NewItems)
                    WatchRow(row);
            Notify();
        }

        private void OnCellsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
                foreach (Cell cell in e.NewItems)
                    cell.PropertyChanged += OnCellChanged;
            Notify();
        }

        private void OnCellChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Cell.Value))
                Notify();
        }

        private void Notify()
        {
            if (_active)
                _changed();
        }
    }
}

/// <summary>
/// エディタ本文 ⇔ TsvDocument の変換（<see cref="VGridEditorSupport"/> の純ロジック部分）。
/// 区切り文字の判定・エスケープは VGrid 側の DelimiterStrategy に委ね、
/// 整形（末尾の空行・空セルの切り落とし）は VGrid の保存処理（TsvFileService.SaveAsync）と同じ規則。
/// </summary>
public static class VGridTextSync
{
    /// <summary>
    /// 検索ハイライト用に「このセル値は一致するか」を判定する述語を作る。VGrid はセル単位で塗るので
    /// 部分一致すればそのセル全体が一致扱い。入力途中の不正な正規表現は <c>null</c>（＝塗らない）。
    /// </summary>
    public static Func<string, bool>? BuildCellMatcher(string term, bool caseSensitive, bool useRegex)
    {
        if (!useRegex)
        {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return value => value.Contains(term, comparison);
        }

        try
        {
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var regex = new Regex(term, options, TimeSpan.FromMilliseconds(100));
            return value =>
            {
                try { return regex.IsMatch(value); }
                catch (RegexMatchTimeoutException) { return false; }   // 病的な式は塗らないだけ
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>エディタ本文から TsvDocument を組み立てる（区切りは拡張子から判定）。</summary>
    public static TsvDocument BuildDocument(string filePath, string text)
    {
        var format = DelimiterStrategyFactory.DetectFromExtension(filePath);
        var strategy = DelimiterStrategyFactory.Create(format);

        var parsedRows = strategy.ParseContent(text);
        var rows = new List<Row>(parsedRows.Count);
        for (int i = 0; i < parsedRows.Count; i++)
            rows.Add(new Row(i, parsedRows[i]));

        var document = new TsvDocument(rows)
        {
            FilePath = filePath,
            IsDirty = false,
            DelimiterFormat = format
        };
        // 実データの少し先まで余白を確保（VGrid 本体と同じ初期サイズ方針）。
        document.EnsureSize(Math.Max(document.RowCount + 5, 20), Math.Max(document.ColumnCount + 3, 15));
        return document;
    }

    /// <summary>
    /// TsvDocument をエディタ本文へ戻す。末尾の空行・空セル（グリッドの余白）は出力しない。
    /// </summary>
    public static string Serialize(TsvDocument document, string newline, bool trailingNewline)
    {
        var strategy = DelimiterStrategyFactory.Create(document.DelimiterFormat);
        var lines = new List<string>();

        int lastNonEmptyRow = -1;
        for (int i = document.Rows.Count - 1; i >= 0; i--)
        {
            if (document.Rows[i].Cells.Any(c => !string.IsNullOrEmpty(c.Value)))
            {
                lastNonEmptyRow = i;
                break;
            }
        }

        for (int i = 0; i <= lastNonEmptyRow; i++)
        {
            var row = document.Rows[i];
            int lastNonEmptyCol = -1;
            for (int j = row.Cells.Count - 1; j >= 0; j--)
            {
                if (!string.IsNullOrEmpty(row.Cells[j].Value))
                {
                    lastNonEmptyCol = j;
                    break;
                }
            }

            lines.Add(lastNonEmptyCol >= 0
                ? strategy.FormatLine(row.Cells.Take(lastNonEmptyCol + 1).Select(c => c.Value ?? string.Empty))
                : string.Empty);
        }

        var text = string.Join(newline, lines);
        return trailingNewline && lines.Count > 0 ? text + newline : text;
    }

    /// <summary>
    /// 「内容として同じか」の比較用正規化。改行コードの違いと末尾の空行は無視する
    /// （書き戻しのエコー検出と、グリッド余白拡張の無視に使う）。
    /// </summary>
    public static string NormalizeForCompare(string text)
        => text.Replace("\r\n", "\n").TrimEnd('\n');
}
