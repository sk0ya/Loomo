using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sk0ya.Loomo.App.Layout;

/// <summary>メインウィンドウの見た目の状態。全モニタへの最大化（<see cref="Span"/>）は WPF の上では Normal の
/// 大きな窓なので、WindowState とは別に持つ。</summary>
public enum ShellWindowShape
{
    Normal,
    Maximized,
    Span,
}

/// <summary>メインウィンドウの置き場所。<see cref="Bounds"/> は「元に戻す」で戻る通常時の矩形（物理ピクセル・スクリーン座標）で、
/// 最大化中・全モニタへの最大化中もそれを持つ。部屋（ワークスペース）ではなくこの機械のモニタ構成に付く状態なので、
/// ワークスペースのスナップショットではなくアプリ全体で 1 つ持つ。</summary>
public sealed record ShellWindowPlacement(ScreenRect Bounds, ShellWindowShape Shape);

/// <summary>保存した置き場所を、いまのモニタ構成で使ってよいかの判定（UI 非依存）。</summary>
public static class ShellWindowPlacementPolicy
{
    /// <summary>掴んで動かせるだけ見えていると見なす、作業領域との重なりの最小幅・最小高さ（物理ピクセル）。</summary>
    public const int MinVisibleWidth = 120;
    public const int MinVisibleHeight = 40;

    /// <summary>窓の上端（タイトル帯）が、どれかのモニタの作業領域に掴めるだけ載っているか。外したモニタや
    /// 解像度を下げたモニタの上に残った位置で開くと、窓が画面の外に出て戻せなくなる。</summary>
    public static bool IsReachable(ScreenRect bounds, IEnumerable<ScreenRect> workAreas)
    {
        if (bounds.Right - bounds.Left < MinVisibleWidth || bounds.Bottom - bounds.Top < MinVisibleHeight)
            return false;
        var titleBand = new ScreenRect(bounds.Left, bounds.Top, bounds.Right, bounds.Top + MinVisibleHeight);
        foreach (var area in workAreas)
        {
            var width = Math.Min(titleBand.Right, area.Right) - Math.Max(titleBand.Left, area.Left);
            var height = Math.Min(titleBand.Bottom, area.Bottom) - Math.Max(titleBand.Top, area.Top);
            if (width >= MinVisibleWidth && height >= MinVisibleHeight)
                return true;
        }
        return false;
    }
}

/// <summary><see cref="ShellWindowPlacement"/> を <c>%APPDATA%/Loomo/window.json</c> に読み書きする。
/// settings.json（人が設定画面や手で書き換える）とは混ぜない：閉じるたびに書くので、設定画面の保存と競合させない。</summary>
public sealed class ShellWindowPlacementStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public ShellWindowPlacementStore() : this(DefaultPath()) { }

    public ShellWindowPlacementStore(string filePath) => _filePath = filePath;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "window.json");

    /// <summary>無い・壊れている・寸法がおかしいときは null（既定の大きさで中央に開く）。</summary>
    public ShellWindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;
            var dto = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_filePath), JsonOpts);
            if (dto is null || dto.Right <= dto.Left || dto.Bottom <= dto.Top)
                return null;
            var shape = Enum.IsDefined(dto.Shape) ? dto.Shape : ShellWindowShape.Normal;
            return new ShellWindowPlacement(new ScreenRect(dto.Left, dto.Top, dto.Right, dto.Bottom), shape);
        }
        catch
        {
            return null;
        }
    }

    public void Save(ShellWindowPlacement placement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var b = placement.Bounds;
            var dto = new Persisted { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom, Shape = placement.Shape };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(dto, JsonOpts));
        }
        catch
        {
            // 書けなくても閉じるのは止めない（次回は既定の位置で開くだけ）。
        }
    }

    private sealed class Persisted
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public ShellWindowShape Shape { get; set; }
    }
}
