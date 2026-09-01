using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.CSharp.Testing;

/// <summary>テストデバッグで起動するtesthostの対象を解決する。</summary>
public static class CSharpTestDebugTargetResolver
{
    /// <summary>選択した構成／TFMのMSBuild評価から、ビルド出力のテストアセンブリを解決する。
    /// プロジェクト名から推測せず、AssemblyName変更にも追従する。</summary>
    public static async Task<string?> ResolveAssemblyPathAsync(string projectPath, string? targetFramework,
        string configuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath)) return null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }
        };
        process.StartInfo.ArgumentList.Add("msbuild");
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(projectPath));
        // MSBuildはpropertyを1件だけ要求するとプレーンテキストを返すため、JSONとして扱えるよう
        // TargetFrameworkも同時に要求する（TargetPathの値自体はAssemblyNameを含む実評価結果）。
        process.StartInfo.ArgumentList.Add("/getProperty:TargetPath,TargetFramework");
        process.StartInfo.ArgumentList.Add("/p:Configuration=" +
            (string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration));
        process.StartInfo.ArgumentList.Add("/p:DesignTimeBuild=true");
        process.StartInfo.ArgumentList.Add("/nologo");
        if (!string.IsNullOrWhiteSpace(targetFramework))
            process.StartInfo.ArgumentList.Add("/p:TargetFramework=" + targetFramework);

        try { process.Start(); }
        catch { return null; }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) return null;

        try
        {
            using var document = JsonDocument.Parse(stdout);
            if (!document.RootElement.TryGetProperty("Properties", out var properties) ||
                !properties.TryGetProperty("TargetPath", out var target) ||
                target.ValueKind != JsonValueKind.String) return null;
            var path = target.GetString();
            return path is { Length: > 0 } && File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>VSTestのtesthostデバッグ待機プロセス。<c>VSTEST_HOST_DEBUG=1</c>でtesthostを停止させ、
/// PIDを検出した時点で呼び出し元へ返す。実際のデバッグ操作は既存DAP attachが担う。</summary>
public sealed class CSharpTestDebugProcess : IAsyncDisposable
{
    private static readonly Regex ProcessIdPattern = new(
        @"(?:Process\s+Id|プロセス\s*ID)\s*:\s*(?<pid>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Process _process;
    private readonly TaskCompletionSource<int> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _cancellation;
    private int _stopped;

    private CSharpTestDebugProcess(Process process, CancellationToken cancellationToken)
    {
        _process = process;
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnOutput;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;
        _cancellation = cancellationToken.Register(static state => ((CSharpTestDebugProcess)state!).Stop(), this);
    }

    /// <summary>testhostのPIDが通知されたときに発火する。イベントはプロセス出力スレッドから呼ばれる。</summary>
    public event Action<string>? Output;

    /// <summary>VSTestコンソールが終了したときに発火する。</summary>
    public event Action<int>? Exited;

    public int? TestHostProcessId { get; private set; }
    public Task Completion => _process.WaitForExitAsync();

    /// <summary>testhostのPIDを検出するまで待って返す。検出前に終了した場合は例外にする。</summary>
    public static async Task<CSharpTestDebugProcess> StartAsync(string assemblyPath, string filterExpression,
        string? workingDirectory = null, CancellationToken cancellationToken = default)
        => await StartAsync(new[] { assemblyPath }, filterExpression, workingDirectory, cancellationToken);

    /// <summary>複数のテストアセンブリを1回のVSTestセッションで待機させる（ソリューション範囲）。</summary>
    public static async Task<CSharpTestDebugProcess> StartAsync(IReadOnlyList<string> assemblyPaths,
        string filterExpression, string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (assemblyPaths is not { Count: > 0 })
            throw new ArgumentException("テストアセンブリが空です。", nameof(assemblyPaths));
        var paths = assemblyPaths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
            throw new ArgumentException("テストアセンブリが空です。", nameof(assemblyPaths));
        var missing = paths.FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
            throw new FileNotFoundException("テストアセンブリが見つかりません。", missing);
        if (string.IsNullOrWhiteSpace(filterExpression))
            throw new ArgumentException("テストフィルターが空です。", nameof(filterExpression));

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Directory.Exists(workingDirectory)
                    ? workingDirectory : Path.GetDirectoryName(paths[0])!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }
        };
        process.StartInfo.ArgumentList.Add("vstest");
        foreach (var path in paths) process.StartInfo.ArgumentList.Add(path);
        process.StartInfo.ArgumentList.Add("/TestCaseFilter:" + filterExpression);
        process.StartInfo.Environment["VSTEST_HOST_DEBUG"] = "1";

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("VSTestコンソールを起動できません。");
        }

        var runner = new CSharpTestDebugProcess(process, cancellationToken);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            // adapter不在PATH、テストDLL不正、testhost起動失敗などでPIDが出ない場合も、
            // IDEの操作を無期限に「準備中」にしない。
            await runner._ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return runner;
        }
        catch
        {
            await runner.DisposeAsync();
            throw;
        }
    }

    /// <summary>VSTestコンソールとtesthostをまとめて停止する。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _ready.TrySetException(new OperationCanceledException("テストデバッグを停止しました。"));
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch { }
        _cancellation.Dispose();
        _process.OutputDataReceived -= OnOutput;
        _process.ErrorDataReceived -= OnOutput;
        _process.Exited -= OnExited;
        _process.Dispose();
    }

    internal static int? ParseProcessId(string line)
    {
        var match = ProcessIdPattern.Match(line);
        return match.Success && int.TryParse(match.Groups["pid"].Value, out var pid) && pid > 0
            ? pid : null;
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not { Length: > 0 } line) return;
        Output?.Invoke(line);
        if (TestHostProcessId is null && ParseProcessId(line) is { } pid)
        {
            TestHostProcessId = pid;
            _ready.TrySetResult(pid);
        }
    }

    private void OnExited(object? sender, EventArgs args)
    {
        var code = -1;
        try { code = _process.ExitCode; } catch (InvalidOperationException) { }
        if (TestHostProcessId is null)
            _ready.TrySetException(new InvalidOperationException(
                $"testhostのPIDを取得する前にVSTestが終了しました（exit {code}）。"));
        Exited?.Invoke(code);
    }
}
