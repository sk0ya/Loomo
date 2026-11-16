using System;
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
/// プレビューは <see cref="PreviewRoot"/> の Resources に派生した色を置き、中の見本は DynamicResource で塗る
/// （部屋の本物のスタイル＝FlatButton 等もそのまま試し塗りされる）。部屋を見ながら詰められるよう、窓はモードレスで 1 枚だけ。</summary>
public partial class UserThemeEditorView : UserControl
{
    private static SettingsWindow? _open;

    private readonly UserThemeEditorViewModel _vm;
    private readonly Popup _pickerPopup;
    private readonly ColorPicker _picker = new();
    private readonly TextBlock _pickerTitle = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _autoButton = new() { Content = "自動に戻す", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private ColorSlotViewModel? _activeSlot;

    /// <summary>開く（開いていれば前へ出す）。</summary>
    public static void Open(Window? owner, AppearanceViewModel appearance)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var view = new UserThemeEditorView(new UserThemeEditorViewModel(appearance));
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
        _pickerPopup.Closed += (_, _) => { BindingOperations.ClearBinding(_picker, ColorPicker.SelectedColorProperty); _activeSlot = null; };

        vm.PropertyChanged += OnVmPropertyChanged;
        ApplyPreview(vm.Preview);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserThemeEditorViewModel.Preview)) ApplyPreview(_vm.Preview);
    }

    /// <summary>派生した色をプレビューの Resources に置く。ペインヘッダーの帯は部屋と同じく実行時の
    /// アクセントの色味（<see cref="ThemeManager.PaneHeaderTint"/>）で作る（パレットの値は灰色の既定）。</summary>
    private void ApplyPreview(UserTheme? theme)
    {
        if (theme is null) return;
        var dict = new ResourceDictionary();
        void Put(string key, Color c) => dict[key] = Frozen(c);
        foreach (var (key, color) in theme.Ui) Put(key, color);
        Put("PaneHeaderBg", ThemeManager.PaneHeaderTint(theme.Ui["Panel"], theme.Accent, theme.Dim));

        Put("PvEditorBg", theme.Bg);
        Put("PvEditorFg", theme.Fg);
        Put("PvComment", theme.Dim);
        Put("PvKeyword", theme.Keyword);
        Put("PvString", theme.String);
        Put("PvNumber", theme.Number);
        Put("PvType", theme.Type);
        Put("PvFunction", theme.Function);
        Put("PvTermBg", theme.Bg);
        Put("PvTermFg", theme.Fg);
        for (var i = 0; i < theme.Ansi.Count; i++) Put($"PvAnsi{i}", theme.Ansi[i]);
        PreviewRoot.Resources = dict;
        AnsiRow.ItemsSource = theme.Ansi.Select(Frozen).ToArray();
    }

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
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmLeave()) e.Cancel = true;
    }
}
