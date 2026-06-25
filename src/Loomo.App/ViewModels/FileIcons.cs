using System.Collections.Generic;
using System.IO;
using System.Windows.Media;

namespace sk0ya.Loomo.App.ViewModels;

// ツリー行のアイコン種別。形状は GeometryFor、色は BrushFor で割り当てる。
internal enum FileIconKind
{
    Folder,
    Code,
    Config,
    Markup,
    Image,
    Document,
}

// 拡張子からアイコン種別を判定し、種別ごとのベクター形状（16x16 座標系・線画）と固定色を返す。
// Geometry / Brush はいずれも Freeze 済みの共有インスタンス（ノードごとに作らない）。
internal static class FileIcons
{
    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".vb", ".fs", ".fsx", ".py", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".go", ".rs", ".java", ".kt", ".kts", ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".rb",
        ".php", ".swift", ".scala", ".sh", ".bash", ".ps1", ".psm1", ".psd1", ".lua", ".dart", ".sql", ".r",
    };

    private static readonly HashSet<string> ConfigExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonc", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".config", ".env",
        ".properties", ".editorconfig", ".gitignore", ".gitattributes", ".dockerignore", ".lock",
        ".csproj", ".vbproj", ".fsproj", ".sln", ".props", ".targets",
    };

    private static readonly HashSet<string> MarkupExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xaml", ".axaml", ".html", ".htm", ".xml", ".css", ".scss", ".sass", ".less",
        ".razor", ".cshtml", ".vue", ".svelte",
    };

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".ico", ".webp", ".tif", ".tiff",
    };

    // 16x16 座標系。Path は Stretch=Uniform で行内に縮小される。線端は丸めてレンダリングする。
    private static readonly Geometry FolderGeometry =
        ParseGeometry("M2.5,3.5 L6,3.5 L7.5,5.3 L13.5,5.3 L13.5,13.5 L2.5,13.5 Z");

    // 折り目付きの書類シルエット（既定／その他全般）。
    private static readonly Geometry DocumentGeometry =
        ParseGeometry("M4,2.3 L9.2,2.3 L12,5.1 L12,13.7 L4,13.7 Z M9.2,2.3 L9.2,5.1 L12,5.1");

    // コード: 山括弧＋スラッシュ（</>）。
    private static readonly Geometry CodeGeometry =
        ParseGeometry("M5.7,4.8 L2.8,8 L5.7,11.2 M10.3,4.8 L13.2,8 L10.3,11.2 M9.3,4 L6.7,12");

    // マークアップ: 山括弧のみ（< >）。
    private static readonly Geometry MarkupGeometry =
        ParseGeometry("M5.7,4.8 L2.8,8 L5.7,11.2 M10.3,4.8 L13.2,8 L10.3,11.2");

    // 設定: 波括弧（{ }）。
    private static readonly Geometry ConfigGeometry =
        ParseGeometry("M6.6,3.6 C5.1,3.6 5.8,7.1 4,8 C5.8,8.9 5.1,12.4 6.6,12.4 " +
                      "M9.4,3.6 C10.9,3.6 10.2,7.1 12,8 C10.2,8.9 10.9,12.4 9.4,12.4");

    // 画像: 額縁＋太陽（円）＋山の稜線。
    private static readonly Geometry ImageGeometry =
        ParseGeometry("M2.5,4 L13.5,4 L13.5,12 L2.5,12 Z " +
                      "M5.6,7.2 A1.05,1.05 0 1 1 5.59,7.2 " +
                      "M3,11.5 L6.5,8 L8.5,10 L10.5,7.5 L13.5,10.8");

    private static readonly Brush FolderBrush = MakeBrush("#E8B339");   // アンバー
    private static readonly Brush CodeBrush = MakeBrush("#4FC1A6");     // 青緑
    private static readonly Brush ConfigBrush = MakeBrush("#E0C341");   // 黄
    private static readonly Brush MarkupBrush = MakeBrush("#E08A4B");   // 橙
    private static readonly Brush ImageBrush = MakeBrush("#6FB36F");    // 緑
    private static readonly Brush DocumentBrush = MakeBrush("#9AA0A6"); // 灰

    public static FileIconKind Classify(string fullPath, bool isDirectory)
    {
        if (isDirectory)
            return FileIconKind.Folder;

        var ext = Path.GetExtension(fullPath);
        if (CodeExts.Contains(ext)) return FileIconKind.Code;
        if (ConfigExts.Contains(ext)) return FileIconKind.Config;
        if (MarkupExts.Contains(ext)) return FileIconKind.Markup;
        if (ImageExts.Contains(ext)) return FileIconKind.Image;
        return FileIconKind.Document;
    }

    public static Geometry GeometryFor(FileIconKind kind) => kind switch
    {
        FileIconKind.Folder => FolderGeometry,
        FileIconKind.Code => CodeGeometry,
        FileIconKind.Config => ConfigGeometry,
        FileIconKind.Markup => MarkupGeometry,
        FileIconKind.Image => ImageGeometry,
        _ => DocumentGeometry,
    };

    public static Brush BrushFor(FileIconKind kind) => kind switch
    {
        FileIconKind.Folder => FolderBrush,
        FileIconKind.Code => CodeBrush,
        FileIconKind.Config => ConfigBrush,
        FileIconKind.Markup => MarkupBrush,
        FileIconKind.Image => ImageBrush,
        _ => DocumentBrush,
    };

    private static Geometry ParseGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Brush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
