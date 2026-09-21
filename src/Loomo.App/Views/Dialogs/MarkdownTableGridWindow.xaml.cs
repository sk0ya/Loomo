using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using sk0ya.Loomo.Core.Markdown;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Models;
using VGrid.Commands;
using VGrid.Editor;
using VGrid.Models;
using VGrid.Services;
using VGrid.ViewModels;
using VGrid.VimEngine;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// Markdown テーブルを VGrid のグリッド（<see cref="TsvEditorControl"/>）で編集・新規作成するモーダルウィンドウ。
/// 開くときに <see cref="MarkdownTableRegion"/> の行列をグリッドへ流し込み、閉じるときに編集後の行列を
/// <see cref="ResultRows"/> として公開する（呼び元が Markdown へ再生成して本文へ反映する）。
/// 「反映して閉じる」/ ✕ で <see cref="Apply"/>=true、「キャンセル」で false。
/// 新規作成（挿入）は <see cref="Insert"/> — 空グリッドで開き、呼び元がカーソル位置へ挿入する。
/// </summary>
public partial class MarkdownTableGridWindow : Window
{
    private readonly TsvEditorControl _grid;
    private readonly TsvDocument _document;

    /// <summary>閉じたあとに編集内容を本文へ反映するか（キャンセル時のみ false）。</summary>
    public bool Apply { get; private set; } = true;

    private MarkdownTableGridWindow(MarkdownTableRegion region, AppTheme theme)
    {
        InitializeComponent();

        _document = VGridTextSync.BuildDocument("table.md", region.Rows, DelimiterFormat.Tsv);
        _grid = new TsvEditorControl { IsVimModeEnabled = true };
        ApplyTheme(_grid, theme);

        var history = new CommandHistory();
        var gridViewModel = new TsvGridViewModel(history);
        gridViewModel.LoadDocument(_document);
        var vimState = new VimState { CommandHistory = history };
        _grid.Tab = new TabItemViewModel("table.md", _document, vimState, gridViewModel);

        GridHost.Child = _grid;
    }

    /// <summary>テーブル編集ウィンドウを開く。反映が選ばれたら編集後の行列を返す（キャンセル時は null）。</summary>
    public static IReadOnlyList<IReadOnlyList<string>>? Edit(
        Window owner, MarkdownTableRegion region, AppTheme theme)
    {
        var window = new MarkdownTableGridWindow(region, theme) { Owner = owner };
        window.ShowDialog();
        return window.Apply ? window.ResultRows : null;
    }

    /// <summary>
    /// 空のグリッドでテーブル新規作成ウィンドウを開く。反映が選ばれたら入力後の行列を返す（キャンセル時は null）。
    /// 1 行目がヘッダとして扱われる（<see cref="MarkdownTableSync.SerializeTable"/> の規約）。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>>? Insert(Window owner, AppTheme theme)
    {
        var region = MarkdownTableGridResultMapper.CreateEmptyRegion();
        var window = new MarkdownTableGridWindow(region, theme)
        {
            Owner = owner,
            Title = "テーブルを挿入",
        };
        window.DescriptionText.Text =
            "グリッドでセルを入力できます（Vim キーバインド対応。1 行目がヘッダになります）。" +
            "閉じるとカーソル位置へ Markdown テーブルとして挿入します。";
        window.ShowDialog();
        return window.Apply ? window.ResultRows : null;
    }

    /// <summary>グリッドの現在値を行列として取り出す（末尾の空行・空列は <see cref="MarkdownTableSync"/> 側で整形）。</summary>
    private IReadOnlyList<IReadOnlyList<string>> ResultRows =>
        MarkdownTableGridResultMapper.ToRows(_document);

    /// <summary>VGrid.Editor のテーマ辞書をグリッド自身の Resources へマージする（<see cref="VGridEditorSupport"/> と同じ流儀）。</summary>
    private static void ApplyTheme(TsvEditorControl grid, AppTheme theme)
    {
        var light = theme.IsLight();
        var dict = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/VGrid.Editor;component/Themes/{(light ? "LightTheme" : "DarkTheme")}.xaml")
        };
        grid.Resources.MergedDictionaries.Clear();
        grid.Resources.MergedDictionaries.Add(dict);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Apply = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Apply = false;
        Close();
    }
}
