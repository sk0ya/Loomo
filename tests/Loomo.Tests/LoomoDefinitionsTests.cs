using System.IO;
using sk0ya.Loomo.Services.Definitions;

namespace sk0ya.Loomo.Tests;

/// <summary>定義の持ち出し・持ち込み（§45.9 P5）。定義は往復し、許し・録った動き・盆は持ち出さない。</summary>
public sealed class LoomoDefinitionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-defs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 後始末の失敗は無視 */ }
    }

    [Fact]
    public void 定義は別の場所へ書き出して取り込め_許しや動きや盆は持ち出さない()
    {
        var pc1 = Path.Combine(_root, "pc1");
        var export = Path.Combine(_root, "export");
        var pc2 = Path.Combine(_root, "pc2");

        // この PC の定義と、持ち出してはいけないもの（許し・録った動き・盆・ビューアの状態）
        Put(pc1, "viewers/dash/viewer.json", "{\"id\":\"dash\"}");
        Put(pc1, "viewers/dash/index.html", "<p>x</p>");
        Put(pc1, "viewers/dash/.state.json", "{\"secret\":1}");
        Put(pc1, "viewers-state.json", "{\"approved\":true}");
        Put(pc1, "motion/key1/probes.json", "[]");
        Put(pc1, "motion/key1/approved.json", "{\"fingerprint\":\"x\"}");
        Put(pc1, "motion/key1/snapshots/s1/snapshot.json", "{}");
        Put(pc1, "orders/key1/orders.json", "[]");
        Put(pc1, "orders/key1/results.json", "[]");

        var counts = LoomoDefinitions.Export(pc1, export);
        Assert.Equal(new LoomoDefinitions.Counts(1, 1, 1), counts);
        Assert.True(File.Exists(Path.Combine(export, LoomoDefinitions.ManifestFileName)));

        // 持ち出したフォルダーに、許しや動きや盆は無い
        Assert.False(File.Exists(Path.Combine(export, "viewers", "dash", ".state.json")));
        Assert.False(File.Exists(Path.Combine(export, "viewers-state.json")));
        Assert.False(File.Exists(Path.Combine(export, "motion", "key1", "approved.json")));
        Assert.False(Directory.Exists(Path.Combine(export, "motion", "key1", "snapshots")));
        Assert.False(File.Exists(Path.Combine(export, "orders", "key1", "results.json")));

        // 別の PC へ取り込む：定義だけが入り、その PC の許しは増えない
        var imported = LoomoDefinitions.Import(export, pc2);
        Assert.Equal(counts, imported);
        Assert.Equal("{\"id\":\"dash\"}", File.ReadAllText(Path.Combine(pc2, "viewers", "dash", "viewer.json")));
        Assert.True(File.Exists(Path.Combine(pc2, "motion", "key1", "probes.json")));
        Assert.True(File.Exists(Path.Combine(pc2, "orders", "key1", "orders.json")));
        Assert.False(File.Exists(Path.Combine(pc2, "viewers-state.json")));
        Assert.False(File.Exists(Path.Combine(pc2, "motion", "key1", "approved.json")));
    }

    [Fact]
    public void 書き出しの形でないフォルダーと_新しい版の書き出しは取り込まない()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Throws<InvalidDataException>(() => LoomoDefinitions.Import(empty, Path.Combine(_root, "x")));

        var future = Path.Combine(_root, "future");
        Directory.CreateDirectory(future);
        File.WriteAllText(Path.Combine(future, LoomoDefinitions.ManifestFileName), "{\"version\":99}");
        Assert.Throws<InvalidDataException>(() => LoomoDefinitions.Import(future, Path.Combine(_root, "y")));
    }

    private static void Put(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
