using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>「自分のテーマ」編集画面の中身。設定・キーボードと同じ <see cref="SettingsWindow"/>（自前キャプション）に載せる。
/// 見本は種類ごとに 1 つ（アプリの画面・エディタ・ターミナル・Markdown プレビュー）で、<see cref="PreviewRoot"/> の
/// Resources に決まった色を置き、中は DynamicResource で塗る（アプリの画面では部屋の本物のスタイル＝FlatButton 等も
/// そのまま試し塗りされる）。部屋を見ながら詰められるよう、窓はモードレスで 1 枚だけ。</summary>
public partial class UserThemeEditorView : UserControl
{
    private static SettingsWindow? _open;

    private readonly UserThemeEditorViewModel _vm;
    private readonly Popup _pickerPopup;
    private readonly ColorPicker _picker = new();
    private readonly TextBlock _pickerTitle = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _autoButton = new() { Content = "自動に戻す", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private ColorSlotViewModel? _activeSlot;

    /// <summary>その種類で開く（開いていれば前へ出して、その種類へ切り替える）。</summary>
    public static void Open(Window? owner, AppearanceViewModel appearance, CustomThemeKind kind)
    {
        if (_open is { Body.Content: UserThemeEditorView openView } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            openView._vm.SelectedKind = openView._vm.Kinds.First(k => k.Kind == kind);
            return;
        }
        var view = new UserThemeEditorView(new UserThemeEditorViewModel(appearance, kind));
        var window = new SettingsWindow(view, "自分のテーマ")
        {
            Owner = owner,
            Width = 1180,
            Height = 740,
            MinWidth = 960,
            MinHeight = 560,
            ShowInTaskbar = true,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        window.Closing += view.OnWindowClosing;
        window.Closed += (_, _) => { view._vm.Dispose(); _open = null; };
        _open = window;
        window.Show();
    }

    private UserThemeEditorView(UserThemeEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _autoButton.SetResourceReference(StyleProperty, "SecondaryButton");
        _autoButton.Click += (_, _) => { _activeSlot?.ResetToAuto(); _pickerPopup!.IsOpen = false; };
        var body = new StackPanel();
        body.Children.Add(_pickerTitle);
        body.Children.Add(_picker);
        body.Children.Add(_autoButton);
        var frame = new Border
        {
            Child = body,
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8),   // 影の分
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35 },
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Panel");
        frame.SetResourceReference(Border.BorderBrushProperty, "Border");
        frame.SetResourceReference(TextElement.ForegroundProperty, "Fg");
        _pickerPopup = new Popup
        {
            Child = frame,
            StaysOpen = false,
            Placement = PlacementMode.Right,
            AllowsTransparency = true,
            HorizontalOffset = 8,
            PopupAnimation = PopupAnimation.Fade,
        };
        _pickerPopup.Closed += (_, _) => { BindingOperations.ClearBinding(_picker, ColorPicker.SelectedColorProperty); _activeSlot = null; ShowUsage(null); };

        vm.PropertyChanged += OnVmPropertyChanged;
        ApplyPreview(vm.Preview);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserThemeEditorViewModel.Preview)) ApplyPreview(_vm.Preview);
        else if (e.PropertyName == nameof(UserThemeEditorViewModel.SelectedKind)) { _pickerPopup.IsOpen = false; ShowUsage(null); }
    }

    /// <summary>決まった色を見本の Resources に置き、その種類の見本だけを出す。アプリの画面はパレットのキー
    /// （ペインヘッダーの帯も <see cref="AppPaletteBuilder"/> が実行時と同じ式で作る）、エディタは
    /// <see cref="EditorThemePresets.FromCustom"/> が作る本物の配色、ほかは欄の鍵に C_ を付けたキー。
    /// ピッカーのドラッグでは色が立て続けに変わるので、描き直しは 1 フレームに 1 回へまとめ、辞書は差し替えずに
    /// 変わった鍵だけ書き換える（辞書ごと替えると見本の DynamicResource が全部引き直しになる）。</summary>
    private void ApplyPreview(CustomTheme? theme)
    {
        _pendingPreview = theme;
        if (_previewQueued) return;
        _previewQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _previewQueued = false;
            ApplyPreviewNow(_pendingPreview);
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private CustomTheme? _pendingPreview;
    private bool _previewQueued;
    private readonly ResourceDictionary _previewResources = new();
    private readonly Dictionary<string, Color> _previewColors = new();

    private void ApplyPreviewNow(CustomTheme? theme)
    {
        AppPreview.Visibility = Show(CustomThemeKind.App);
        EditorPreview.Visibility = Show(CustomThemeKind.Editor);
        TerminalPreview.Visibility = Show(CustomThemeKind.Terminal);
        PreviewPreview.Visibility = Show(CustomThemeKind.Preview);
        if (theme is null) return;

        void Put(string key, Color c)
        {
            if (_previewColors.TryGetValue(key, out var old) && old == c) return;
            _previewColors[key] = c;
            _previewResources[key] = Frozen(c);
        }
        void PutBrush(string key, Brush brush) => Put(key, brush is SolidColorBrush b ? b.Color : Colors.Magenta);

        foreach (var (key, color) in theme.Colors) Put("C_" + key, color);
        switch (theme.Kind)
        {
            case CustomThemeKind.App:
                foreach (var (key, color) in AppPaletteBuilder.Build(theme)) Put(key, color);
                break;
            case CustomThemeKind.Editor:
                // 本物のエディタと同じ配色（行番号の帯・現在行・選択・検索・下の帯の文字）。
                var editor = EditorThemePresets.FromCustom(theme);
                PutBrush("E_LineBg", editor.LineNumberBg);
                PutBrush("E_CurrentLine", editor.CurrentLineBg);
                PutBrush("E_Selection", editor.SelectionBg);
                PutBrush("E_Search", editor.SearchHighlightBg);
                PutBrush("E_StatusFg", editor.StatusBarFg);
                ErrorRun.TextDecorations = new TextDecorationCollection
                {
                    new TextDecoration(TextDecorationLocation.Underline,
                        new Pen(Frozen(theme["error"]), 1.5) { DashStyle = new DashStyle(new double[] { 2, 1 }, 0) },
                        1, TextDecorationUnit.Pixel, TextDecorationUnit.Pixel),
                };
                break;
        }
        if (!ReferenceEquals(PreviewRoot.Resources, _previewResources)) PreviewRoot.Resources = _previewResources;
        PreviewRoot.Background = Frozen(theme["background"]);
        _usageStroke = Frozen(theme.IsLight ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White);
        // 色を変えると見本の大きさも変わりうるので、囲みは並べ直しが済んでから描き直す。
        if (_shownSlot is not null)
            Dispatcher.BeginInvoke(() => ShowUsage(_shownSlot), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private Visibility Show(CustomThemeKind kind) => _vm.Kind == kind ? Visibility.Visible : Visibility.Collapsed;

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>色のタイルを押したら、その欄の色にピッカーを双方向で繋いで横に出す。</summary>
    private void OnSlotClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ColorSlotViewModel slot } target) return;
        BindingOperations.ClearBinding(_picker, ColorPicker.SelectedColorProperty);
        _activeSlot = slot;
        _pickerTitle.Text = slot.Label;
        _autoButton.Visibility = slot.CanBeAuto ? Visibility.Visible : Visibility.Collapsed;
        _picker.SelectedColor = slot.Color;
        _picker.Palette = _vm.PaletteFor(slot).ToArray();
        BindingOperations.SetBinding(_picker, ColorPicker.SelectedColorProperty,
            new Binding(nameof(ColorSlotViewModel.Color)) { Source = slot, Mode = BindingMode.TwoWay });
        _pickerPopup.PlacementTarget = target;
        _pickerPopup.IsOpen = true;
        ShowUsage(slot);
    }

    // ===== 色の使われ所をプレビューで囲む =====

    private ColorSlotViewModel? _shownSlot;
    private Brush _usageStroke = Brushes.White;

    private void OnSlotMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ColorSlotViewModel slot }) ShowUsage(slot);
    }

    /// <summary>カーソルが離れたら、ピッカーで選んでいる欄があればそちらへ戻す。</summary>
    private void OnSlotMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ShowUsage(_activeSlot);

    /// <summary>その欄の色を使っている見本（Tag="use:…" に鍵を含むもの）を点線で囲み、見出しに何の色かを出す。
    /// null なら囲みを消す。</summary>
    private void ShowUsage(ColorSlotViewModel? slot)
    {
        _shownSlot = slot;
        UsageLayer.Children.Clear();
        if (slot is null)
        {
            PreviewCaption.Text = "見本";
            return;
        }
        var count = 0;
        foreach (var element in Tagged(PreviewRoot))
        {
            if (element.Tag is not string tag || !tag.StartsWith("use:", StringComparison.Ordinal)
                || !tag[4..].Split(' ').Contains(slot.Key) || !element.IsVisible) continue;
            var bounds = element.TransformToVisual(UsageLayer).TransformBounds(new Rect(element.RenderSize));
            bounds.Inflate(2, 2);
            var box = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(0, bounds.Width),
                Height = Math.Max(0, bounds.Height),
                Stroke = _usageStroke,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                RadiusX = 4,
                RadiusY = 4,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, slot.Color.R, slot.Color.G, slot.Color.B)),
            };
            Canvas.SetLeft(box, bounds.X);
            Canvas.SetTop(box, bounds.Y);
            UsageLayer.Children.Add(box);
            count++;
        }
        PreviewCaption.Text = count > 0
            ? $"見本 — 「{slot.Label}」が使われている所を点線で囲んでいます"
            : $"見本 — 「{slot.Label}」はこの見本には出てきません";
    }

    private static IEnumerable<FrameworkElement> Tagged(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: string } fe) yield return fe;
            foreach (var nested in Tagged(child)) yield return nested;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // 自動保存の書き残しをここで書き切る。書けなかったときだけ、閉じてよいか聞く。
        if (!_vm.Flush() && MessageBox.Show("変更を保存できませんでした。保存せずに閉じますか？", "自分のテーマ",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            e.Cancel = true;
    }
}
