using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
public sealed class VGridEditorSupport : IEditorSupportVisualProvider
{
    /// <summary>グリッド編集をエディタ本文へまとめて書き戻すまでの猶予。</summary>
    private static readonly TimeSpan WriteBackDelay = TimeSpan.FromMilliseconds(500);

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

    public event EventHandler<EditorSupportContentEdited>? ContentEdited;

    public VGridEditorSupport(AiSettings settings) => _settings = settings;

    public bool CanSupport(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() is ".csv" or ".tsv";

    public string DescribeTitle(string filePath) => $"Grid: {Path.GetFileName(filePath)}";

    public FrameworkElement GetOrCreateView()
    {
        _view ??= new TsvEditorControl { IsVimModeEnabled = true };
        ApplyTheme(_view);
        return _view;
    }

    /// <summary>
    /// VGrid.Editor のテーマ辞書（DataGrid*Brush 等）をビュー自身の Resources へマージする。
    /// アプリ全体ではなくビューへスコープすることで、Loomo 側のテーマキーと衝突しない。
    /// 例外：ヘッダー背景の3キーは VGrid のコンバータが Application.Current.Resources から
    /// 直接引くため、その3つだけアプリ直下へも複製する（Loomo は同名キーを使っていない）。
    /// </summary>
    private void ApplyTheme(TsvEditorControl view)
    {
        var light = _settings.Theme == AppTheme.Light;
        if (_appliedLightTheme == light)
            return;

        var dict = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/VGrid.Editor;component/Themes/{(light ? "LightTheme" : "DarkTheme")}.xaml")
        };
        view.Resources.MergedDictionaries.Clear(); // ここで入れたテーマ辞書だけが入っている
        view.Resources.MergedDictionaries.Add(dict);

        if (Application.Current is { } app)
        {
            foreach (var key in new[]
                     {
                         "DataGridHeaderBackgroundBrush",
                         "DataGridCurrentRowHeaderBrush",
                         "DataGridCurrentColumnHeaderBrush",
                     })
            {
                if (dict[key] is Brush brush)
                    app.Resources[key] = brush;
            }
        }

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

        _view.Tab = new TabItemViewModel(filePath, document, vimState, gridViewModel);
        _document = document;
        _lastPath = filePath;
        _lastText = text;
        // 書き戻し時に元の改行コードと末尾改行の有無を踏襲する。
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        _trailingNewline = text.EndsWith("\n");

        _watcher = new DocumentWatcher(document, ScheduleWriteBack);
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
