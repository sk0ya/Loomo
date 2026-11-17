using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// Loomo の確認・通知ダイアログ。<see cref="System.Windows.MessageBox"/> と同じ呼び方・同じ戻り値で、
/// 見た目だけを部屋の中の一枚（自前のキャプション・パレットの色・Controls.xaml のボタン）に揃える。
/// 取り消せない操作（警告アイコンで、ボタンが OK だけでないもの）は実行ボタンを危険色にし、
/// 既定のフォーカスは「やめる」側に置く。
/// </summary>
public partial class LoomoMessageBox : Window
{
    private readonly MessageBoxResult _closeResult;
    private MessageBoxResult _result;

    private LoomoMessageBox(string caption, string text, MessageBoxButton buttons,
        MessageBoxImage icon, MessageBoxResult? defaultResult)
    {
        InitializeComponent();

        Title = caption;
        CaptionText.Text = caption;
        MessageText.Text = text;
        ApplyIcon(icon);

        var hasCancel = buttons is MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel;
        // 閉じる（×・Esc）の結果：やめる側があればそれ、OK だけなら OK。
        _closeResult = buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };
        _result = _closeResult;

        // 取り消せない操作（警告の確認）は既定を「やめる」にして、Enter の誤爆を防ぐ。
        var danger = icon == MessageBoxImage.Warning && buttons != MessageBoxButton.OK;
        var fallbackDefault = danger && hasCancel ? MessageBoxResult.Cancel : (MessageBoxResult?)null;
        var focusResult = defaultResult ?? fallbackDefault ?? PrimaryResult(buttons);

        foreach (var (label, result, primary) in ButtonsFor(buttons))
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource(primary ? (danger ? "FlatDangerButton" : "FlatButton") : "SecondaryButton"),
                IsDefault = result == focusResult,
                IsCancel = result == _closeResult,
                Tag = result,
            };
            button.Click += OnButtonClick;
            ButtonRow.Children.Add(button);
        }

        Loaded += (_, _) =>
        {
            var focus = ButtonRow.Children.OfType<Button>().FirstOrDefault(b => b.IsDefault);
            focus?.Focus();
        };
    }

    /// <summary>
    /// 確認・通知を出して、押されたボタンの結果を返す。owner が null のときは今アクティブな窓の上に出す。
    /// </summary>
    public static MessageBoxResult Show(Window? owner, string text, string caption,
        MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult? defaultResult)
    {
        var dialog = Create(text, caption, buttons, icon, defaultResult);
        var parent = owner ?? ActiveWindow();
        if (parent is not null && parent.IsLoaded)
        {
            dialog.Owner = parent;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>出さずに組み立てるだけの窓を返す（ViewHarness が見た目を撮るため）。通常は <see cref="Show(Window?, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult?)"/> を使う。</summary>
    public static LoomoMessageBox Create(string text, string caption, MessageBoxButton buttons,
        MessageBoxImage icon, MessageBoxResult? defaultResult) =>
        new(caption, text, buttons, icon, defaultResult);

    /// <summary>窓の中身（キャプション・本文・ボタン）を窓から外して返す。ハーネスで別の枠に載せて撮るため。</summary>
    public FrameworkElement DetachContentForPreview()
    {
        var content = (FrameworkElement)Content;
        Content = null;
        return content;
    }

    public static MessageBoxResult Show(string text) =>
        Show(null, text, "Loomo", MessageBoxButton.OK, MessageBoxImage.None, null);

    public static MessageBoxResult Show(string text, string caption) =>
        Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None, null);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons) =>
        Show(null, text, caption, buttons, MessageBoxImage.None, null);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) =>
        Show(null, text, caption, buttons, icon, null);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons,
        MessageBoxImage icon, MessageBoxResult defaultResult) =>
        Show(null, text, caption, buttons, icon, defaultResult);

    public static MessageBoxResult Show(Window? owner, string text) =>
        Show(owner, text, "Loomo", MessageBoxButton.OK, MessageBoxImage.None, null);

    public static MessageBoxResult Show(Window? owner, string text, string caption) =>
        Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None, null);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons) =>
        Show(owner, text, caption, buttons, MessageBoxImage.None, null);

    public static MessageBoxResult Show(Window? owner, string text, string caption,
        MessageBoxButton buttons, MessageBoxImage icon) =>
        Show(owner, text, caption, buttons, icon, null);

    public static MessageBoxResult Show(Window? owner, string text, string caption,
        MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult) =>
        Show(owner, text, caption, buttons, icon, (MessageBoxResult?)defaultResult);

    private static Window? ActiveWindow() =>
        System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? System.Windows.Application.Current?.MainWindow;

    /// <summary>ボタンの並び（左→右）。主の操作は右端。</summary>
    private static (string Label, MessageBoxResult Result, bool Primary)[] ButtonsFor(MessageBoxButton buttons) =>
        buttons switch
        {
            MessageBoxButton.OK => [("OK", MessageBoxResult.OK, true)],
            MessageBoxButton.OKCancel => [("キャンセル", MessageBoxResult.Cancel, false), ("OK", MessageBoxResult.OK, true)],
            MessageBoxButton.YesNo => [("いいえ", MessageBoxResult.No, false), ("はい", MessageBoxResult.Yes, true)],
            _ => [("キャンセル", MessageBoxResult.Cancel, false), ("いいえ", MessageBoxResult.No, false), ("はい", MessageBoxResult.Yes, true)],
        };

    private static MessageBoxResult PrimaryResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OK or MessageBoxButton.OKCancel => MessageBoxResult.OK,
        _ => MessageBoxResult.Yes,
    };

    private void ApplyIcon(MessageBoxImage icon)
    {
        // パレットに警告色は無いので、警告と誤りは Error 色を使い、記号の形で区別する。
        // 記号は地の円より明るい色で描く（円の上の記号が読めるように）。
        (string glyph, string discKey, string glyphKey) = icon switch
        {
            MessageBoxImage.Error => ("", "Error", "Error"),                // ErrorBadge
            MessageBoxImage.Warning => ("", "Error", "Error"),              // Warning
            MessageBoxImage.Question => ("", "Accent", "AccentHover"),      // Help
            _ => ("", "Accent", "AccentHover"),                             // Info
        };
        IconGlyph.Text = glyph;
        IconGlyph.SetResourceReference(TextElement.ForegroundProperty, glyphKey);
        IconDisc.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, discKey);
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        _result = (MessageBoxResult)((Button)sender).Tag!;
        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _result = _closeResult;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _result = _closeResult;
            Close();
            e.Handled = true;
        }
    }
}
