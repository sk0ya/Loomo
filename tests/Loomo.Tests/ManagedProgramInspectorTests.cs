using System.IO;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using sk0ya.Loomo.Services.Debug;

namespace sk0ya.Loomo.Tests;

/// <summary>起動対象が .NET Framework か .NET（Core）か、何 bit で動くか——デバッグアダプタの選び分けの根拠。</summary>
public sealed class ManagedProgramInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-inspect-{Guid.NewGuid():N}");

    public ManagedProgramInspectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 一時フォルダーの後始末失敗はテスト結果に影響させない */ }
    }

    /// <summary>ランタイム設定を持たない管理コードの exe（＝ .NET Framework の exe と同じ形）を作る。</summary>
    private string EmitExe(Platform platform, string name)
    {
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText("static class P { static void Main() { } }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, platform: platform));
        var path = Path.Combine(_root, name + ".exe");
        using (var stream = File.Create(path))
        {
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }
        return path;
    }

    [Theory]
    [InlineData(Platform.AnyCpu, false)]
    [InlineData(Platform.AnyCpu32BitPreferred, true)]
    [InlineData(Platform.X86, true)]
    [InlineData(Platform.X64, false)]
    public void Framework_exe_bitness_follows_corflags(Platform platform, bool expected32Bit)
    {
        var runtime = ManagedProgramInspector.Inspect(EmitExe(platform, "App" + platform));

        Assert.Equal(ManagedRuntimeKind.NetFramework, runtime.Kind);
        Assert.Equal(expected32Bit, runtime.Is32Bit);
    }

    [Fact]
    public void Exe_with_runtimeconfig_is_dotnet_core_apphost_side()
    {
        var exe = EmitExe(Platform.AnyCpu, "Core");
        File.WriteAllText(Path.ChangeExtension(exe, ".runtimeconfig.json"), "{}");

        Assert.Equal(ManagedRuntimeKind.CoreClr, ManagedProgramInspector.Inspect(exe).Kind);
    }

    [Fact]
    public void Dll_and_unreadable_files_stay_on_netcoredbg()
    {
        Assert.Equal(ManagedRuntimeKind.CoreClr, ManagedProgramInspector.Inspect(Path.Combine(_root, "App.dll")).Kind);
        var notPe = Path.Combine(_root, "Broken.exe");
        File.WriteAllText(notPe, "not a PE file");
        Assert.Equal(ManagedRuntimeKind.CoreClr, ManagedProgramInspector.Inspect(notPe).Kind);
    }

    [Theory]
    [InlineData(Machine.I386, PEMagic.PE32, CorFlags.ILOnly, false)]
    [InlineData(Machine.I386, PEMagic.PE32, CorFlags.ILOnly | CorFlags.Prefers32Bit | CorFlags.Requires32Bit, true)]
    [InlineData(Machine.I386, PEMagic.PE32, CorFlags.ILOnly | CorFlags.Requires32Bit, true)]
    [InlineData(Machine.I386, PEMagic.PE32, (CorFlags)0, true)]
    [InlineData(Machine.Amd64, PEMagic.PE32Plus, CorFlags.ILOnly, false)]
    public void Bitness_rules_match_corflags(Machine machine, PEMagic magic, CorFlags flags, bool expected)
        => Assert.Equal(expected, ManagedProgramInspector.Is32BitImage(machine, magic, flags));

    [Fact]
    public void Framework_program_gets_the_bundled_adapter_of_matching_bitness()
    {
        var adapter = DebugAdapterResolver.ForProgram(EmitExe(Platform.AnyCpu32BitPreferred, "Legacy"));

        Assert.Equal("clr", adapter.LaunchType);
        Assert.EndsWith("sk0ya.Loomo.NetFxDebug.x86.exe", adapter.Executable);
    }

    [Fact]
    public void Core_program_keeps_netcoredbg()
    {
        var adapter = DebugAdapterResolver.ForProgram(Path.Combine(_root, "App.dll"));

        Assert.Equal("coreclr", adapter.LaunchType);
        Assert.Equal("netcoredbg", adapter.Executable);
    }
}
