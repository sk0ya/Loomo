using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>起動プロジェクト選択コンボボックス用の検出ヘルパ（.sln 解析・浅い再帰走査・テストプロジェクト判定）の検証。</summary>
public class DebugProjectDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-discovery");

    public DebugProjectDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string PlainCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net9.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string TestCsprojViaPackageRef = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net9.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.0.0" />
          </ItemGroup>
        </Project>
        """;

    private const string TestCsprojViaFlag = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net9.0</TargetFramework>
            <IsTestProject>true</IsTestProject>
          </PropertyGroup>
        </Project>
        """;

    private void WriteProject(string relativeDir, string name, string content)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.csproj"), content);
    }

    [Fact]
    public void Discover_without_sln_scans_directory_tree_and_flags_test_projects()
    {
        WriteProject("src/App", "App", PlainCsproj);
        WriteProject("tests/App.Tests", "App.Tests", TestCsprojViaPackageRef);

        var found = DebugProjectDiscovery.Discover(_root);

        Assert.Equal(2, found.Count);
        var app = Assert.Single(found, p => p.Name == "App");
        Assert.False(app.IsTest);
        var tests = Assert.Single(found, p => p.Name == "App.Tests");
        Assert.True(tests.IsTest);
    }

    [Fact]
    public void Discover_detects_IsTestProject_flag()
    {
        WriteProject(".", "MyTests", TestCsprojViaFlag);

        var found = DebugProjectDiscovery.Discover(_root);

        Assert.True(Assert.Single(found).IsTest);
    }

    [Fact]
    public void Discover_skips_bin_obj_and_git_directories()
    {
        WriteProject("bin/Debug", "Ignored", PlainCsproj);
        WriteProject("obj", "AlsoIgnored", PlainCsproj);
        WriteProject(".git", "GitIgnored", PlainCsproj);
        WriteProject("src/Real", "Real", PlainCsproj);

        var found = DebugProjectDiscovery.Discover(_root);

        Assert.Single(found);
        Assert.Equal("Real", found[0].Name);
    }

    [Fact]
    public void Discover_with_sln_uses_only_referenced_projects()
    {
        WriteProject("src/App", "App", PlainCsproj);
        WriteProject("src/Extra", "Extra", PlainCsproj);  // sln に載らない野良プロジェクト

        File.WriteAllText(Path.Combine(_root, "Solution.sln"),
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """);

        var found = DebugProjectDiscovery.Discover(_root);

        Assert.Single(found);
        Assert.Equal("App", found[0].Name);
    }

    [Fact]
    public void Discover_ignores_sln_entries_whose_csproj_is_missing()
    {
        File.WriteAllText(Path.Combine(_root, "Solution.sln"),
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Missing", "src\Missing\Missing.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
            EndGlobal
            """);
        WriteProject("src/Fallback", "Fallback", PlainCsproj);

        var found = DebugProjectDiscovery.Discover(_root);

        // sln 参照が1件も実体化できない場合はディレクトリ走査へフォールバックする。
        Assert.Single(found);
        Assert.Equal("Fallback", found[0].Name);
    }
}
