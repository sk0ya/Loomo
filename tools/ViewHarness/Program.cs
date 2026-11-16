using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Models;

namespace ViewHarness;

/// <summary>
/// 使い方（リポジトリ直下から）:
///   dotnet run --project tools/ViewHarness -- --list
///   dotnet run --project tools/ViewHarness -- --scene style-gallery
///   dotnet run --project tools/ViewHarness -- --scene user-theme-editor --themes Dark,Light,HighContrast --font 16,20 --out <dir>
///   dotnet run --project tools/ViewHarness -- --xaml sketch.xaml        （緩い XAML の下書きを Loomo のスタイルで描く）
/// 出力: &lt;out&gt;/&lt;scene&gt;-&lt;theme&gt;-&lt;px&gt;[-&lt;step&gt;].png、指摘があれば同名の -marked.png（赤枠）、findings.txt。
/// 終了コード: 0 = 指摘なし、1 = 指摘あり、2 = 引数・描画の失敗。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { return Run(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        var opt = Options.Parse(args);
        var scenes = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(IScene).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IScene)Activator.CreateInstance(t)!)
            .OrderBy(s => s.Name)
            .ToList();

        if (opt.List || (opt.Scene is null && opt.Xaml is null))
        {
            Console.WriteLine("シーン一覧（--scene 名前）:");
            foreach (var s in scenes) Console.WriteLine($"  {s.Name,-24} {s.Description}");
            Console.WriteLine("緩い XAML の下書きは --xaml <file>。");
            return opt.List ? 0 : 2;
        }

        IScene scene = opt.Xaml is not null
            ? new XamlFileScene(opt.Xaml, opt.Size)
            : scenes.FirstOrDefault(s => s.Name.Equals(opt.Scene, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"シーン '{opt.Scene}' がない。--list で一覧。");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var part in new[] { "Themes/Palette.Dark.xaml", "Themes/Typography.xaml", "Themes/Controls.xaml" })
            app.Resources.MergedDictionaries.Add(Pack(part));

        Directory.CreateDirectory(opt.Out);
        var fontManager = new UiFontManager();
        var report = new StringBuilder();
        var total = 0;

        foreach (var themeName in opt.Themes)
        {
            // ThemeManager は相対 URI（Themes/Palette.*.xaml）で読むので、入口がハーネスだと解決できず黙って Dark のまま。
            // パレットは Loomo.App の pack URI で差し替える。
            var theme = Enum.Parse<AppTheme>(themeName, ignoreCase: true);
            app.Resources.MergedDictionaries[0] = Pack($"Themes/Palette.{theme}.xaml");
            foreach (var px in opt.Fonts)
            {
                fontManager.Apply(px);
                var baseName = $"{scene.Name}-{themeName}-{px:0}";
                var root = scene.Build();
                // 窓の背景は RenderTargetBitmap に写らないので、部屋の背景（Bg）を敷いた枠ごと撮る。
                var frame = new Border { Child = root };
                frame.SetResourceReference(Border.BackgroundProperty, "Bg");
                frame.SetResourceReference(TextElement.ForegroundProperty, "Fg");
                var window = Host(frame, opt.Size ?? scene.Size);
                try
                {
                    total += Shoot(frame, Path.Combine(opt.Out, baseName), report);
                    var i = 1;
                    foreach (var step in scene.Steps(root))
                    {
                        step.Do();
                        Settle(opt.SettleMs);
                        total += Shoot(frame, Path.Combine(opt.Out, $"{baseName}-{++i:00}-{step.Label}"), report);
                    }
                }
                finally { window.Close(); }
            }
        }

        var reportPath = Path.Combine(opt.Out, "findings.txt");
        File.WriteAllText(reportPath, total == 0 ? "指摘なし\n" : report.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"出力: {Path.GetFullPath(opt.Out)}");
        Console.WriteLine(total == 0 ? "指摘なし" : $"指摘 {total} 件（{reportPath}）");
        Console.Write(report);
        app.Shutdown();
        return total == 0 ? 0 : 1;
    }

    private static ResourceDictionary Pack(string part) => new()
    {
        Source = new Uri($"pack://application:,,,/sk0ya.Loomo.App;component/{part}", UriKind.Absolute),
    };

    private static Window Host(FrameworkElement root, Size size)
    {
        var window = new Window
        {
            Content = root,
            Width = size.Width,
            Height = size.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -32000,
            Top = -32000,
        };
        window.SetResourceReference(Control.BackgroundProperty, "Bg");
        window.SetResourceReference(TextElement.ForegroundProperty, "Fg");
        window.Show();
        Settle(400);
        return window;
    }

    /// <summary>レイアウト・バインディング・Loaded の後始末を流し切る（非同期読み込みの分は待ち時間で）。</summary>
    private static void Settle(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        } while (DateTime.UtcNow < until);
    }

    private static int Shoot(FrameworkElement root, string pathNoExt, StringBuilder report)
    {
        var w = (int)Math.Ceiling(root.ActualWidth);
        var h = (int)Math.Ceiling(root.ActualHeight);
        var bmp = new RenderTargetBitmap(Math.Max(w, 1), Math.Max(h, 1), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        Save(bmp, pathNoExt + ".png");

        var findings = Inspector.Inspect(root);
        if (findings.Count == 0) return 0;

        // 指摘の場所に赤枠を描いた版も出す（どこの話か画像で分かるように）。
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(bmp, new Rect(0, 0, w, h));
            var pen = new Pen(Brushes.Red, 2);
            foreach (var f in findings) dc.DrawRectangle(null, pen, f.Bounds);
        }
        var marked = new RenderTargetBitmap(Math.Max(w, 1), Math.Max(h, 1), 96, 96, PixelFormats.Pbgra32);
        marked.Render(dv);
        Save(marked, pathNoExt + "-marked.png");

        report.AppendLine($"## {Path.GetFileName(pathNoExt)}");
        foreach (var g in findings.GroupBy(f => f.Kind))
            foreach (var f in g)
                report.AppendLine($"- [{f.Kind}] {f.Message} @({f.Bounds.X:0},{f.Bounds.Y:0} {f.Bounds.Width:0}x{f.Bounds.Height:0})");
        report.AppendLine();
        return findings.Count;
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private sealed record Options(string? Scene, string? Xaml, string[] Themes, double[] Fonts, string Out, Size? Size, int SettleMs, bool List)
    {
        public static Options Parse(string[] args)
        {
            string? scene = null, xaml = null;
            var themes = new[] { "Dark", "Light" };
            var fonts = new[] { (double)UiFontManager.DefaultSize };
            var outDir = Path.Combine(Path.GetTempPath(), "loomo-view-harness");
            Size? size = null;
            var settle = 300;
            var list = false;
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} の値がない");
                switch (args[i])
                {
                    case "--scene": scene = Next(); break;
                    case "--xaml": xaml = Next(); break;
                    case "--themes": themes = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                    case "--font": fonts = Next().Split(',').Select(double.Parse).ToArray(); break;
                    case "--out": outDir = Next(); break;
                    case "--size":
                        var wh = Next().Split('x');
                        size = new Size(double.Parse(wh[0]), double.Parse(wh[1]));
                        break;
                    case "--settle": settle = int.Parse(Next()); break;
                    case "--list": list = true; break;
                    default: throw new ArgumentException($"知らない引数: {args[i]}");
                }
            }
            return new(scene, xaml, themes, fonts, outDir, size, settle, list);
        }
    }

    /// <summary>緩い XAML（x:Class なし）を描く。作る前の下書き（並び・ラベル・幅の当たり）を本物のスタイルで見る用。
    /// StaticResource / DynamicResource で Controls.xaml のスタイル・パレットの色・Fs* が使える。</summary>
    private sealed class XamlFileScene(string path, Size? size) : IScene
    {
        public string Name => Path.GetFileNameWithoutExtension(path);
        public string Description => "緩い XAML の下書き";
        public Size Size => size ?? new Size(1000, 700);
        public FrameworkElement Build()
        {
            using var fs = File.OpenRead(path);
            return (FrameworkElement)XamlReader.Load(fs);
        }
    }
}
