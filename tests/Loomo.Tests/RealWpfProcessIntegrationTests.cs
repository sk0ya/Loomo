using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Xunit.Sdk;

namespace sk0ya.Loomo.Tests;

/// <summary>実アプリプロセスがC#編集面をUI Automationへ公開することの確認。
/// 編集操作の細部はEditor.Controlsの入力契約に依存するため、ここでは起動後の公開面と
/// C#ワークスペースの到達性だけを確認する。通常のテストではデスクトップを占有しないよう省略する。</summary>
public sealed class RealWpfProcessIntegrationTests
{
    [RealWpfFact]
    public void App_process_exposes_the_csharp_editor_surface()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "sk0ya.Loomo.App.exe");
        Assert.True(File.Exists(executable), $"App executable was not found: {executable}");

        var workspace = FindFixtureWorkspace();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(75);
            AutomationElement? window = null;
            while (DateTime.UtcNow < deadline)
            {
                process.Refresh();
                if (process.HasExited)
                    Assert.Fail($"Loomo exited before exposing its window (exit code {process.ExitCode}).");

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    window = AutomationElement.FromHandle(process.MainWindowHandle);
                    if (HasCSharpSurface(window))
                        break;
                }

                Thread.Sleep(500);
            }

            Assert.NotNull(window);
            Assert.Equal("Loomo", window!.Current.Name);
            Assert.NotNull(FindById(window, "WorkspaceButton"));
            Assert.NotNull(FindById(window, "CSharpSolutionTree"));
            var canvas = FindById(window, "Canvas");
            Assert.NotNull(canvas);
            Assert.True(canvas!.TryGetCurrentPattern(TextPattern.Pattern, out var pattern));
            var text = ((TextPattern)pattern).DocumentRange.GetText(-1);
            Assert.Contains("class FeatureService", text, StringComparison.Ordinal);
            Assert.Contains(
                FindAllById(window, "TabTitle").Select(static element => element.Current.Name),
                static name => name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                FindAllById(window, "StatusText").Select(static element => element.Current.Name),
                static name => name.Contains("LSP: ready", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(5000))
                        process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
        }
    }

    [RealWpfFact]
    public void App_process_accepts_csharp_edit_and_save_from_the_desktop_input_path()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "sk0ya.Loomo.App.exe");
        Assert.True(File.Exists(executable), $"App executable was not found: {executable}");

        string workspace = FindFixtureWorkspace();
        string sourcePath = Path.Combine(workspace, "src", "Feature", "FeatureService.cs");
        byte[] originalBytes = File.ReadAllBytes(sourcePath);
        string original = Encoding.UTF8.GetString(originalBytes);
        const string marker = "// Loomo real WPF edit smoke";
        Assert.DoesNotContain(marker, original, StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            var window = WaitForCSharpWindow(process!, TimeSpan.FromSeconds(75));
            var canvas = FindById(window, "Canvas");
            Assert.NotNull(canvas);
            Assert.True(canvas!.TryGetCurrentPattern(TextPattern.Pattern, out var pattern));
            Assert.Contains("class FeatureService", ((TextPattern)pattern).DocumentRange.GetText(-1),
                StringComparison.Ordinal);

            SetForegroundWindow(process!.MainWindowHandle);
            Thread.Sleep(100);
            canvas.SetFocus();
            Assert.True(WaitUntil(() =>
            {
                try { return AutomationElement.FocusedElement.Current.AutomationId == "Canvas"; }
                catch (ElementNotAvailableException) { return false; }
            }, TimeSpan.FromSeconds(5)), "EditorCanvas did not receive keyboard focus.");

            SendVirtualKey(VirtualKey.Control, keyUp: false);
            SendVirtualKey(VirtualKey.End, keyUp: false);
            SendVirtualKey(VirtualKey.End, keyUp: true);
            SendVirtualKey(VirtualKey.Control, keyUp: true);
            // The user settings may enable Vim mode.  Enter insert mode first; in
            // plain mode this harmlessly becomes the same printable-input path.
            SendVirtualKey(VirtualKey.I, keyUp: false);
            SendVirtualKey(VirtualKey.I, keyUp: true);
            SendVirtualKey(VirtualKey.Return, keyUp: false);
            SendVirtualKey(VirtualKey.Return, keyUp: true);
            SendUnicodeText(marker);

            Assert.True(WaitUntil(() =>
            {
                try
                {
                    var current = FindById(window, "Canvas");
                    return current?.TryGetCurrentPattern(TextPattern.Pattern, out var currentPattern) == true &&
                        ((TextPattern)currentPattern).DocumentRange.GetText(-1).Contains(marker, StringComparison.Ordinal);
                }
                catch (ElementNotAvailableException) { return false; }
            }, TimeSpan.FromSeconds(10)), "The marker did not reach the C# editor buffer.");

            SendVirtualKey(VirtualKey.Control, keyUp: false);
            SendVirtualKey(VirtualKey.S, keyUp: false);
            SendVirtualKey(VirtualKey.S, keyUp: true);
            SendVirtualKey(VirtualKey.Control, keyUp: true);

            Assert.True(WaitUntil(() =>
            {
                try { return File.ReadAllText(sourcePath).Contains(marker, StringComparison.Ordinal); }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(10)), "Ctrl+S did not persist the C# edit to disk.");
        }
        finally
        {
            CloseProcess(process!);
            File.WriteAllBytes(sourcePath, originalBytes);
        }
    }

    [RealWpfFact]
    public void App_processes_csharp_diagnostic_quick_fix_and_save_from_the_desktop_input_path()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "sk0ya.Loomo.App.exe");
        Assert.True(File.Exists(executable), $"App executable was not found: {executable}");

        string workspace = FindFixtureWorkspace();
        string sourcePath = Path.Combine(workspace, "src", "Feature", "FeatureService.cs");
        byte[] originalBytes = File.ReadAllBytes(sourcePath);
        string original = Encoding.UTF8.GetString(originalBytes);
        Assert.Contains("=> _value;", original, StringComparison.Ordinal);
        Assert.DoesNotContain("this._value", original, StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            var window = WaitForCSharpWindow(process!, TimeSpan.FromSeconds(75));
            SetForegroundWindow(process!.MainWindowHandle);
            Thread.Sleep(100);
            AutomationElement? fileNode = null;
            Assert.True(WaitUntil(() =>
            {
                fileNode = FindByNameAndType(window, "FeatureService.cs", ControlType.TreeItem);
                return fileNode is not null;
            }, TimeSpan.FromSeconds(30)), "FeatureService.cs did not appear in C# Solution Explorer.");
            Assert.True(fileNode!.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern));
            ((SelectionItemPattern)selectionPattern).Select();
            fileNode.SetFocus();
            SendVirtualKey(VirtualKey.Return, keyUp: false);
            SendVirtualKey(VirtualKey.Return, keyUp: true);
            Assert.True(WaitUntil(() =>
            {
                try
                {
                    if (!FindAllById(window, "TabTitle").Any(static element =>
                            string.Equals(element.Current.Name, "FeatureService.cs", StringComparison.OrdinalIgnoreCase)))
                        return false;
                    var current = FindById(window, "Canvas");
                    return current?.TryGetCurrentPattern(TextPattern.Pattern, out var currentPattern) == true &&
                        ((TextPattern)currentPattern).DocumentRange.GetText(-1)
                            .Contains("class FeatureService", StringComparison.Ordinal);
                }
                catch (ElementNotAvailableException) { return false; }
            }, TimeSpan.FromSeconds(15)), "FeatureService.cs did not become the active editor tab.");

            var canvas = FindById(window, "Canvas");
            Assert.NotNull(canvas);
            Assert.True(canvas!.TryGetCurrentPattern(TextPattern.Pattern, out var pattern));
            Assert.True(WaitUntil(() => FindByName(window, "Feature") is not null,
                TimeSpan.FromSeconds(30)), "C# Solution Explorer did not finish loading the Feature project.");

            SetForegroundWindow(process!.MainWindowHandle);
            Thread.Sleep(100);
            canvas.SetFocus();
            Assert.True(WaitUntil(() =>
            {
                try { return AutomationElement.FocusedElement.Current.AutomationId == "Canvas"; }
                catch (ElementNotAvailableException) { return false; }
            }, TimeSpan.FromSeconds(5)), "EditorCanvas did not receive keyboard focus.");

            // The host analyzer is deliberately debounced and builds a Roslyn
            // compilation off the UI thread.  Give the initial project pass time
            // to publish the SA1101 diagnostic before requesting its Quick Fix.
            Thread.Sleep(5000);

            // Select the actual diagnostic token through the public TextPattern
            // surface.  This avoids making the test depend on Vim's Home/Down
            // semantics while still keeping the process boundary intact.
            var currentCanvas = FindById(window, "Canvas");
            Assert.NotNull(currentCanvas);
            Assert.True(WaitUntil(() =>
            {
                try
                {
                    return currentCanvas!.TryGetCurrentPattern(TextPattern.Pattern, out var currentPattern) &&
                        ((TextPattern)currentPattern).DocumentRange.GetText(-1)
                            .Contains("_value;", StringComparison.Ordinal);
                }
                catch (ElementNotAvailableException) { return false; }
            }, TimeSpan.FromSeconds(15)),
                $"The active C# editor did not expose the diagnostic token: {GetAutomationText(currentCanvas)}");
            Assert.True(currentCanvas!.TryGetCurrentPattern(TextPattern.Pattern, out var currentTextPattern));
            canvas.SetFocus();
            Assert.True(WaitUntil(() =>
            {
                try { return AutomationElement.FocusedElement.Current.AutomationId == "Canvas"; }
                catch (ElementNotAvailableException) { return false; }
            }, TimeSpan.FromSeconds(5)), "EditorCanvas did not retain keyboard focus for Quick Fix.");
            var diagnosticRange = ((TextPattern)currentTextPattern).DocumentRange.FindText(
                "_value;", backward: false, ignoreCase: false);
            Assert.NotNull(diagnosticRange);
            diagnosticRange!.Select();

            SendVirtualKey(VirtualKey.Alt, keyUp: false);
            SendVirtualKey(VirtualKey.Return, keyUp: false);
            SendVirtualKey(VirtualKey.Return, keyUp: true);
            SendVirtualKey(VirtualKey.Alt, keyUp: true);

            Assert.True(WaitUntil(() =>
            {
                try
                {
                    return FindAllById(window, "StatusText").Select(static element => element.Current.Name)
                        .Any(static name => name.Contains("Quick Fix:", StringComparison.Ordinal) &&
                                            !name.Contains("no fixes available", StringComparison.OrdinalIgnoreCase) &&
                                            name.Contains("available", StringComparison.OrdinalIgnoreCase));
                }
                catch (ElementNotAvailableException) { return false; }
            }, TimeSpan.FromSeconds(30)),
                $"Alt+Enter did not expose a C# Quick Fix. Status: {string.Join(" | ", FindAllById(window, "StatusText").Select(static element => element.Current.Name))}");

            canvas.SetFocus();
            SendVirtualKey(VirtualKey.Return, keyUp: false);
            SendVirtualKey(VirtualKey.Return, keyUp: true);

            var preview = WaitForTopLevelWindow("編集プレビュー", TimeSpan.FromSeconds(10));
            Assert.NotNull(preview);
            var applyButton = preview!.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "適用")));
            Assert.NotNull(applyButton);
            Assert.True(applyButton!.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern));
            ((InvokePattern)invokePattern).Invoke();

            Assert.True(WaitUntil(() =>
            {
                try
                {
                    var current = FindById(window, "Canvas");
                    return current?.TryGetCurrentPattern(TextPattern.Pattern, out var currentPattern) == true &&
                        ((TextPattern)currentPattern).DocumentRange.GetText(-1)
                            .Contains("this._value", StringComparison.Ordinal);
                }
                catch (ElementNotAvailableException) { return false; }
            }, TimeSpan.FromSeconds(30)),
                $"Quick Fix did not update the C# editor buffer. Status: {string.Join(" | ", FindAllById(window, "StatusText").Select(static element => element.Current.Name))}");

            SendVirtualKey(VirtualKey.Control, keyUp: false);
            SendVirtualKey(VirtualKey.S, keyUp: false);
            SendVirtualKey(VirtualKey.S, keyUp: true);
            SendVirtualKey(VirtualKey.Control, keyUp: true);
            Assert.True(WaitUntil(() =>
            {
                try { return File.ReadAllText(sourcePath).Contains("this._value", StringComparison.Ordinal); }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(10)), "Quick Fix result was not saved to disk.");

            // Continue the same desktop journey through Solution Explorer so the
            // C#-specific Fix result is proven usable by the actual Build/Test actions.
            AutomationElement? testProjectNode = null;
            Assert.True(WaitUntil(() =>
            {
                testProjectNode = FindByNameAndType(window, "Feature.Tests", ControlType.TreeItem);
                return testProjectNode is not null;
            }, TimeSpan.FromSeconds(15)), "Feature.Tests did not appear in C# Solution Explorer.");
            Assert.True(testProjectNode!.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var testProjectSelection));
            ((SelectionItemPattern)testProjectSelection).Select();
            var buildButton = WaitForAutomationId("CSharpSolutionBuild", TimeSpan.FromSeconds(10));
            Assert.NotNull(buildButton);
            Assert.True(buildButton!.TryGetCurrentPattern(InvokePattern.Pattern, out var buildInvoke));
            ((InvokePattern)buildInvoke).Invoke();
            Assert.True(WaitUntil(() => HasAutomationIdText(window, "CSharpSolutionExecutionStatus", "ビルド成功"),
                TimeSpan.FromMinutes(3)),
                $"The Solution Explorer Build action did not succeed. Status: {GetAutomationName(window, "CSharpSolutionExecutionStatus")}");

            var testButton = WaitForAutomationId("CSharpSolutionTest", TimeSpan.FromSeconds(10));
            Assert.NotNull(testButton);
            Assert.True(testButton!.TryGetCurrentPattern(InvokePattern.Pattern, out var testInvoke));
            ((InvokePattern)testInvoke).Invoke();
            Assert.True(WaitUntil(() => HasAutomationIdText(window, "CSharpSolutionExecutionStatus", "テスト成功"),
                TimeSpan.FromMinutes(3)),
                $"The Solution Explorer Test action did not succeed. Status: {GetAutomationName(window, "CSharpSolutionExecutionStatus")}");
        }
        finally
        {
            CloseProcess(process!);
            File.WriteAllBytes(sourcePath, originalBytes);
        }
    }

    [RealWpfFact]
    public void App_rejects_a_quick_fix_when_the_open_file_changes_during_preview()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "sk0ya.Loomo.App.exe");
        Assert.True(File.Exists(executable), $"App executable was not found: {executable}");

        string workspace = FindFixtureWorkspace();
        string sourcePath = Path.Combine(workspace, "src", "Feature", "FeatureService.cs");
        byte[] originalBytes = File.ReadAllBytes(sourcePath);
        const string externalMarker = "// Loomo quick fix external change";
        Assert.DoesNotContain(externalMarker, Encoding.UTF8.GetString(originalBytes),
            StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            var window = WaitForCSharpWindow(process!, TimeSpan.FromSeconds(75));
            OpenFeatureService(process!, window);
            var preview = OpenQuickFixPreview(process!, window);

            // Keep the preview open while another writer changes the real file.  The
            // editor buffer is intentionally unchanged; the transaction must notice
            // the disk snapshot mismatch before applying the Quick Fix.
            File.AppendAllText(sourcePath, Environment.NewLine + externalMarker,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.Contains(externalMarker, File.ReadAllText(sourcePath), StringComparison.Ordinal);

            var applyButton = preview.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "適用")));
            Assert.NotNull(applyButton);
            Assert.True(applyButton!.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern));
            ((InvokePattern)invokePattern).Invoke();

            Assert.True(WaitUntil(() => FindAllById(window, "StatusText").Select(static element => element.Current.Name)
                    .Any(static name => name.Contains("外部変更", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(15)),
                $"Quick Fix external-change rejection was not reported. Status: {string.Join(" | ", FindAllById(window, "StatusText").Select(static element => element.Current.Name))}");
            Assert.Contains(externalMarker, File.ReadAllText(sourcePath), StringComparison.Ordinal);
            Assert.DoesNotContain("this._value", File.ReadAllText(sourcePath), StringComparison.Ordinal);

            var canvas = FindById(window, "Canvas");
            Assert.NotNull(canvas);
            Assert.True(canvas!.TryGetCurrentPattern(TextPattern.Pattern, out var pattern));
            Assert.Contains("_value;", ((TextPattern)pattern).DocumentRange.GetText(-1),
                StringComparison.Ordinal);
        }
        finally
        {
            CloseProcess(process!);
            File.WriteAllBytes(sourcePath, originalBytes);
        }
    }

    [RealWpfFact]
    public void App_runs_csharp_code_generation_through_the_command_palette()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "sk0ya.Loomo.App.exe");
        Assert.True(File.Exists(executable), $"App executable was not found: {executable}");

        string workspace = FindFixtureWorkspace();
        string sourcePath = Path.Combine(workspace, "src", "Feature", "FeatureService.cs");
        byte[] originalBytes = File.ReadAllBytes(sourcePath);
        string original = Encoding.UTF8.GetString(originalBytes);
        Assert.DoesNotContain("GetHashCode()", original, StringComparison.Ordinal);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            var window = WaitForCSharpWindow(process!, TimeSpan.FromSeconds(75));
            OpenFeatureService(process!, window);

            var canvas = FindById(window, "Canvas");
            Assert.NotNull(canvas);
            Assert.True(canvas!.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern));
            var classRange = ((TextPattern)textPattern).DocumentRange.FindText(
                "FeatureService", backward: false, ignoreCase: false);
            Assert.NotNull(classRange);
            classRange!.Select();
            canvas.SetFocus();

            SendVirtualKey(VirtualKey.Control, keyUp: false);
            SendVirtualKey(VirtualKey.Shift, keyUp: false);
            SendVirtualKey(VirtualKey.P, keyUp: false);
            SendVirtualKey(VirtualKey.P, keyUp: true);
            SendVirtualKey(VirtualKey.Shift, keyUp: true);
            SendVirtualKey(VirtualKey.Control, keyUp: true);

            var paletteInput = WaitForAutomationId("PaletteInput", TimeSpan.FromSeconds(10));
            Assert.NotNull(paletteInput);
            paletteInput!.SetFocus();
            SendUnicodeText("Equals／GetHashCodeを生成");
            SendVirtualKey(VirtualKey.Return, keyUp: false);
            SendVirtualKey(VirtualKey.Return, keyUp: true);

            var preview = WaitForTopLevelWindow("編集プレビュー", TimeSpan.FromSeconds(15));
            Assert.NotNull(preview);
            var applyButton = preview!.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "適用")));
            Assert.NotNull(applyButton);
            Assert.True(applyButton!.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern));
            ((InvokePattern)invokePattern).Invoke();

            Assert.True(WaitUntil(() =>
            {
                try
                {
                    var current = FindById(window, "Canvas");
                    if (current?.TryGetCurrentPattern(TextPattern.Pattern, out var currentPattern) != true)
                        return false;
                    var updated = ((TextPattern)currentPattern).DocumentRange.GetText(-1);
                    return updated.Contains("bool Equals", StringComparison.Ordinal) &&
                           updated.Contains("int GetHashCode", StringComparison.Ordinal);
                }
                catch (ElementNotAvailableException) { return false; }
            }, TimeSpan.FromSeconds(30)), "C# code generation did not update the editor buffer.");

            SendVirtualKey(VirtualKey.Control, keyUp: false);
            SendVirtualKey(VirtualKey.S, keyUp: false);
            SendVirtualKey(VirtualKey.S, keyUp: true);
            SendVirtualKey(VirtualKey.Control, keyUp: true);
            Assert.True(WaitUntil(() =>
            {
                try
                {
                    var updated = File.ReadAllText(sourcePath);
                    return updated.Contains("bool Equals", StringComparison.Ordinal) &&
                           updated.Contains("int GetHashCode", StringComparison.Ordinal);
                }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(15)), "C# generated code was not saved to disk.");
        }
        finally
        {
            CloseProcess(process!);
            File.WriteAllBytes(sourcePath, originalBytes);
        }
    }

    private static bool HasCSharpSurface(AutomationElement window)
        => FindById(window, "CSharpSolutionTree") is not null &&
           FindById(window, "Canvas") is not null &&
           FindAllById(window, "TabTitle").Any(static element =>
               element.Current.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    private static void OpenFeatureService(Process process, AutomationElement window)
    {
        SetForegroundWindow(process.MainWindowHandle);
        Thread.Sleep(100);
        AutomationElement? fileNode = null;
        Assert.True(WaitUntil(() =>
        {
            fileNode = FindByNameAndType(window, "FeatureService.cs", ControlType.TreeItem);
            return fileNode is not null;
        }, TimeSpan.FromSeconds(30)), "FeatureService.cs did not appear in C# Solution Explorer.");
        Assert.True(fileNode!.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern));
        ((SelectionItemPattern)selectionPattern).Select();
        fileNode.SetFocus();
        SendVirtualKey(VirtualKey.Return, keyUp: false);
        SendVirtualKey(VirtualKey.Return, keyUp: true);
        Assert.True(WaitUntil(() =>
        {
            try
            {
                if (!FindAllById(window, "TabTitle").Any(static element =>
                        string.Equals(element.Current.Name, "FeatureService.cs", StringComparison.OrdinalIgnoreCase)))
                    return false;
                var current = FindById(window, "Canvas");
                return current?.TryGetCurrentPattern(TextPattern.Pattern, out var currentPattern) == true &&
                    ((TextPattern)currentPattern).DocumentRange.GetText(-1)
                        .Contains("class FeatureService", StringComparison.Ordinal);
            }
            catch (ElementNotAvailableException) { return false; }
        }, TimeSpan.FromSeconds(15)), "FeatureService.cs did not become the active editor tab.");
    }

    private static AutomationElement OpenQuickFixPreview(Process process, AutomationElement window)
    {
        var canvas = FindById(window, "Canvas");
        Assert.NotNull(canvas);
        Assert.True(WaitUntil(() => FindByName(window, "Feature") is not null,
            TimeSpan.FromSeconds(30)), "C# Solution Explorer did not finish loading the Feature project.");

        SetForegroundWindow(process.MainWindowHandle);
        Thread.Sleep(100);
        canvas!.SetFocus();
        Assert.True(WaitUntil(() =>
        {
            try { return AutomationElement.FocusedElement.Current.AutomationId == "Canvas"; }
            catch (ElementNotAvailableException) { return false; }
        }, TimeSpan.FromSeconds(5)), "EditorCanvas did not receive keyboard focus for Quick Fix.");
        Thread.Sleep(5000);

        Assert.True(WaitUntil(() =>
        {
            try
            {
                var current = FindById(window, "Canvas");
                return current?.TryGetCurrentPattern(TextPattern.Pattern, out var currentPattern) == true &&
                    ((TextPattern)currentPattern).DocumentRange.GetText(-1)
                        .Contains("_value;", StringComparison.Ordinal);
            }
            catch (ElementNotAvailableException) { return false; }
        }, TimeSpan.FromSeconds(15)), "The active C# editor did not expose the diagnostic token.");

        Assert.True(canvas.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern));
        var diagnosticRange = ((TextPattern)textPattern).DocumentRange.FindText(
            "_value;", backward: false, ignoreCase: false);
        Assert.NotNull(diagnosticRange);
        diagnosticRange!.Select();
        canvas.SetFocus();

        SendVirtualKey(VirtualKey.Alt, keyUp: false);
        SendVirtualKey(VirtualKey.Return, keyUp: false);
        SendVirtualKey(VirtualKey.Return, keyUp: true);
        SendVirtualKey(VirtualKey.Alt, keyUp: true);

        Assert.True(WaitUntil(() =>
        {
            try
            {
                return FindAllById(window, "StatusText").Select(static element => element.Current.Name)
                    .Any(static name => name.Contains("Quick Fix:", StringComparison.Ordinal) &&
                                        !name.Contains("no fixes available", StringComparison.OrdinalIgnoreCase) &&
                                        name.Contains("available", StringComparison.OrdinalIgnoreCase));
            }
            catch (ElementNotAvailableException) { return false; }
        }, TimeSpan.FromSeconds(30)), "Alt+Enter did not expose a C# Quick Fix.");

        canvas.SetFocus();
        SendVirtualKey(VirtualKey.Return, keyUp: false);
        SendVirtualKey(VirtualKey.Return, keyUp: true);
        var preview = WaitForTopLevelWindow("編集プレビュー", TimeSpan.FromSeconds(10));
        Assert.NotNull(preview);
        return preview!;
    }

    private static AutomationElement WaitForCSharpWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
                Assert.Fail($"Loomo exited before exposing its window (exit code {process.ExitCode}).");

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                var window = AutomationElement.FromHandle(process.MainWindowHandle);
                if (HasCSharpSurface(window)) return window;
            }

            Thread.Sleep(500);
        }

        Assert.Fail("Loomo did not expose a C# editor surface in time.");
        return null!;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return condition();
    }

    private static void CloseProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
    }

    private enum VirtualKey : ushort
    {
        Alt = 0x12,
        Control = 0x11,
        Down = 0x28,
        End = 0x23,
        Home = 0x24,
        I = 0x49,
        P = 0x50,
        Return = 0x0D,
        Right = 0x27,
        S = 0x53,
        Shift = 0x10,
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, NativeInput[] inputs, int size);

    private static void SendVirtualKey(VirtualKey key, bool keyUp)
    {
        var input = new NativeInput
        {
            Type = 1,
            Data = new NativeInputUnion
            {
                Keyboard = new NativeKeyboardInput
                {
                    VirtualKey = (ushort)key,
                    Flags = keyUp ? 0x0002u : 0u,
                },
            },
        };
        Assert.Equal(1u, SendInput(1, [input], Marshal.SizeOf<NativeInput>()));
    }

    private static void SendRepeatedVirtualKey(VirtualKey key, int count)
    {
        for (var i = 0; i < count; i++)
        {
            SendVirtualKey(key, keyUp: false);
            SendVirtualKey(key, keyUp: true);
        }
    }

    private static void SendUnicodeText(string text)
    {
        foreach (char character in text)
        {
            var inputs = new[]
            {
                new NativeInput
                {
                    Type = 1,
                    Data = new NativeInputUnion
                    {
                        Keyboard = new NativeKeyboardInput
                        {
                            ScanCode = character,
                            Flags = 0x0004u,
                        },
                    },
                },
                new NativeInput
                {
                    Type = 1,
                    Data = new NativeInputUnion
                    {
                        Keyboard = new NativeKeyboardInput
                        {
                            ScanCode = character,
                            Flags = 0x0004u | 0x0002u,
                        },
                    },
                },
            };
            Assert.Equal(2u, SendInput(2, inputs, Marshal.SizeOf<NativeInput>()));
        }
    }

    private static AutomationElement? FindById(AutomationElement root, string id)
        => root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static AutomationElement? FindByName(AutomationElement root, string name)
        => root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));

    private static AutomationElement? FindByNameAndType(
        AutomationElement root, string name, ControlType controlType)
        => root.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.ControlTypeProperty, controlType)));

    private static string GetAutomationText(AutomationElement? element)
    {
        try
        {
            if (element is null || !element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                return "(unavailable)";
            var text = ((TextPattern)pattern).DocumentRange.GetText(-1);
            var head = text[..Math.Min(160, text.Length)];
            var tail = text.Length > 160 ? text[^160..] : string.Empty;
            return $"length={text.Length}; head={head}; tail={tail}";
        }
        catch (ElementNotAvailableException) { return "(unavailable)"; }
    }

    private static AutomationElement? WaitForTopLevelWindow(string namePart, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Subtree,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            for (var i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                try
                {
                    if (window.Current.Name.Contains(namePart, StringComparison.Ordinal))
                        return window;
                }
                catch (ElementNotAvailableException) { }
            }
            Thread.Sleep(100);
        }
        return null;
    }

    private static AutomationElement? WaitForAutomationId(string id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = AutomationElement.RootElement.FindFirst(
                TreeScope.Subtree,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (found is not null) return found;
            Thread.Sleep(100);
        }
        return null;
    }

    private static bool HasAutomationIdText(AutomationElement root, string id, string text)
        => GetAutomationName(root, id).Contains(text, StringComparison.Ordinal);

    private static string GetAutomationName(AutomationElement root, string id)
    {
        try
        {
            var element = FindById(root, id);
            return element?.Current.Name ?? "(not found)";
        }
        catch (ElementNotAvailableException) { return "(unavailable)"; }
    }

    private static IReadOnlyList<AutomationElement> FindAllById(AutomationElement root, string id)
    {
        var found = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        var result = new AutomationElement[found.Count];
        for (var i = 0; i < found.Count; i++)
            result[i] = found[i];
        return result;
    }

    private static string FindFixtureWorkspace()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "CSharpIde");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var fallback = Path.Combine(repositoryRoot, "tests", "Fixtures", "CSharpIde");
        Assert.True(Directory.Exists(fallback), $"C# fixture was not found: {fallback}");
        return fallback;
    }

    private sealed class RealWpfFactAttribute : FactAttribute
    {
        public RealWpfFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("LOOMO_RUN_REAL_WPF"), "1",
                    StringComparison.Ordinal))
                Skip = "LOOMO_RUN_REAL_WPF=1 のときだけ実WPFプロセスを起動します。";
        }
    }

}
