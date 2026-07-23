using System;
using System.IO;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>TS IDE ペインのゲート判定と tsconfig ディレクトリ解決（<see cref="TsDebugTargetResolver"/>）のテスト。</summary>
public class TsDebugTargetResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("loomo-tsgate-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeDir(string relative)
    {
        var dir = Path.Combine(_root, relative);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Gate_is_false_for_empty_or_unrelated_folder()
    {
        Assert.False(TsDebugTargetResolver.HasTypeScriptProject(Array.Empty<string>()));
        Assert.False(TsDebugTargetResolver.HasTypeScriptProject([_root]));   // マーカー無し
    }

    [Fact]
    public void Gate_detects_tsconfig_or_package_json_within_depth()
    {
        File.WriteAllText(Path.Combine(MakeDir(@"src\app"), "tsconfig.json"), "{}");
        Assert.True(TsDebugTargetResolver.HasTypeScriptProject([_root]));
    }

    [Fact]
    public void Gate_ignores_node_modules()
    {
        File.WriteAllText(Path.Combine(MakeDir(@"node_modules\pkg"), "package.json"), "{}");
        Assert.False(TsDebugTargetResolver.HasTypeScriptProject([_root]));
    }

    [Fact]
    public void Gate_checks_all_workspace_folders()
    {
        var other = MakeDir("other");
        File.WriteAllText(Path.Combine(other, "package.json"), "{}");
        Assert.True(TsDebugTargetResolver.HasTypeScriptProject([MakeDir("empty"), other]));
    }

    [Fact]
    public void FindTsconfigDir_prefers_preferred_dir_then_scans()
    {
        // スキャン対象ルート（tsconfig は src\app のみ）と、ルート外の優先ディレクトリを分けて決定的にする。
        var scanRoot = MakeDir("scan");
        var scanned = MakeDir(@"scan\src\app");
        File.WriteAllText(Path.Combine(scanned, "tsconfig.json"), "{}");
        var preferred = MakeDir("pkg");
        File.WriteAllText(Path.Combine(preferred, "tsconfig.json"), "{}");

        Assert.Equal(preferred, TsDebugTargetResolver.FindTsconfigDir([scanRoot], preferredDir: preferred));
        Assert.Equal(scanned, TsDebugTargetResolver.FindTsconfigDir([scanRoot]));
        Assert.Null(TsDebugTargetResolver.FindTsconfigDir([MakeDir("plain")]));
    }
}
