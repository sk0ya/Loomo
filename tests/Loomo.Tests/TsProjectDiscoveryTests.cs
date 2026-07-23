using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>package.json 探索と npm スクリプト読み取り（<see cref="TsProjectDiscovery"/>）のテスト。</summary>
public class TsProjectDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("loomo-tsdisc-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WritePackageJson(string relativeDir, string content)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "package.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Discover_finds_packages_and_reads_names()
    {
        WritePackageJson("", """{ "name": "root-app" }""");
        WritePackageJson("packages/web", """{ "name": "web" }""");

        var entries = TsProjectDiscovery.Discover(_root);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == "root-app" && e.RelativePath == "package.json");
        Assert.Contains(entries, e => e.Name == "web");
        Assert.All(entries, e => Assert.False(e.IsTest));
    }

    [Fact]
    public void Discover_skips_node_modules_and_uses_dir_name_for_broken_json()
    {
        WritePackageJson("app", "{ broken json");
        WritePackageJson("app/node_modules/lodash", """{ "name": "lodash" }""");

        var entries = TsProjectDiscovery.Discover(_root);

        var only = Assert.Single(entries);
        Assert.Equal("app", only.Name);   // name が読めなければディレクトリ名
    }

    [Fact]
    public void ReadScripts_returns_names_in_definition_order()
    {
        var pkg = WritePackageJson("", """
            { "name": "x", "scripts": { "dev": "tsx watch src/main.ts", "build": "tsc", "start": "node dist/main.js" } }
            """);

        var scripts = TsProjectDiscovery.ReadScripts(pkg);

        Assert.Equal(new[] { "dev", "build", "start" }, scripts.ToArray());
    }

    [Fact]
    public void ReadScripts_handles_missing_scripts_and_missing_file()
    {
        var pkg = WritePackageJson("", """{ "name": "x" }""");
        Assert.Empty(TsProjectDiscovery.ReadScripts(pkg));
        Assert.Empty(TsProjectDiscovery.ReadScripts(Path.Combine(_root, "nope", "package.json")));
    }
}
