using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 停止 → 行末の値をエディタへ → 続行で消す、の旅程を <see cref="DebugInspectionViewModel"/> で通す。
/// 値は scopes→variables（変数ツリーと同じ経路）から取り、アダプタ（netcoredbg／NetFx／js-debug）には依存しない。
/// </summary>
public sealed class DebugInspectionInlineValuesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loomo-inline-values-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;

    public DebugInspectionInlineValuesTests()
    {
        Directory.CreateDirectory(_dir);
        _source = Path.Combine(_dir, "Program.cs");
        File.WriteAllText(_source, """
            class Program
            {
                static int Foo(int a, string name)
                {
                    var x = a + 1;
                    Console.WriteLine(name);
                    return x * 2;
                }
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    [Fact]
    public async Task Stopping_sends_values_for_the_function_up_to_the_stop_line_and_clear_removes_them()
    {
        var session = new RecordingSession();
        var debug = FakeDebugService.Create(new DebugStackFrame(1, "Foo", _source, Line: 6));  // "Console.WriteLine(name);"
        var inspection = new DebugInspectionViewModel(debug, session);

        await inspection.LoadStackAsync();
        var values = await session.WaitForNonEmptyAsync();

        Assert.Equal(_source, values.SourcePath);
        Assert.Equal(new[]
        {
            new DebugInlineValueLine(2, "a = 1, name = \"abc\""),
            new DebugInlineValueLine(4, "x = 2, a = 1"),
            new DebugInlineValueLine(5, "name = \"abc\""),
        }, values.Lines);  // 停止行より下（return x * 2;）には出さない

        inspection.Clear();  // 続行・ステップ・終了
        Assert.True(session.Last.IsEmpty);
    }

    [Fact]
    public async Task Disabled_setting_collects_nothing_and_refresh_clears()
    {
        var session = new RecordingSession { InlineValuesEnabled = false };
        var debug = FakeDebugService.Create(new DebugStackFrame(1, "Foo", _source, Line: 6));
        var inspection = new DebugInspectionViewModel(debug, session);

        await inspection.LoadStackAsync();
        await Task.Delay(200);
        Assert.All(session.Received, v => Assert.True(v.IsEmpty));

        // 設定を ON に戻すと、次の停止を待たずにいまのフレームで出し直す。
        session.InlineValuesEnabled = true;
        await inspection.RefreshInlineValuesAsync();
        Assert.False(session.Last.IsEmpty);
    }

    [Fact]
    public async Task Frame_without_source_sends_an_empty_set()
    {
        var session = new RecordingSession();
        var debug = FakeDebugService.Create(new DebugStackFrame(1, "[外部コード]", null, Line: 0));
        var inspection = new DebugInspectionViewModel(debug, session);

        await inspection.LoadStackAsync();
        await Task.Delay(200);
        Assert.True(session.Last.IsEmpty);
    }

    private sealed class RecordingSession : IDebugSession
    {
        private readonly object _gate = new();
        public List<DebugInlineValueSet> Received { get; } = new();
        public DebugInlineValueSet Last { get { lock (_gate) return Received.Count == 0 ? DebugInlineValueSet.Empty : Received[^1]; } }

        public async Task<DebugInlineValueSet> WaitForNonEmptyAsync()
        {
            for (int i = 0; i < 100; i++)
            {
                lock (_gate)
                    if (Received.LastOrDefault() is { IsEmpty: false } v) return v;
                await Task.Delay(20);
            }
            throw new TimeoutException("行末の値が届かなかった");
        }

        public bool InlineValuesEnabled { get; set; } = true;
        public void RaiseInlineValues(DebugInlineValueSet values) { lock (_gate) Received.Add(values); }

        public bool IsBusy => true;
        public bool IsStopped => true;
        public bool IsTaskRunning { get; set; }
        public bool IsAdapterMissing => false;
        public string StatusMessage { get; set; } = "";
        public void RefreshAdapter() { }
        public CancellationToken BeginSession() => default;
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

    /// <summary>停止中のアダプタを装う。スタック 1 フレーム・スコープ（Locals と重い Global）・変数だけを答え、
    /// それ以外の要求は既定値で返す（式評価は失敗扱い——行末の値が式評価に頼っていないことの確認にもなる）。</summary>
    public class FakeDebugService : DispatchProxy
    {
        private DebugStackFrame _frame = null!;

        public static IDebugService Create(DebugStackFrame frame)
        {
            var proxy = DispatchProxy.Create<IDebugService, FakeDebugService>();
            ((FakeDebugService)(object)proxy)._frame = frame;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IDebugService.GetStackTraceAsync):
                    return Task.FromResult<IReadOnlyList<DebugStackFrame>>(new[] { _frame });
                case nameof(IDebugService.GetScopesAsync):
                    return Task.FromResult<IReadOnlyList<DebugScope>>(new[]
                    {
                        new DebugScope("Locals", 10, false),
                        new DebugScope("Global", 99, true),
                    });
                case nameof(IDebugService.GetVariablesAsync):
                    IReadOnlyList<DebugVariable> vars = (int)args![0]! == 10
                        ? new[]
                        {
                            new DebugVariable("a", "1", "int", 0),
                            new DebugVariable("name", "\"abc\"", "string", 0),
                            new DebugVariable("x", "2", "int", 0),
                        }
                        : throw new InvalidOperationException("重いスコープは読まないはず");
                    return Task.FromResult(vars);
                case nameof(IDebugService.EvaluateAsync):
                    return Task.FromResult("(評価エラー)");
                case nameof(IDebugService.GetThreadsAsync):
                    return Task.FromResult<IReadOnlyList<DebugThread>>(Array.Empty<DebugThread>());
                case nameof(IDebugService.GetModulesAsync):
                    return Task.FromResult<IReadOnlyList<DebugModule>>(Array.Empty<DebugModule>());
            }

            var ret = method.ReturnType;
            if (ret == typeof(Task)) return Task.CompletedTask;
            if (ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = ret.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
            }
            return ret.IsValueType && ret != typeof(void) ? Activator.CreateInstance(ret) : null;
        }
    }
}
