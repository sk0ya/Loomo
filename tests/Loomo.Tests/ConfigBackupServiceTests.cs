using System.IO;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>設定のエクスポート／インポート（ConfigBackupService）の検証。</summary>
public class ConfigBackupServiceTests
{
    [Fact]
    public void Export_then_import_roundtrips_config_and_excludes_heavy_dirs()
    {
        var temp = Path.Combine(Path.GetTempPath(), "loomo-backup-test-" + Path.GetRandomFileName());
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "dst");
        var zip = Path.Combine(temp, "backup.zip");
        Directory.CreateDirectory(src);

        try
        {
            // 設定ファイル・サブフォルダ・除外対象（models）を用意する。
            File.WriteAllText(Path.Combine(src, "settings.json"), "{\"a\":1}");
            Directory.CreateDirectory(Path.Combine(src, "sessions"));
            File.WriteAllText(Path.Combine(src, "sessions", "s1.json"), "session");
            Directory.CreateDirectory(Path.Combine(src, "models"));
            File.WriteAllText(Path.Combine(src, "models", "huge.onnx"), "should-be-excluded");

            var exported = new ConfigBackupService(src).Export(zip);
            Assert.Equal(2, exported); // settings.json と sessions/s1.json のみ（models は除外）

            var imported = new ConfigBackupService(dst).Import(zip);
            Assert.Equal(2, imported);

            Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(dst, "settings.json")));
            Assert.Equal("session", File.ReadAllText(Path.Combine(dst, "sessions", "s1.json")));
            Assert.False(File.Exists(Path.Combine(dst, "models", "huge.onnx")));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 後始末失敗は無視 */ }
        }
    }
}
