using System.IO;
using sk0ya.Loomo.CSharp.Build;

namespace sk0ya.Loomo.Tests;

/// <summary>旧形式（非SDK）プロジェクトの判定と、それを扱う MSBuild の選び方。</summary>
public sealed class MsBuildToolchainTests : IDisposable
{
    private const string LegacyProject = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" />
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
          </PropertyGroup>
          <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
        </Project>
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-msbuild-{Guid.NewGuid():N}");

    public MsBuildToolchainTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 一時フォルダーの後始末失敗はテスト結果に影響させない */ }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Classic_project_with_its_own_imports_is_legacy()
        => Assert.True(MsBuildToolchain.IsLegacyProject(Write("Legacy.csproj", LegacyProject)));

    [Theory]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup /></Project>""")]
    [InlineData("""<Project><Sdk Name="Microsoft.NET.Sdk" /><PropertyGroup /></Project>""")]
    [InlineData("""<Project><Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" /><Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" /></Project>""")]
    [InlineData("<Project />")]
    [InlineData("<Project><PropertyGroup /></Project>")]
    public void Sdk_projects_and_projects_without_evidence_are_not_legacy(string content)
        => Assert.False(MsBuildToolchain.IsLegacyProject(Write($"P{Guid.NewGuid():N}.csproj", content)));

    [Fact]
    public void Missing_or_broken_project_is_not_legacy()
    {
        Assert.False(MsBuildToolchain.IsLegacyProject(Path.Combine(_root, "Missing.csproj")));
        Assert.False(MsBuildToolchain.IsLegacyProject(Write("Broken.csproj", "<Project")));
    }

    [Fact]
    public void Solution_containing_one_legacy_project_needs_visual_studio_msbuild()
    {
        Write(@"Modern\Modern.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Write(@"Old\Old.csproj", LegacyProject);
        var solution = Write("Mixed.sln", """

            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Modern", "Modern\Modern.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Old", "Old\Old.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        Assert.Equal(2, MsBuildToolchain.SolutionProjects(solution).Count);
        Assert.True(MsBuildToolchain.ContainsLegacyProject(solution));
    }

    [Fact]
    public void Slnx_with_only_sdk_projects_stays_on_dotnet()
    {
        Write(@"A\A.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        var solution = Write("Only.slnx", """<Solution><Project Path="A/A.csproj" /></Solution>""");

        Assert.False(MsBuildToolchain.ContainsLegacyProject(solution));
        Assert.Same(MsBuildInvocation.DotnetSdk, MsBuildToolchain.For(solution));
    }

    [Fact]
    public void Legacy_project_goes_to_visual_studio_msbuild_when_one_is_configured()
    {
        var project = Write("Legacy.csproj", LegacyProject);
        var fakeMsBuild = Write(@"VS\MSBuild.exe", "");
        var previous = Environment.GetEnvironmentVariable(MsBuildToolchain.MsBuildPathVariable);
        Environment.SetEnvironmentVariable(MsBuildToolchain.MsBuildPathVariable, fakeMsBuild);
        try
        {
            var invocation = MsBuildToolchain.For(project);
            Assert.True(invocation.IsVisualStudio);
            Assert.Equal(fakeMsBuild, invocation.FileName);
            Assert.Empty(invocation.PrefixArguments);
        }
        finally { Environment.SetEnvironmentVariable(MsBuildToolchain.MsBuildPathVariable, previous); }
    }

    [Fact]
    public void Legacy_build_restores_packages_config_in_the_same_msbuild_run()
    {
        var command = CSharpBuildService.BuildCommand(@"C:\work\Old.sln", "Debug", "(既定)",
            new MsBuildInvocation(@"C:\VS\MSBuild.exe", [], true), containsLegacyProject: true);

        Assert.Equal(
            "& 'C:\\VS\\MSBuild.exe' 'C:\\work\\Old.sln' -restore -p:RestorePackagesConfig=true " +
            "-p:Configuration='Debug' -m -nologo -v:minimal",
            command);
    }

    [Fact]
    public void Legacy_build_without_msbuild_falls_back_to_dotnet_with_a_note_and_no_framework_switch()
    {
        var command = CSharpBuildService.BuildCommand(@"C:\work\Old.csproj", "Debug", "(既定)",
            MsBuildInvocation.DotnetSdk, containsLegacyProject: true);

        Assert.StartsWith("Write-Host '", command);
        Assert.EndsWith("; dotnet build 'C:\\work\\Old.csproj' -c 'Debug' --nologo", command);
        Assert.DoesNotContain(" -f ", command);
    }

    [Fact]
    public void Single_project_build_gets_the_solution_folder_that_lists_it()
    {
        var project = Write(@"repo\App\App.csproj", LegacyProject);
        Write(@"repo\App.sln", """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            """);
        // 同じプロジェクトを載せていない .sln が手前にあっても、載せている方を選ぶ。
        Write(@"repo\App\Other.sln", "");

        Assert.Equal(Path.Combine(_root, "repo"), MsBuildToolchain.SolutionDirectoryFor(project));
    }

    [Fact]
    public void Project_without_any_solution_uses_its_parent_folder()
    {
        var project = Write(@"lonely\App\App.csproj", LegacyProject);

        Assert.Equal(Path.Combine(_root, "lonely"), MsBuildToolchain.SolutionDirectoryFor(project));
    }

    [Fact]
    public void Legacy_project_build_passes_solution_dir_without_a_trailing_backslash()
    {
        var project = Write(@"repo2\App\App.csproj", LegacyProject);
        Write(@"repo2\App.sln", """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{44444444-4444-4444-4444-444444444444}"
            EndProject
            """);

        var command = CSharpBuildService.VisualStudioBuildCommand(@"C:\VS\MSBuild.exe", project, "Debug");

        Assert.Contains(" -p:SolutionDir='" + Path.Combine(_root, "repo2") + "/' ", command);
    }

    [Fact]
    public void Legacy_run_builds_then_starts_the_target_path_msbuild_reports()
    {
        var command = CSharpRunService.LegacyRunCommand(@"C:\VS\MSBuild.exe", @"C:\work\Old.csproj", "Debug");

        Assert.StartsWith("& 'C:\\VS\\MSBuild.exe' 'C:\\work\\Old.csproj' -restore", command);
        Assert.Contains("if ($LASTEXITCODE -eq 0)", command);
        Assert.Contains("-getProperty:TargetPath -p:Configuration='Debug'", command);
        // MSBuild のエラー文を実行ファイル名として起動しない。
        Assert.Contains("if (Test-Path -LiteralPath $loomoTarget -PathType Leaf) { & $loomoTarget }", command);
    }

    [Fact]
    public void Legacy_target_path_query_uses_the_same_solution_dir_as_the_build()
    {
        var project = Write(@"repo3\App\App.csproj", LegacyProject);
        var command = CSharpRunService.LegacyRunCommand(@"C:\VS\MSBuild.exe", project, "Debug");
        var solutionDir = " -p:SolutionDir='" + Path.Combine(_root, "repo3") + "/'";

        // ビルドと TargetPath の問い合わせの両方に同じ値（OutputPath が $(SolutionDir) 基準でも答えが揃う）。
        Assert.Equal(2, command.Split(new[] { solutionDir }, StringSplitOptions.None).Length - 1);
        Assert.Equal(Path.Combine(_root, "repo3") + "/", MsBuildToolchain.SolutionDirPropertyFor(project));
    }

    [Fact]
    public void Solution_dir_property_is_only_for_legacy_projects()
    {
        Assert.Null(MsBuildToolchain.SolutionDirPropertyFor(Write(@"sdk\Sdk.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""")));
        Assert.Null(MsBuildToolchain.SolutionDirPropertyFor(Write("Any.sln", "")));
    }

    [Theory]
    [InlineData("<TargetFramework>net48</TargetFramework>", true)]
    [InlineData("<TargetFrameworks>net472;net48</TargetFrameworks>", true)]
    [InlineData("<TargetFrameworks>net48;net8.0</TargetFrameworks>", false)]
    [InlineData("<TargetFramework>netstandard2.0</TargetFramework>", false)]
    [InlineData("<TargetFramework>net10.0-windows</TargetFramework>", false)]
    [InlineData("", false)]
    public void Sdk_projects_targeting_only_net_framework_count_as_framework(string frameworks, bool expected)
    {
        var project = Write($"F{Guid.NewGuid():N}.csproj",
            $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>{frameworks}</PropertyGroup></Project>""");

        Assert.Equal(expected, MsBuildToolchain.ContainsOnlyNetFrameworkProjects(project));
    }

    [Fact]
    public void Msbuild_without_property_query_support_is_not_used()
    {
        // バージョン情報の無い（＝17.8 未満と区別できない）実行ファイルは使わない。
        Assert.False(MsBuildToolchain.SupportsPropertyQueries(Write(@"old\MSBuild.exe", "")));
        if (MsBuildToolchain.FindVisualStudioMsBuild() is { } found)
            Assert.True(MsBuildToolchain.SupportsPropertyQueries(found));
    }
}
