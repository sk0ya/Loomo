using System;
using System.Windows;

namespace sk0ya.Loomo.App.Views;

/// <summary>名前入力用の汎用モーダルダイアログ（リネーム・新規作成で使用）。</summary>
public partial class InputDialog : Window
{
    private InputDialog()
    {
        InitializeComponent();
    }

    /// <summary>ダイアログを開き、入力された名前（前後空白を除去）を返す。キャンセル時は null。</summary>
    /// <param name="initial">初期値。</param>
    /// <param name="selectNameOnly">true なら拡張子を除いた部分だけを選択（ファイル名のリネーム向け）。</param>
    public static string? Prompt(
        Window? owner, string title, string prompt, string initial = "", bool selectNameOnly = false)
    {
        var dialog = new InputDialog
        {
            Owner = owner,
            Title = title,
        };
        dialog.PromptText.Text = prompt;
        dialog.InputBox.Text = initial;

        dialog.Loaded += (_, _) =>
        {
            dialog.InputBox.Focus();
            var dot = selectNameOnly ? initial.LastIndexOf('.') : -1;
            if (dot > 0)
                dialog.InputBox.Select(0, dot);   // 拡張子手前まで選択
            else
                dialog.InputBox.SelectAll();
        };

        return dialog.ShowDialog() == true ? dialog.InputBox.Text.Trim() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            ErrorText.Text = "名前を入力してください。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }
}
