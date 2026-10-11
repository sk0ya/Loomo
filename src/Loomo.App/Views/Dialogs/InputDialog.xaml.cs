using System;
using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>名前入力用の汎用モーダルダイアログ（リネーム・新規作成で使用）。</summary>
public partial class InputDialog : Window
{
    private InputDialog()
    {
        InitializeComponent();
    }

    /// <summary>空入力（OK）を許すか。条件編集など「空にして解除」を許す用途で true にする。</summary>
    private bool _allowEmpty;

    /// <summary>ダイアログを開き、入力された名前（前後空白を除去）を返す。キャンセル時は null。</summary>
    /// <param name="initial">初期値。</param>
    /// <param name="selectNameOnly">true なら拡張子を除いた部分だけを選択（ファイル名のリネーム向け）。</param>
    /// <param name="allowEmpty">true なら空入力でも OK を許す（空文字列を返す）。既定は非空必須。</param>
    /// <param name="multiline">true なら複数行入力欄として表示する。</param>
    /// <param name="password">true なら入力を伏せる（tasks.json の <c>password</c> 入力）。前後の空白も削らずに返す。</param>
    public static string? Prompt(
        Window? owner, string title, string prompt, string initial = "",
        bool selectNameOnly = false, bool allowEmpty = false, bool multiline = false, bool password = false)
    {
        var dialog = new InputDialog
        {
            Owner = owner,
            Title = title,
            _allowEmpty = allowEmpty,
        };
        dialog.PromptText.Text = prompt;
        dialog.InputBox.Text = initial;
        if (multiline)
        {
            dialog.InputBox.AcceptsReturn = true;
            dialog.InputBox.AcceptsTab = true;
            dialog.InputBox.TextWrapping = TextWrapping.Wrap;
            dialog.InputBox.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
            dialog.InputBox.MinHeight = 140;
        }

        if (password)
        {
            dialog._password = true;
            dialog.InputBox.Visibility = Visibility.Collapsed;
            dialog.PasswordInput.Visibility = Visibility.Visible;
            dialog.PasswordInput.Password = initial;
        }

        dialog.Loaded += (_, _) =>
        {
            if (password)
            {
                dialog.PasswordInput.Focus();
                dialog.PasswordInput.SelectAll();
                return;
            }
            dialog.InputBox.Focus();
            dialog.InputBox.Select(0, DialogTextPolicy.NameSelectionLength(initial, selectNameOnly));
        };

        if (dialog.ShowDialog() != true) return null;
        return password ? dialog.PasswordInput.Password : DialogTextPolicy.Trim(dialog.InputBox.Text);
    }

    private bool _password;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!_allowEmpty && DialogTextPolicy.IsMissingRequiredText(_password ? PasswordInput.Password : InputBox.Text))
        {
            ErrorText.Text = "名前を入力してください。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }
}
