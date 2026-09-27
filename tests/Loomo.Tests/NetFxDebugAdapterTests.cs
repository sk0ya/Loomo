using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// .NET Framework の exe を、Loomo のデバッグサービス越しに同梱の NetFx アダプタでデバッグする（実プロセス）。
/// netcoredbg は .NET Framework を扱えないので、ここが通ることが「旧形式のプロジェクトをデバッグできる」の中身。
/// ICorDebug はデバッガと対象のビット数が合わないと繋がらないので、64bit と 32bit（Prefer32Bit）の両方を回す。
/// </summary>
[Collection(CSharpExternalProcessCollection.Name)]
public sealed class NetFxDebugAdapterTests : IDisposable
{
    private const string Source = """
        using System;

        static class P
        {
            static int Main(string[] args)
            {
                var name = "Loomo";
                var count = args.Length + 2;
                Console.WriteLine("hello " + name);
                return count + 4;
            }
        }
        """;

    private const int BreakLine = 9;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-netfxdbg-{Guid.NewGuid():N}");
    private readonly string? _previousAdapterDirectory = Environment.GetEnvironmentVariable(DebugAdapterCatalog.NetFxDirectoryVariable);

    public NetFxDebugAdapterTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(DebugAdapterCatalog.NetFxDirectoryVariable, AdapterDirectory());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DebugAdapterCatalog.NetFxDirectoryVariable, _previousAdapterDirectory);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* デバッグ対象の終了直後はまだ握られていることがある */ }
    }

    [Theory]
    [InlineData(Platform.AnyCpu)]
    [InlineData(Platform.AnyCpu32BitPreferred)]
    public async Task Breakpoint_variables_evaluation_and_exit_code_work_on_net_framework(Platform platform)
    {
        var (exe, source) = EmitFrameworkProgram(platform);
        var service = new NetcoredbgDebugService();
        var stopped = new TaskCompletionSource<DebugStopped>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<DebugExited>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        service.Stopped += (_, e) => stopped.TrySetResult(e);
        service.Exited += (_, e) => exited.TrySetResult(e);
        service.Output += (_, e) => { lock (output) output.AppendLine(e.Text); };

        await service.SetBreakpointsAsync(source, [new DebugBreakpoint(BreakLine)], CancellationToken.None);
        await service.StartAsync(new DebugLaunchConfig(exe, _root, Args: ["x"], JustMyCode: true), CancellationToken.None);
        try
        {
            var stop = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(BreakLine, stop.Line);
            Assert.Equal(Path.GetFullPath(source), stop.SourcePath, ignoreCase: true);

            var frames = await service.GetStackTraceAsync();
            Assert.Contains("Main", frames[0].Name);
            var scope = Assert.Single(await service.GetScopesAsync(frames[0].Id));
            var variables = await service.GetVariablesAsync(scope.VariablesReference);
            Assert.Contains(variables, v => v.Name == "count" && v.Value == "3" && v.Type == "int");
            Assert.Contains(variables, v => v.Name == "name" && v.Value == "\"Loomo\"");
            var args = Assert.Single(variables, v => v.Name == "args");
            Assert.Equal("\"x\"", Assert.Single(await service.GetVariablesAsync(args.VariablesReference)).Value);

            Assert.Equal("6", await service.EvaluateAsync("count * 2", frames[0].Id));
            Assert.Equal("5", await service.EvaluateAsync("name.Length", frames[0].Id));
            Assert.Equal("\"LOOMO\"", await service.EvaluateAsync("name.ToUpperInvariant()", frames[0].Id));

            await service.ContinueAsync();
            var exit = await exited.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(7, exit.ExitCode);
            lock (output) Assert.Contains("hello Loomo", output.ToString());
        }
        finally
        {
            await service.StopAsync();
            await service.WaitForIdleAsync();
        }
    }

    /// <summary>.NET Framework の mscorlib を参照して exe と ポータブル PDB を書く（旧形式ビルドの出力と同じ形）。</summary>
    private (string Exe, string Source) EmitFrameworkProgram(Platform platform)
    {
        var mscorlib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework", "v4.0.30319", "mscorlib.dll");
        Assert.True(File.Exists(mscorlib), ".NET Framework 4.x が見つかりません: " + mscorlib);

        var name = "NetFxApp" + platform;
        var source = Path.Combine(_root, name + ".cs");
        File.WriteAllText(source, Source, Encoding.UTF8);
        var tree = CSharpSyntaxTree.ParseText(Source, path: source, encoding: Encoding.UTF8);
        var compilation = CSharpCompilation.Create(name, [tree], [MetadataReference.CreateFromFile(mscorlib)],
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, platform: platform,
                optimizationLevel: OptimizationLevel.Debug));
        var exe = Path.Combine(_root, name + ".exe");
        using (var peStream = File.Create(exe))
        using (var pdbStream = File.Create(Path.ChangeExtension(exe, ".pdb")))
        {
            var result = compilation.Emit(peStream, pdbStream,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb,
                    pdbFilePath: Path.ChangeExtension(exe, ".pdb")));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }
        return (exe, source);
    }

    /// <summary>Loomo 本体の出力に置かれたアダプタ（64bit・32bit が同じフォルダーに並ぶ）。テストと同じ構成のものを使う。</summary>
    private static string AdapterDirectory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "sk0ya.Loomo.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = AppContext.BaseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        return Path.Combine(root!.FullName, "src", "Loomo.App", "bin", configuration, "netfxdbg");
    }
}
