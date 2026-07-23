using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>複数ワークスペースフォルダーにまたがる .sln/.csproj 探索（<see cref="DebugTargetResolver"/>）の検証。</summary>
public sealed class DebugTargetResolverMultiFolderTests : IDisposable
{
    private readonly string _folderWithoutProject;
    private readonly string _folderWithProject;
    private readonly string _csprojPath;

    public DebugTargetResolverMultiFolderTests()
    {
        _folderWithoutProject = Path.Combine(Path.GetTempPath(), $"loomo-debug-{Guid.NewGuid():N}-a");
        _folderWithProject = Path.Combine(Path.GetTempPath(), $"loomo-debug-{Guid.NewGuid():N}-b");
        Directory.CreateDirectory(_folderWithoutProject);
        Directory.CreateDirectory(_folderWithProject);
        _csprojPath = Path.Combine(_folderWithProject, "Sample.csproj");
        File.WriteAllText(_csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folderWithoutProject, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
        try { Directory.Delete(_folderWithProject, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private sealed class NullSession : IDebugSession
    {
        public bool IsBusy => false;
        public bool IsStopped => false;
        public bool IsTaskRunning { get; set; }
        public bool IsAdapterMissing => false;
        public string StatusMessage { get; set; } = "";
        public void RefreshAdapter() { }
        public System.Threading.CancellationToken BeginSession() => default;
        public void CancelSession() { }
        public void Append(DebugOutputCategory category, string text) { }
        public void WriteConsole(string output) { }
        public void ReportBuildOutput(string output) { }
        public void RequestOutput() { }
        public string? FindBuildTarget() => null;
        public void RaiseExecutionLine(string? path, int line0) { }
        public void RaiseFramePreview(string path, int line0) { }
        public void RaiseFrameActivated(string path, int line0) { }
        public void RaiseBreakpointsRefreshed(string path) { }
        public event Action? SessionStateChanged { add { } remove { } }
    }

    [Fact]
    public void HasCSharpProject_true_when_any_folder_has_a_project()
    {
        Assert.True(DebugTargetResolver.HasCSharpProject(new[] { _folderWithoutProject, _folderWithProject }));
    }

    [Fact]
    public void HasCSharpProject_false_when_no_folder_has_a_project()
    {
        Assert.False(DebugTargetResolver.HasCSharpProject(new[] { _folderWithoutProject }));
    }

    [Fact]
    public void HasCSharpProject_false_for_empty_folder_list()
    {
        Assert.False(DebugTargetResolver.HasCSharpProject(Array.Empty<string>()));
    }

    [Fact]
    public void FindBuildTarget_finds_csproj_in_secondary_folder_when_primary_has_none()
    {
        var workspace = new WorkspaceService(new SafetySettings());
        workspace.OpenFolder(_folderWithoutProject);
        workspace.AddFolder(_folderWithProject);

        var target = DebugTargetResolver.FindBuildTarget(workspace, new NullSession());

        Assert.Equal(Path.GetFullPath(_csprojPath), target is null ? null : Path.GetFullPath(target));
    }
}
