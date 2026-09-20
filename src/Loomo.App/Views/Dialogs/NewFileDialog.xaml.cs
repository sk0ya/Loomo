using System;
using System.IO;
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
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("ファイル名を入力してください。");
            return;
        }

        var extension = ExtensionBox.Text.Trim();
        ResultName = ComposeFileName(name, extension);
        DialogResult = true;
    }

    /// <summary>ファイル名と拡張子を結合する。既に拡張子があれば二重付与しない。</summary>
    internal static string ComposeFileName(string name, string extension)
    {
        name = name.Trim();
        extension = extension.Trim();

        if (string.IsNullOrEmpty(extension)
            || extension == "（なし）"
            || Path.HasExtension(name)
            || Path.GetFileName(name).StartsWith(".", StringComparison.Ordinal))
            return name;

        if (!extension.StartsWith(".", StringComparison.Ordinal))
            extension = "." + extension;
        return extension.Length == 1 ? name : name + extension;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
