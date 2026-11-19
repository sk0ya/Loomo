using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using sk0ya.Loomo.App.Views;

namespace ViewHarness.Scenes;

/// <summary>ブラウザの画面に印を付けて Claude Code へ渡す窓（§24.110 の 1）。窓の枠は外し、中身を実際の窓の大きさで撮る。
/// 手順：印なし（開いた直後）→ 枠・矢印・文字を置く → 渡せなかったときの理由。</summary>
public sealed class ScreenMarkupScene : IScene
{
    private ScreenMarkupWindow? _window;

    public string Name => "screen-markup";
    public string Description => "ブラウザの画面に印を付けて Claude Code へ渡す窓（印なし・印あり・失敗）";
    public Size Size => new(900, 620);

    public FrameworkElement Build()
    {
        _window = new ScreenMarkupWindow(FakeScreen(1280, 800), _ => Task.FromResult<string?>(null));
        return new System.Windows.Controls.Border
        {
            Background = (Brush)Application.Current.FindResource("Panel"),
            Child = _window.DetachContentForPreview(),
        };
    }

    public IEnumerable<SceneStep> Steps(FrameworkElement root)
    {
        yield return new SceneStep("marks", () =>
        {
            _window!.AddRectMark(new Point(120, 140), new Point(620, 320));
            _window.AddArrowMark(new Point(900, 600), new Point(640, 340));
            _window.AddTextMark(new Point(660, 560), "ここが 2 行に折れる");
        });
        yield return new SceneStep("error", () =>
            _window!.ShowErrorForPreview("Claude Code が動いているターミナルがありません。ターミナルで claude を起動してください。"));
    }

    /// <summary>それらしいページ：見出しの帯・本文の行・ボタン。白地（よくあるページ）にして、印の色が映えるかを見る。</summary>
    private static BitmapSource FakeScreen(int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x29, 0x2F)), null, new Rect(0, 0, w, 64));
            var line = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE));
            for (var i = 0; i < 12; i++)
                dc.DrawRectangle(line, null, new Rect(120, 150 + i * 40, 400 + (i * 53 % 500), 14));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x88, 0x3D)), null, new Rect(120, 680, 160, 44), 6, 6);
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
