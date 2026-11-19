using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 撮った画面に印（枠・矢印・短い文字）を付けて、Claude Code へ渡す画像にする窓（§24.110 の 1）。
/// 印は画像の画素の座標で持ち、渡すときは画像と印を元の大きさのまま 1 枚の PNG に焼く。
/// 渡す先（一時ファイル＋@参照）は呼ぶ側が決める——この窓は「焼いた PNG を返す／やめた」だけ。
/// </summary>
public partial class ScreenMarkupWindow : Window
{
    /// <summary>印の色。テーマに合わせない：画像は Claude が見るもので、どのページの上でも目立つ必要がある（パレットの鍵を使わない理由）。</summary>
    private static readonly Brush MarkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x55)));
    private static readonly Brush LabelBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));

    private readonly BitmapSource _screen;
    private readonly Func<byte[], Task<string?>> _send;
    private readonly Stack<UIElement> _marks = new();
    private readonly double _stroke;
    private readonly double _fontSize;

    private Point _dragStart;
    private Shape? _draft;
    private TextBox? _editingText;
    private bool _sending;

    /// <param name="send">焼いた PNG を渡す。失敗したら理由を返す（窓は閉じずに出す）。null なら成功。</param>
    public ScreenMarkupWindow(BitmapSource screen, Func<byte[], Task<string?>> send)
    {
        InitializeComponent();
        _screen = screen;
        _send = send;
        ScreenImage.Source = screen;
        // 画像は画素の大きさで置く（Viewbox が窓に収める）。DPI が 96 でない PNG でも画素と座標を揃えるため。
        Surface.Width = ScreenImage.Width = MarkCanvas.Width = screen.PixelWidth;
        Surface.Height = ScreenImage.Height = MarkCanvas.Height = screen.PixelHeight;
        _stroke = Math.Max(3, screen.PixelWidth / 320.0);
        _fontSize = Math.Max(18, screen.PixelWidth / 55.0);
        Loaded += (_, _) => SendButton.Focus();
    }

    /// <summary>持ち主の窓に合わせて大きさを決める。<b>見せる前に</b>呼ぶ——CenterOwner は見せた時点の大きさで真ん中を決めるので、
    /// 見せてから大きさを変えると片寄る。</summary>
    public void FitTo(Window owner)
    {
        Owner = owner;
        var maxW = Math.Max(MinWidth, owner.ActualWidth * 0.85);
        var maxH = Math.Max(MinHeight, owner.ActualHeight * 0.85);
        // 画像＋キャプション・帯・余白のぶん。
        Width = Math.Clamp(_screen.PixelWidth + 24, MinWidth, maxW);
        Height = Math.Clamp(_screen.PixelHeight + 110, MinHeight, maxH);
    }

    // ── 印を描く ───────────────────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        CommitText();
        _dragStart = e.GetPosition(MarkCanvas);
        if (TextTool.IsChecked == true)
        {
            BeginText(_dragStart);
            e.Handled = true;
            return;
        }
        _draft = ArrowTool.IsChecked == true ? NewArrow(_dragStart, _dragStart) : NewRect();
        if (_draft is Rectangle)
            Place(_draft, _dragStart, _dragStart);
        MarkCanvas.Children.Add(_draft);
        MarkCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_draft is null)
            return;
        var p = Clamp(e.GetPosition(MarkCanvas));
        if (_draft is Rectangle) Place(_draft, _dragStart, p);
        else if (_draft is Path path) path.Data = ArrowGeometry(_dragStart, p);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draft is null)
            return;
        MarkCanvas.ReleaseMouseCapture();
        var p = Clamp(e.GetPosition(MarkCanvas));
        var draft = _draft;
        _draft = null;
        // 押しただけ（ほとんど動かしていない）は印にしない。
        if ((p - _dragStart).Length < _stroke * 3)
        {
            MarkCanvas.Children.Remove(draft);
            return;
        }
        PushMark(draft);
    }

    private Rectangle NewRect() => new()
    {
        Stroke = MarkBrush,
        StrokeThickness = _stroke,
        RadiusX = _stroke,
        RadiusY = _stroke,
        IsHitTestVisible = false,
    };

    private Path NewArrow(Point from, Point to) => new()
    {
        Stroke = MarkBrush,
        Fill = MarkBrush,
        StrokeThickness = _stroke,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        Data = ArrowGeometry(from, to),
        IsHitTestVisible = false,
    };

    private static void Place(Shape shape, Point a, Point b)
    {
        Canvas.SetLeft(shape, Math.Min(a.X, b.X));
        Canvas.SetTop(shape, Math.Min(a.Y, b.Y));
        shape.Width = Math.Abs(b.X - a.X);
        shape.Height = Math.Abs(b.Y - a.Y);
    }

    /// <summary>軸の線と、先端の塗った三角。三角の大きさは線の太さに比例させる。</summary>
    private Geometry ArrowGeometry(Point from, Point to)
    {
        var group = new GeometryGroup();
        var v = to - from;
        if (v.Length < 1)
            return group;
        v.Normalize();
        var head = _stroke * 4.5;
        var n = new Vector(-v.Y, v.X);
        var baseCenter = to - v * head;
        group.Children.Add(new LineGeometry(from, baseCenter));
        var tri = new StreamGeometry();
        using (var ctx = tri.Open())
        {
            ctx.BeginFigure(to, isFilled: true, isClosed: true);
            ctx.LineTo(baseCenter + n * head * 0.6, true, false);
            ctx.LineTo(baseCenter - n * head * 0.6, true, false);
        }
        group.Children.Add(tri);
        return group;
    }

    private void BeginText(Point at)
    {
        var box = new TextBox
        {
            FontSize = _fontSize,
            MinWidth = _fontSize * 4,
            Foreground = MarkBrush,
            Background = LabelBackground,
            BorderBrush = MarkBrush,
            BorderThickness = new Thickness(Math.Max(1, _stroke / 2)),
            Padding = new Thickness(4, 1, 4, 1),
        };
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y);
        box.LostKeyboardFocus += (_, _) => CommitText();
        MarkCanvas.Children.Add(box);
        _editingText = box;
        box.Focus();
        HintText.Text = "Enter で決める・Esc でやめる";
    }

    /// <summary>書いている文字を印に決める（空ならやめる）。</summary>
    private void CommitText()
    {
        if (_editingText is not { } box)
            return;
        _editingText = null;
        HintText.Text = "";
        MarkCanvas.Children.Remove(box);
        var text = box.Text.Trim();
        if (text.Length == 0)
            return;
        var label = new Border
        {
            Background = LabelBackground,
            BorderBrush = MarkBrush,
            BorderThickness = new Thickness(Math.Max(1, _stroke / 2)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = text, FontSize = _fontSize, Foreground = MarkBrush, FontWeight = FontWeights.SemiBold },
        };
        Canvas.SetLeft(label, Canvas.GetLeft(box));
        Canvas.SetTop(label, Canvas.GetTop(box));
        MarkCanvas.Children.Add(label);
        PushMark(label);
    }

    private void CancelText()
    {
        if (_editingText is not { } box)
            return;
        _editingText = null;
        HintText.Text = "";
        MarkCanvas.Children.Remove(box);
        SendButton.Focus();
    }

    private void PushMark(UIElement mark)
    {
        _marks.Push(mark);
        UndoButton.IsEnabled = true;
        HintText.Text = "";
    }

    private void Undo()
    {
        if (_editingText is not null)
        {
            CancelText();
            return;
        }
        if (_marks.Count == 0)
            return;
        MarkCanvas.Children.Remove(_marks.Pop());
        UndoButton.IsEnabled = _marks.Count > 0;
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _screen.PixelWidth), Math.Clamp(p.Y, 0, _screen.PixelHeight));

    // ── 終わり方 ─────────────────────────────────────────

    private void OnUndoClicked(object sender, RoutedEventArgs e) => Undo();

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    private async void OnSendClicked(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_sending)
            return;
        CommitText();
        _sending = true;
        SendButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var error = await _send(Render());
            if (error is null)
            {
                Close();
                return;
            }
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>画像と印を、画像の画素の大きさのまま 1 枚の PNG に焼く。</summary>
    internal byte[] Render() => Compose(_screen, MarkCanvas);

    /// <summary>画像の上に印の層（画素の座標で置いたもの）を重ねて PNG にする。</summary>
    internal static byte[] Compose(BitmapSource screen, Visual markLayer)
    {
        var w = screen.PixelWidth;
        var h = screen.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(screen, new Rect(0, 0, w, h));
            // 印の層を画素の座標のまま重ねる。VisualBrush の既定は中身の外接矩形に合わせて伸ばすので、範囲を絶対で決める。
            var area = new Rect(0, 0, w, h);
            dc.DrawRectangle(new VisualBrush(markLayer)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = area,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = area,
                Stretch = Stretch.Fill,
            }, null, area);
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (_editingText is not null)
        {
            // 文字を書いている間：Enter で決める・Esc でその文字だけやめる（窓は閉じない）。
            if (e.Key == Key.Enter && !ctrl) { CommitText(); SendButton.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key == Key.Enter && ctrl) { _ = SendAsync(); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter when ctrl || Keyboard.FocusedElement is not Button || ReferenceEquals(Keyboard.FocusedElement, SendButton):
                _ = SendAsync();
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                Undo();
                e.Handled = true;
                break;
            case Key.D1 or Key.NumPad1 when Keyboard.Modifiers == ModifierKeys.None:
                RectTool.IsChecked = true;
                e.Handled = true;
                break;
            case Key.D2 or Key.NumPad2 when Keyboard.Modifiers == ModifierKeys.None:
                ArrowTool.IsChecked = true;
                e.Handled = true;
                break;
            case Key.D3 or Key.NumPad3 when Keyboard.Modifiers == ModifierKeys.None:
                TextTool.IsChecked = true;
                e.Handled = true;
                break;
        }
    }

    // ── ハーネス・テストから状態を作る口（マウスを使わずに印を置く） ─────────────

    /// <summary>枠の印を置く（画素の座標）。</summary>
    public void AddRectMark(Point a, Point b)
    {
        var rect = NewRect();
        Place(rect, a, b);
        MarkCanvas.Children.Add(rect);
        PushMark(rect);
    }

    /// <summary>矢印の印を置く（画素の座標）。</summary>
    public void AddArrowMark(Point from, Point to)
    {
        var arrow = NewArrow(from, to);
        MarkCanvas.Children.Add(arrow);
        PushMark(arrow);
    }

    /// <summary>文字の印を置く（画素の座標）。</summary>
    public void AddTextMark(Point at, string text)
    {
        BeginText(at);
        _editingText!.Text = text;
        CommitText();
    }

    /// <summary>渡せなかったときの理由を出す（ハーネスでエラーの見た目を撮るため）。</summary>
    public void ShowErrorForPreview(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>窓の中身（キャプション・帯・画像）を窓から外して返す。ハーネスで別の枠に載せて撮るため。</summary>
    public FrameworkElement DetachContentForPreview()
    {
        var content = (FrameworkElement)Content;
        Content = null;
        return content;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
