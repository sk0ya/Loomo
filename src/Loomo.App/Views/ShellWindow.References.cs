using sk0ya.Loomo.App.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Editor.Controls;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタの「使用箇所表示（Find References / gr）」の結果を受けて一覧表示する。
/// エディタコントロールは LSP に問い合わせて参照を計算するが、結果は自身では描画せず
/// <see cref="VimEditorControl.FindReferencesResult"/> イベントを発火するだけなので、ホスト側で
/// ポップアップに一覧を出し、クリックで該当ファイル・行へジャンプさせる。
/// 同じイベントは grep / 診断一覧 / コール・型ヒエラルキー / ワークスペースシンボルの結果にも使われる。
/// ※「まず配線だけ最小実装」段階：機能は通っているが見た目は最小。後でドッキングパネル等へ移す。</summary>
public partial class ShellWindow
{
    private void OnEditorFindReferencesResult(object? sender, FindReferencesResultEventArgs e)
    {
        BuildReferencesPopup(e.Items, $"{e.TitlePrefix} ({e.Items.Count}) — {e.SymbolName}");
        ReferencesPopup.IsOpen = true;
    }

    /// <summary>ポップアップの中身（参照行の一覧）を組み直す。</summary>
    private void BuildReferencesPopup(IReadOnlyList<FindReferenceItem> items, string title)
    {
        ReferencesPopupTitle.Text = title;
        ReferencesPopupList.Children.Clear();

        if (items.Count == 0)
        {
            ReferencesPopupList.Children.Add(new TextBlock
            {
                Text = "使用箇所が見つかりませんでした",
                FontSize = UiFontManager.Scaled(12),
                Margin = new Thickness(10, 6, 10, 6),
                Foreground = (Brush)FindResource("FgDim"),
            });
            return;
        }

        foreach (var item in items)
        {
            var captured = item;
            // Line/Col は LSP 由来の 0 始まり。表示は 1 始まりへ、ジャンプ（OpenPathInEditorAsync は 1 始まり）も +1。
            var location = $"{Path.GetFileName(captured.FilePath)}:{captured.Line + 1}:{captured.Col + 1}";
            var preview = captured.Preview ?? ReadSourceLine(captured.FilePath, captured.Line);

            var content = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            content.Inlines.Add(new System.Windows.Documents.Run(location)
            {
                Foreground = (Brush)FindResource("Accent"),
            });
            if (!string.IsNullOrWhiteSpace(preview))
                content.Inlines.Add(new System.Windows.Documents.Run("   " + preview)
                {
                    Foreground = (Brush)FindResource("FgDim"),
                });

            var row = new Button
            {
                Style = (Style)FindResource("BranchMenuItem"),
                FontSize = UiFontManager.Scaled(12),
                ToolTip = $"{captured.FilePath}:{captured.Line + 1}:{captured.Col + 1}",
                Content = content,
            };
            row.Click += (_, _) =>
            {
                ReferencesPopup.IsOpen = false;
                _ = OpenPathInEditorAsync(captured.FilePath, captured.Line + 1, captured.Col + 1);
            };
            ReferencesPopupList.Children.Add(row);
        }
    }

    /// <summary>ファイルの指定行（0 始まり）をプレビュー用に読む。読めなければ空文字。</summary>
    private static string ReadSourceLine(string filePath, int line)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            for (var i = 0; i < line; i++)
                if (reader.ReadLine() == null) return "";
            return (reader.ReadLine() ?? "").Trim();
        }
        catch { return ""; }
    }
}
