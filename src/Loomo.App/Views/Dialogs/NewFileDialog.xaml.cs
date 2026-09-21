using System.Windows;
using System.Windows.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>ファイル名と拡張子を分けて指定できる新規ファイル用ダイアログ。</summary>
public partial class NewFileDialog : Window
{
    private NewFileDialog()
    {
        InitializeComponent();
    }

    /// <summary>ダイアログを開き、作成するファイル名を返す。キャンセル時は null。</summary>
    public static string? Prompt(Window? owner)
    {
        var dialog = new NewFileDialog { Owner = owner };
        dialog.Loaded += (_, _) =>
        {
            dialog.NameBox.Focus();
            dialog.NameBox.SelectAll();
            dialog.ExtensionBox.SelectedIndex = 1; // Loomo で最も使う .md を初期候補にする。
        };

        return dialog.ShowDialog() == true ? dialog.ResultName : null;
    }

    private string? ResultName { get; set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!NewFileNamePolicy.TryComposeFileName(NameBox.Text, ExtensionBox.Text, out var fileName))
        {
            ShowError("ファイル名を入力してください。");
            return;
        }

        ResultName = fileName;
        DialogResult = true;
    }

    /// <summary>既存の呼び出し元向けに、ファイル名の結合規則を公開する。</summary>
    public static string ComposeFileName(string name, string extension)
        => NewFileNamePolicy.ComposeFileName(name, extension);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
