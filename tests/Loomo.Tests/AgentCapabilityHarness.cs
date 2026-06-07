using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using sk0ya.Loomo.Core.Tools.Implementations;
using Xunit;
using Xunit.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 実モデル（phi-4-mini ONNX）＋実ツール（run_powershell/write_file/edit_file）で
/// AgentOrchestrator をヘッドレス駆動し、「何をどこまで任せられるか」を実測するハーネス。
/// GUI を介さず、ターミナルは実 pwsh、ワークスペースは一時フォルダに差し替える。
/// 既定では Skip（手動実行）。RUN_AGENT_HARNESS=1 で有効化。
/// </summary>
public sealed class AgentCapabilityHarness
{
    private readonly ITestOutputHelper _out;
    public AgentCapabilityHarness(ITestOutputHelper output) => _out = output;

    private static string ModelPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "models", "phi-4-mini-instruct-cpu-int4");

    [Fact]
    public async Task RunHarness()
    {
        if (Environment.GetEnvironmentVariable("RUN_AGENT_HARNESS") != "1")
            return; // 通常の dotnet test では走らせない（重い・モデル必須）

        Assert.True(Directory.Exists(ModelPath), $"model not found: {ModelPath}");

        // --- 一時ワークスペース。タスクごとに同じ初期状態へ再シードし、タスク間でファイル変更が
        //     波及して結果を汚染しないよう隔離する（read タスクの誤編集が次タスクへ漏れない）。 ---
        var ws = Path.Combine(Path.GetTempPath(), "loomo-harness-" + Guid.NewGuid().ToString("N")[..8]);
        void SeedWorkspace()
        {
            if (Directory.Exists(ws)) Directory.Delete(ws, recursive: true);
            Directory.CreateDirectory(ws);
            File.WriteAllText(Path.Combine(ws, "README.md"),
                "# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n");
            File.WriteAllText(Path.Combine(ws, "app.py"),
                "def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n");
            Directory.CreateDirectory(Path.Combine(ws, "src"));
            File.WriteAllText(Path.Combine(ws, "src", "util.txt"), "alpha\nbeta\ngamma\n");
        }
        SeedWorkspace();

        // --- 設定（実モデルを指す） ---
        var settings = new AiSettings();
        settings.Local.Model = "phi-4-mini-instruct-cpu-int4";
        settings.Local.ModelPath = ModelPath;
        settings.Local.MaxTokens = 1024;
        settings.Safety.AutoApprove = true;

        // --- サービス（ヘッドレス実装） ---
        var workspace = new HeadlessWorkspace(ws);
        var terminal = new HeadlessTerminal(ws);
        var editor = new HeadlessEditor();
        using var engine = new Phi4Engine();

        var factory = new AiClientFactory(engine, settings, workspace);
        var tools = new ToolRegistry(new IAgentTool[]
        {
            new PwshTool(terminal),
            new WriteFileTool(workspace, editor),
            new EditFileTool(workspace, editor),
        });
        var safety = new SafetyPolicy(settings.Safety);
        var approval = new AutoApproval();
        var context = new SettingsContextWindowPolicy(settings);
        var orch = new AgentOrchestrator(factory, tools, approval, safety, context,
            NullLogger<AgentOrchestrator>.Instance);

        var tasks = new (string Name, string Prompt)[]
        {
            ("greet",        "こんにちは。あなたは何ができますか？"),
            ("list",         "このワークスペースにあるファイルを一覧して。"),
            ("read-version", "README.md に書かれているバージョンを教えて。"),
            ("search-bug",   "app.py に含まれるバグを指摘して。"),
            ("create-file",  "notes/hello.txt というファイルを作って、中身に「Loomoのテスト」と書いて。"),
            ("edit-file",    "README.md のバージョンを 1.2.3 から 2.0.0 に書き換えて。"),
            ("multi-step",   "src/util.txt の2行目を読み取って、その内容を大文字にして書き戻して。"),
        };

        var report = new StringBuilder();
        report.AppendLine("# Loomo エージェント能力ハーネス結果");
        report.AppendLine($"- 実行日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"- モデル: {settings.Local.Model}");
        report.AppendLine($"- ワークスペース: {ws}");
        report.AppendLine();

        foreach (var (name, prompt) in tasks)
        {
            SeedWorkspace();   // 各タスクを同一初期状態から開始
            var convo = new Conversation();
            var rec = new TurnRecord(name, prompt);
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await foreach (var ev in orch.RunTurnAsync(convo, prompt, name, cts.Token))
                    rec.Observe(ev);
            }
            catch (Exception ex)
            {
                rec.Error = "EXCEPTION: " + ex.Message;
            }
            sw.Stop();
            rec.ElapsedMs = sw.ElapsedMilliseconds;
            rec.WriteTo(report);
            // 客観的な地上真実：タスク後のファイル状態（モデルの自己申告に依存しない検証）。
            report.AppendLine("- 実ファイル状態:");
            foreach (var rel in new[] { "README.md", "app.py", "src/util.txt", "notes/hello.txt" })
            {
                var full = Path.Combine(ws, rel.Replace('/', Path.DirectorySeparatorChar));
                var state = File.Exists(full) ? File.ReadAllText(full).Replace("\n", "\\n").Replace("\r", "") : "(なし)";
                report.AppendLine($"    - {rel}: `{state}`");
            }
            report.AppendLine();
            _out.WriteLine($"[{name}] done in {sw.ElapsedMilliseconds}ms, iters={rec.Iterations}, tools={rec.ToolCalls.Count}");
        }

        var outPath = Path.Combine(AppContext.BaseDirectory, "harness-report.md");
        File.WriteAllText(outPath, report.ToString());
        _out.WriteLine("REPORT: " + outPath);
        // リポジトリ直下にもコピー（読みやすいよう）
        try { File.WriteAllText(@"C:\Projects\Loomo\harness-report.md", report.ToString()); } catch { }
    }

    private sealed class TurnRecord
    {
        public TurnRecord(string name, string prompt) { Name = name; Prompt = prompt; }
        public string Name { get; }
        public string Prompt { get; }
        public int Iterations { get; private set; }
        public List<string> ToolCalls { get; } = new();
        public List<string> ToolResults { get; } = new();
        public List<string> ParseFailures { get; } = new();
        public StringBuilder FinalText { get; } = new();
        public string? Error { get; set; }
        public long ElapsedMs { get; set; }
        private double _prefillMs, _decodeMs, _loadMs;

        public void Observe(AgentEvent ev)
        {
            switch (ev)
            {
                case TextDelta td: FinalText.Append(td.Text); break;
                case TurnCompleted tc: if (!string.IsNullOrEmpty(tc.FinalText)) { FinalText.Clear(); FinalText.Append(tc.FinalText); } break;
                case ToolUseRequested r:
                    Iterations++;
                    ToolCalls.Add($"{r.ToolUse.Name} {r.ToolUse.ArgumentsJson}");
                    break;
                case ToolExecutionCompleted c:
                    var content = c.Result.Content ?? "";
                    ToolResults.Add($"[{(c.Result.IsError ? "ERR" : "ok")}] {Truncate(content, 240)}");
                    break;
                case ToolCallParseFailed pf: ParseFailures.Add(Truncate(pf.RawText, 200)); break;
                case AiUsageReported u:
                    _prefillMs += u.PromptEvalMs ?? 0; _decodeMs += u.EvalMs ?? 0; _loadMs += u.LoadMs ?? 0;
                    break;
                case AgentError e: Error = e.Message; break;
            }
        }

        public void WriteTo(StringBuilder sb)
        {
            sb.AppendLine($"## [{Name}] {Prompt}");
            sb.AppendLine($"- 所要: {ElapsedMs}ms（load {_loadMs:F0} / prefill {_prefillMs:F0} / decode {_decodeMs:F0}）");
            sb.AppendLine($"- AI呼び出し回数(=ツール反復): {Iterations}");
            if (ToolCalls.Count > 0)
            {
                sb.AppendLine("- ツール呼び出し:");
                foreach (var t in ToolCalls) sb.AppendLine($"    - `{Truncate(t, 200)}`");
            }
            if (ToolResults.Count > 0)
            {
                sb.AppendLine("- ツール結果:");
                foreach (var t in ToolResults) sb.AppendLine($"    - {t.Replace("\n", " ")}");
            }
            if (ParseFailures.Count > 0)
            {
                sb.AppendLine("- ⚠ ツール呼び出しJSON解釈失敗:");
                foreach (var p in ParseFailures) sb.AppendLine($"    - `{p.Replace("\n", " ")}`");
            }
            if (Error != null) sb.AppendLine($"- ❌ エラー: {Error.Replace("\n", " ")}");
            sb.AppendLine($"- 最終回答: {Truncate(FinalText.ToString().Trim(), 600).Replace("\n", " ")}");
            sb.AppendLine();
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    }

    // ---- ヘッドレスサービス実装 ----

    private sealed class HeadlessWorkspace : IWorkspaceService
    {
        private readonly string _root;
        public HeadlessWorkspace(string root) => _root = Path.GetFullPath(root);
        public string? RootPath => _root;
        public string? SelectedPath { get; set; }
        public void OpenFolder(string rootPath) { }
        public Task<IReadOnlyList<FileNode>> ListAsync(string path)
        {
            var dir = ResolvePath(path);
            IReadOnlyList<FileNode> nodes = Directory.Exists(dir)
                ? Directory.GetFileSystemEntries(dir)
                    .Select(p => new FileNode(Path.GetFileName(p), p, Directory.Exists(p))).ToList()
                : new List<FileNode>();
            return Task.FromResult(nodes);
        }
        public Task<string> ReadFileAsync(string path) => File.ReadAllTextAsync(ResolvePath(path));
        public string ResolvePath(string path)
        {
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
            if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"ワークスペース外へのアクセスは許可されていません: {path}");
            return full;
        }
        public event EventHandler<string?>? SelectionChanged;
        public event EventHandler<string?>? RootChanged;
    }

    private sealed class HeadlessTerminal : ITerminalService
    {
        private string _cwd;
        public HeadlessTerminal(string cwd) => _cwd = cwd;
        public string CurrentDirectory => _cwd;
        public bool IsExecuting { get; private set; }
        public void SetWorkingDirectory(string path) => _cwd = path;
        public event EventHandler<CommandResult>? CommandExecuted;

        public async Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
        {
            IsExecuting = true;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _cwd,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);

                using var proc = Process.Start(psi)!;
                var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
                var stderr = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                var output = stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n[stderr]\n" + stderr);
                var result = new CommandResult(command, output.TrimEnd(), proc.ExitCode, _cwd, proc.ExitCode == 0);
                CommandExecuted?.Invoke(this, result);
                return result;
            }
            finally { IsExecuting = false; }
        }
    }

    private sealed class HeadlessEditor : IEditorService
    {
        public string? ActiveFilePath { get; private set; }
        public Task OpenFileAsync(string path) { ActiveFilePath = path; return Task.CompletedTask; }
        public Task<string> GetActiveContentAsync() => Task.FromResult("");
        public Task<string> GetSelectedTextAsync() => Task.FromResult("");
        public Task<string> ShowDiffAsync(string path, string proposedContent) => Task.FromResult("");
        public Task<bool> ApplyEditAsync(string path, string newContent) => Task.FromResult(true);
        public Task OpenDocumentAsync(EditorDocument document) => Task.CompletedTask;
    }

    private sealed class AutoApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string summary, CancellationToken ct) => Task.FromResult(true);
    }
}
