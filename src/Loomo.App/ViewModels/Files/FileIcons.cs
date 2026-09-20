using System.IO;
using System.Windows;
using System.Windows.Media;

namespace sk0ya.Loomo.App.ViewModels;

// ツリー・検索結果の行に出すファイル種別アイコン。形と色は Catppuccin の VS Code アイコン (MIT) 由来で、
// 拡張子・ファイル名ごとの定義は自動生成の FileIconData（tools/icons/gen_file_icons.py）が持つ。
//
// アイコンは 16x16 の座標系に描かれた**線画**で、1 個が数本の線（色ちがい）でできている。1 要素で
// 描けるように DrawingImage（凍結済み）へ畳んでから Image.Source に渡す。
//
// 性能上の約束ごと：
//  - Geometry / Pen / Brush / DrawingImage はいずれも「種別ごとに 1 個」を共有し、ノードごとには作らない。
//  - Geometry.Parse は重いので、実際に画面へ出たアイコンの線だけを初回に一度だけ解析する
//    （ワークスペースに .cs しか無ければ .cs 用の数本しか解析しない）。
//  - 生成したものは必ず Freeze する（変更通知の購読が要らなくなり、スレッドをまたげる）。
//  - 暗色用・明色用は別々のキャッシュに持ち、テーマを往復しても作り直さない。
internal static class FileIcons
{
    /// <summary>アイコンが描かれている座標系の一辺。表示サイズもこれに合わせる。</summary>
    public const double Size = 16;

    // 形は配色に依らないので暗色・明色で共有する。
    private static readonly Geometry?[] Geometries = new Geometry?[FileIconData.Layers.Length];

    private static readonly Pen?[] DarkPens = new Pen?[FileIconData.Layers.Length];
    private static readonly Pen?[] LightPens = new Pen?[FileIconData.Layers.Length];
    private static readonly Brush?[] DarkBrushes = new Brush?[FileIconData.DarkPalette.Length];
    private static readonly Brush?[] LightBrushes = new Brush?[FileIconData.LightPalette.Length];
    private static readonly ImageSource?[] DarkImages = new ImageSource?[FileIconData.Defs.Length];
    private static readonly ImageSource?[] LightImages = new ImageSource?[FileIconData.Defs.Length];

    // DrawingImage の大きさは中身の境界で決まる。線画は 16x16 の中に余白を持つので、そのままだと
    // アイコンごとに拡大率が変わってしまう。透明な 16x16 の下敷きを 1 枚敷いて境界を固定する。
    private static readonly Geometry Bounds = Frozen(new RectangleGeometry(new Rect(0, 0, Size, Size)));
    private static readonly Brush BoundsBrush = Frozen(new SolidColorBrush(Colors.Transparent));

    private static bool _light;

    /// <summary>明色テーマ用の配色を使うか。<see cref="Services.ThemeManager"/> が設定する。</summary>
    public static bool UseLightPalette
    {
        get => _light;
        set
        {
            if (_light == value) return;
            _light = value;
            PaletteChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>配色が切り替わったので、表示中のアイコンを引き直してほしい。購読側（ツリー等）は
    /// アプリと同じ寿命の ViewModel なので、解除は要らない。</summary>
    public static event EventHandler? PaletteChanged;

    /// <summary>パスからアイコン定義の索引を引く。ファイル名まるごとの一致（package.json や
    /// Dockerfile）を拡張子より優先し、どちらにも当たらなければ既定のファイルアイコンにする。</summary>
    public static int IndexFor(string fullPath, bool isDirectory)
    {
        if (isDirectory)
            return FileIconData.FolderIndex;

        var name = Path.GetFileName(fullPath);
        if (FileIconData.ByFileName.TryGetValue(name, out var byName))
            return byName;

        var ext = Path.GetExtension(name);
        if (ext.Length > 0 && FileIconData.ByExtension.TryGetValue(ext, out var byExt))
            return byExt;

        return FileIconData.DefaultFileIndex;
    }

    /// <summary>索引に対応するアイコン。<see cref="Size"/> 角に置いてそのまま表示できる。</summary>
    public static ImageSource ImageFor(int index)
    {
        var cache = _light ? LightImages : DarkImages;
        // 同じ索引を別スレッドが同時に作ることはあり得るが、どちらも同じ内容の凍結済みインスタンス
        // になるので、後勝ちで上書きされても問題ない（参照の代入は不可分）。ロックは張らない。
        return cache[index] ??= BuildImage(index);
    }

    /// <summary>フォルダー用アイコン（開いている状態も選べる）。</summary>
    public static ImageSource FolderImage(bool open) =>
        ImageFor(open ? FileIconData.FolderOpenIndex : FileIconData.FolderIndex);

    private static ImageSource BuildImage(int index)
    {
        var def = FileIconData.Defs[index];
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(BoundsBrush, null, Bounds));

        for (var i = def.Start; i < def.Start + def.Count; i++)
        {
            var layer = FileIconData.Layers[i];
            var geometry = Geometries[i] ??= BuildGeometry(i);
            group.Children.Add(layer.IsFill
                ? new GeometryDrawing(BrushFor(layer.Color), null, geometry)
                : new GeometryDrawing(null, PenFor(i), geometry));
        }

        group.Freeze();
        return Frozen(new DrawingImage(group));
    }

    private static Geometry BuildGeometry(int layerIndex)
    {
        var layer = FileIconData.Layers[layerIndex];
        var geometry = Geometry.Parse(layer.Data);
        if (layer.Transform != 0)
        {
            // Geometry.Parse は凍結済みを返すため、変換を載せる前に複製する。
            var t = layer.Transform * 6;
            var m = FileIconData.Transforms;
            geometry = geometry.CloneCurrentValue();
            geometry.Transform = Frozen(new MatrixTransform(m[t], m[t + 1], m[t + 2], m[t + 3], m[t + 4], m[t + 5]));
        }
        geometry.Freeze();
        return geometry;
    }

    private static Pen PenFor(int layerIndex)
    {
        var cache = _light ? LightPens : DarkPens;
        if (cache[layerIndex] is { } cached) return cached;

        var layer = FileIconData.Layers[layerIndex];
        var pen = new Pen(BrushFor(layer.Color), layer.Width)
        {
            StartLineCap = layer.RoundCap ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = layer.RoundCap ? PenLineCap.Round : PenLineCap.Flat,
            LineJoin = layer.RoundJoin ? PenLineJoin.Round : PenLineJoin.Miter,
            // WPF の既定は 10 だが SVG は 4。合わせないと鋭角がとがって元絵と変わる。
            MiterLimit = 4,
        };
        return cache[layerIndex] = Frozen(pen);
    }

    private static Brush BrushFor(byte color)
    {
        var cache = _light ? LightBrushes : DarkBrushes;
        if (cache[color] is { } cached) return cached;

        var argb = (_light ? FileIconData.LightPalette : FileIconData.DarkPalette)[color];
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        return cache[color] = Frozen(brush);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
