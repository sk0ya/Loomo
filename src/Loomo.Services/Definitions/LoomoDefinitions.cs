using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace sk0ya.Loomo.Services.Definitions;

/// <summary>
/// 定義の持ち出し・持ち込み（§45.9 P5）：Viewer・見どころ・言いつけの<b>定義</b>を、人が選んだフォルダーへ書き出し、別の PC で取り込む。
/// <para>持ち出すのは定義だけ：Viewerの <c>viewer.json</c> と <c>index.html</c>、見どころの <c>probes.json</c>、言いつけの <c>orders.json</c>。
/// <b>許し（承認の指紋）・録った動き・言いつけの盆・作業ツリーは持ち出さない</b>——許しを持ち込むと、別の PC で黙って回ることになるため。
/// 取り込んだViewerは、その PC で人が一度許すまで走らない（許しは PC ごとの置き場にあるので、指紋が合わなければ聞き直される）。</para>
/// <para>フォルダーの形：<c>loomo-definitions.json</c>（版・書き出した時刻・件数）と、<c>viewers/&lt;id&gt;/</c>・<c>motion/&lt;key&gt;/probes.json</c>・
/// <c>orders/&lt;key&gt;/orders.json</c>。取り込みは同じ形を読むだけで、フォルダーの外には書かない。</para>
/// </summary>
public static class LoomoDefinitions
{
    public const string ManifestFileName = "loomo-definitions.json";
    public const int Version = 1;

    /// <summary>Viewerの中で持ち出すファイル（Viewerの状態は PC ごと・許しは別の置き場）。</summary>
    private static readonly HashSet<string> ViewerFiles = new(StringComparer.OrdinalIgnoreCase) { "viewer.json", "index.html" };

    public sealed record Counts(int Viewers, int Probes, int Orders);

    /// <summary><paramref name="appDataRoot"/>（%APPDATA%\Loomo）から <paramref name="target"/> へ書き出す。件数を返す。</summary>
    public static Counts Export(string appDataRoot, string target)
    {
        Directory.CreateDirectory(target);
        var viewers = 0;
        var viewerRoot = Path.Combine(appDataRoot, "viewers");
        if (Directory.Exists(viewerRoot))
        {
            foreach (var dir in Directory.GetDirectories(viewerRoot))
            {
                if (!File.Exists(Path.Combine(dir, "viewer.json")))
                    continue;   // Viewerではないフォルダー
                var dest = Path.Combine(target, "viewers", Path.GetFileName(dir));
                Directory.CreateDirectory(dest);
                foreach (var file in Directory.GetFiles(dir).Where(f => ViewerFiles.Contains(Path.GetFileName(f))))
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
                viewers++;
            }
        }
        var probes = CopyEach(Path.Combine(appDataRoot, "motion"), "probes.json", Path.Combine(target, "motion"));
        var orders = CopyEach(Path.Combine(appDataRoot, "orders"), "orders.json", Path.Combine(target, "orders"));
        var counts = new Counts(viewers, probes, orders);
        File.WriteAllText(Path.Combine(target, ManifestFileName), new JsonObject
        {
            ["version"] = Version,
            ["exportedAt"] = DateTimeOffset.Now.ToString("o"),
            ["viewers"] = counts.Viewers,
            ["probes"] = counts.Probes,
            ["orders"] = counts.Orders,
        }.ToJsonString(), new UTF8Encoding(false));
        return counts;
    }

    /// <summary><paramref name="source"/>（書き出したフォルダー）から <paramref name="appDataRoot"/> へ取り込む。件数を返す。
    /// 承認・録った動き・盆には触れない。版の違う・壊れたフォルダーは例外。</summary>
    public static Counts Import(string source, string appDataRoot)
    {
        var manifestPath = Path.Combine(source, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("このフォルダーは Loomo の定義の書き出しではありません（loomo-definitions.json がありません）。");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
                       ?? throw new InvalidDataException("loomo-definitions.json が読めません。");
        if ((int?)manifest["version"] is not { } version || version > Version)
            throw new InvalidDataException("この書き出しは新しい版の Loomo のものです。Loomo を更新してから取り込んでください。");

        var viewers = 0;
        var viewerRoot = Path.Combine(source, "viewers");
        if (Directory.Exists(viewerRoot))
        {
            foreach (var dir in Directory.GetDirectories(viewerRoot))
            {
                if (!File.Exists(Path.Combine(dir, "viewer.json")))
                    continue;
                var dest = Path.Combine(appDataRoot, "viewers", Path.GetFileName(dir));
                Directory.CreateDirectory(dest);
                foreach (var file in Directory.GetFiles(dir).Where(f => ViewerFiles.Contains(Path.GetFileName(f))))
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
                viewers++;
            }
        }
        var probes = CopyEach(Path.Combine(source, "motion"), "probes.json", Path.Combine(appDataRoot, "motion"));
        var orders = CopyEach(Path.Combine(source, "orders"), "orders.json", Path.Combine(appDataRoot, "orders"));
        return new Counts(viewers, probes, orders);
    }

    /// <summary><paramref name="fromRoot"/> の各フォルダーの <paramref name="fileName"/> を、<paramref name="toRoot"/> の同じ名前のフォルダーへ写す。件数を返す。</summary>
    private static int CopyEach(string fromRoot, string fileName, string toRoot)
    {
        if (!Directory.Exists(fromRoot))
            return 0;
        var count = 0;
        foreach (var dir in Directory.GetDirectories(fromRoot))
        {
            var file = Path.Combine(dir, fileName);
            if (!File.Exists(file))
                continue;
            var dest = Path.Combine(toRoot, Path.GetFileName(dir));
            Directory.CreateDirectory(dest);
            File.Copy(file, Path.Combine(dest, fileName), overwrite: true);
            count++;
        }
        return count;
    }
}
