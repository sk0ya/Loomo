using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VGrid.Commands;
using VGrid.Editor;
using VGrid.Models;
using VGrid.Services;
using VGrid.ViewModels;
using VGrid.VimEngine;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Excel ブック（.xlsx / .xlsm）の読み取り専用プレビュー。<see cref="ExcelSheetReader"/> で全ワークシートを
/// 読み、ワークシートごとのタブを下に並べ、<b>1つだけ</b>の VGrid グリッド（<see cref="TsvEditorControl"/>）に
/// 選択中シートを表示する（CSV/TSV の <see cref="VGridEditorSupport"/> と同じグリッドを流用）。エディタ本文は
/// 使わず（<see cref="UsesEditorText"/> = false）、ファイルパスから直接読む。表示専用で書き戻しはしない。
/// 横スクロールは DataGrid 内部の ScrollViewer に対して ShellWindow の WM_MOUSEHWHEEL フックが効く。
/// </summary>
public sealed class ExcelEditorSupport : IEditorSupportVisualProvider
{
    private static readonly string[] Extensions = [".xlsx", ".xlsm"];

    private readonly AiSettings _settings;

    private Grid? _view;
    private TsvEditorControl? _grid;
    private ListBox? _tabStrip;
    private TextBlock? _message;
    private bool? _appliedLightTheme;

    private string? _lastPath;
    private IReadOnlyList<ExcelSheet> _sheets = Array.Empty<ExcelSheet>();
    private TsvDocument?[] _docs = Array.Empty<TsvDocument?>();
    private bool _suppressTabEvent;
    private int _updateSeq;

    public ExcelEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    // .xlsx は ZIP バイナリ。エディタ本文は使わず、ファイルパスから直接読む。
    public bool UsesEditorText => false;

    // 読み取り専用なので書き戻しは無い（購読は受けるが発火しない）。
    public event EventHandler<EditorSupportContentEdited>? ContentEdited { add { } remove { } }

    public string DescribeTitle(string filePath) => $"Excel: {Path.GetFileName(filePath)}";

    public FrameworkElement GetOrCreateView()
    {
        if (_view is null)
        {
            _grid = new TsvEditorControl { IsVimModeEnabled = true };

            _tabStrip = new ListBox
            {
                SelectionMode = SelectionMode.Single,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0),
                ItemsPanel = HorizontalPanelTemplate(),
                ItemContainerStyle = TabItemStyle(),
            };
            _tabStrip.SetResourceReference(Control.BackgroundProperty, "Panel");
            _tabStrip.SetResourceReference(Control.BorderBrushProperty, "Border");
            ScrollViewer.SetHorizontalScrollBarVisibility(_tabStrip, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_tabStrip, ScrollBarVisibility.Disabled);
            _tabStrip.SelectionChanged += OnTabSelectionChanged;

            _message = new TextBlock
            {
                Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Visibility = Visibility.Collapsed,
            };
            _message.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");

            _view = new Grid();
            _view.SetResourceReference(Panel.BackgroundProperty, "Panel");
            _view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _view.Children.Add(_grid);
            Grid.SetRow(_grid, 0);
            _view.Children.Add(_message);
            Grid.SetRow(_message, 0);
            _view.Children.Add(_tabStrip);
            Grid.SetRow(_tabStrip, 1);
        }

        ApplyTheme();
        return _view;
    }

    /// <summary>
    /// VGrid.Editor のテーマ辞書をグリッド自身の Resources へマージする（<see cref="VGridEditorSupport"/> と同じ）。
    /// </summary>
    private void ApplyTheme()
    {
        if (_grid is null)
            return;

        var light = _settings.Theme == AppTheme.Light;
        if (_appliedLightTheme == light)
            return;

        var dict = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/VGrid.Editor;component/Themes/{(light ? "LightTheme" : "DarkTheme")}.xaml")
        };
        _grid.Resources.MergedDictionaries.Clear();
        _grid.Resources.MergedDictionaries.Add(dict);
        _appliedLightTheme = light;
    }

    public async Task UpdateAsync(string filePath, string text)
    {
        GetOrCreateView();

        // 同じファイルを再表示する要求（サムネイル再描画など）は読み直さない。
        if (filePath == _lastPath && _sheets.Count > 0)
            return;

        var seq = ++_updateSeq;

        IReadOnlyList<ExcelSheet> sheets;
        try
        {
            // ClosedXML の読み込み（IO＋パース）は UI スレッドから外す。
            sheets = await Task.Run(() => ExcelSheetReader.Read(filePath));
        }
        catch (Exception ex)
        {
            if (seq == _updateSeq)
                ShowMessage($"このファイルを表示できませんでした：{ex.Message}");
            return;
        }

        if (seq != _updateSeq)
            return; // 後続の更新が最新を描く

        _lastPath = filePath;
        _sheets = sheets;
        _docs = new TsvDocument?[sheets.Count];

        if (sheets.Count == 0)
        {
            ShowMessage("ワークシートがありません。");
            return;
        }

        HideMessage();
        PopulateTabs(sheets);
        ShowSheet(0);
    }

    private void PopulateTabs(IReadOnlyList<ExcelSheet> sheets)
    {
        if (_tabStrip is null)
            return;

        _suppressTabEvent = true;
        _tabStrip.Items.Clear();
        foreach (var sheet in sheets)
            _tabStrip.Items.Add(sheet.Name);
        _tabStrip.SelectedIndex = 0;
        _suppressTabEvent = false;

        // シートが1つだけならタブ帯は出さない。
        _tabStrip.Visibility = sheets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabEvent || _tabStrip is null)
            return;
        var index = _tabStrip.SelectedIndex;
        if (index >= 0)
            ShowSheet(index);
    }

    private void ShowSheet(int index)
    {
        if (_grid is null || index < 0 || index >= _sheets.Count)
            return;

        var doc = _docs[index] ??= BuildDocument(_lastPath ?? string.Empty, _sheets[index]);

        var history = new CommandHistory();
        var gridViewModel = new TsvGridViewModel(history);
        gridViewModel.LoadDocument(doc);
        var vimState = new VimState { CommandHistory = history };

        // Tab 差し替えのたびにカーソルセルへフォーカスを奪われるのを、プログラム的ロードの間だけ抑止する
        // （VGridEditorSupport と同じ理由・同じ手法）。
        _grid.IsRestoringSession = true;
        try
        {
            _grid.Tab = new TabItemViewModel(_sheets[index].Name, doc, vimState, gridViewModel);
        }
        finally
        {
            _grid.IsRestoringSession = false;
        }
        HideMessage();
    }

    private static TsvDocument BuildDocument(string filePath, ExcelSheet sheet)
    {
        var rows = new List<Row>(sheet.Rows.Count);
        for (var i = 0; i < sheet.Rows.Count; i++)
            rows.Add(new Row(i, sheet.Rows[i]));

        var document = new TsvDocument(rows)
        {
            FilePath = filePath,
            IsDirty = false,
            DelimiterFormat = DelimiterFormat.Tsv,
        };
        // 実データの少し先まで余白を確保（VGrid 本体と同じ初期サイズ方針）。
        document.EnsureSize(Math.Max(document.RowCount + 2, 20), Math.Max(document.ColumnCount + 1, 8));
        return document;
    }

    private void ShowMessage(string text)
    {
        if (_message is null || _grid is null)
            return;
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        _grid.Visibility = Visibility.Collapsed;
        if (_tabStrip is not null)
            _tabStrip.Visibility = Visibility.Collapsed;
    }

    private void HideMessage()
    {
        if (_message is not null)
            _message.Visibility = Visibility.Collapsed;
        if (_grid is not null)
            _grid.Visibility = Visibility.Visible;
    }

    private static ItemsPanelTemplate HorizontalPanelTemplate()
    {
        var factory = new System.Windows.FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        return new ItemsPanelTemplate { VisualTree = factory };
    }

    /// <summary>
    /// ワークシートタブ1つ分のスタイル。色は Loomo のテーマブラシ（DynamicResource）で描き、テーマ切替に
    /// 追従させる：既定は <c>FgDim</c>、ホバーで <c>Fg</c>＋<c>BgAlt</c>、選択タブは <c>Accent</c> 背景＋
    /// <c>AccentFg</c> 文字。既定テンプレートの OS 依存のハイライト（黒文字・システム青）は使わない。
    /// </summary>
    private static Style TabItemStyle()
    {
        var border = new System.Windows.FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 0));
        border.SetResourceReference(Border.BorderBrushProperty, "Border");

        var content = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };

        // 選択タブ：Accent 背景＋AccentFg 文字（Foreground は継承で ContentPresenter の文字へ届く）。
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Accent"), "bd"));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("AccentFg")));
        template.Triggers.Add(selected);

        // 非選択のホバー：BgAlt 背景＋Fg 文字。
        var hover = new MultiTrigger();
        hover.Conditions.Add(new Condition(ListBoxItem.IsMouseOverProperty, true));
        hover.Conditions.Add(new Condition(ListBoxItem.IsSelectedProperty, false));
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgAlt"), "bd"));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Fg")));
        template.Triggers.Add(hover);

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 3, 10, 3)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgDim")));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        return style;
    }
}
