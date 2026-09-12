using System.Windows.Controls;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コマンドパレットのプレビュー欄。選んでいるナビゲーション項目（ファイル／テキスト／シンボル／行）の
/// 中身を、タブを作らずその場に出す。組み立ては <see cref="PalettePreviewLoader"/>（バックグラウンド）が
/// 済ませてあるので、ここは受け取った <see cref="PalettePreviewContent"/> を描くだけ。
/// </summary>
public partial class PalettePreviewView : UserControl
{
    public PalettePreviewView()
    {
        InitializeComponent();
    }

    public void Show(PalettePreviewContent content)
    {
        HeaderText.Text = content.Header;
        SubHeaderText.Text = content.SubHeader;
        SubHeaderText.Visibility = string.IsNullOrEmpty(content.SubHeader)
            ? Visibility.Collapsed : Visibility.Visible;

        LinesView.DataContext = content;
        LinesView.ItemsSource = content.Lines;
        // 切り出しはジャンプ先の少し上から始まるので、先頭へ戻すだけで目的の行が見える位置になる。
        BodyScroll.ScrollToTop();

        MessageText.Text = content.Message ?? "";
        MessageText.Visibility = string.IsNullOrEmpty(content.Message) ? Visibility.Collapsed : Visibility.Visible;
        BodyScroll.Visibility = string.IsNullOrEmpty(content.Message) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>読み込み中・対象なしの空表示（前の項目の中身を残さない）。</summary>
    public void ShowMessage(string header, string message)
        => Show(new PalettePreviewContent(header, "", message, Array.Empty<PalettePreviewLine>(), null));

    public void Clear() => Show(PalettePreviewContent.Empty);
}
