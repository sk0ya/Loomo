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

    /// <summary>計測対象モデルのフォルダ名。環境変数 HARNESS_MODEL で切り替え可（既定は phi4-mini）。
    /// 例: <c>$env:HARNESS_MODEL='qwen3-4b-cpu-int4'</c> で Qwen3-4B を測る。</summary>
    private static string ModelFolderName =>
        Environment.GetEnvironmentVariable("HARNESS_MODEL") is { Length: > 0 } m
            ? m : "phi-4-mini-instruct-cpu-int4";

    private static string ModelPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "models", ModelFolderName);

    /// <summary>各タスクの初期状態（タスクごとにこの内容へ再シードして隔離する）。
    /// オラクルが「不変であるべきファイル」を照合する基準にもなる。キーは "/" 区切りの相対パス。</summary>
    private static readonly Dictionary<string, string> SeedFiles = new()
    {
        ["README.md"]    = "# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n",
        ["app.py"]       = "def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n",
        ["src/util.txt"] = "alpha\nbeta\ngamma\n",
        ["config.json"]  = "{\n  \"name\": \"loomo\",\n  \"version\": \"1.2.3\"\n}\n",
        ["numbers.txt"]  = "3\n7\n5\n",
    };

    private sealed record HarnessTask(string Name, string Prompt, Func<TurnRecord, (bool ok, string detail)> Oracle);

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
            foreach (var (rel, content) in SeedFiles)
            {
                var full = Path.Combine(ws, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
        }
        SeedWorkspace();

        // --- 設定（実モデルを指す） ---
        var settings = new AiSettings();
        settings.Local.Model = ModelFolderName;
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

        // --- 地上真実オラクル。モデルの自己申告ではなく実ファイル/最終回答で機械判定する。
        //     ケース＋オラクルを足す→走らせる→PASS率で前後比較→プロンプト/ループを直す、という
        //     「能力拡張の回し車」を成立させる肝。 ---
        string Full(string rel) => Path.Combine(ws, rel.Replace('/', Path.DirectorySeparatorChar));
        string? ReadOrNull(string rel) { var f = Full(rel); return File.Exists(f) ? File.ReadAllText(f) : null; }
        static string Norm(string? s) => (s ?? "").Replace("\r", "");
        static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
        // 指定相対パス群がシード初期状態のまま（＝誤って改変されていない）か。
        bool Unchanged(params string[] rels) =>
            rels.All(r => SeedFiles.TryGetValue(r, out var v) && Norm(ReadOrNull(r)) == Norm(v));
        static bool Has(string? s, string sub) => (s ?? "").Contains(sub, StringComparison.OrdinalIgnoreCase);

        var tasks = new HarnessTask[]
        {
            // ---- 既存ベースライン ----
            new("greet", "こんにちは。あなたは何ができますか？", rec =>
                (rec.ToolCalls.Count == 0 && Unchanged("README.md", "app.py", "src/util.txt") && rec.FinalText.Length > 0,
                 "ツール呼び出し無し・ファイル不変・回答あり")),

            new("list", "このワークスペースにあるファイルを一覧して。", rec =>
                (rec.ToolCalls.Any(t => t.StartsWith("run_powershell")) &&
                 (Has(rec.FinalText.ToString(), "app.py") || Has(rec.FinalText.ToString(), "README")) &&
                 Unchanged("README.md", "app.py", "src/util.txt"),
                 "一覧コマンド実行・既知ファイルに言及・ファイル不変")),

            new("read-version", "README.md に書かれているバージョンを教えて。", rec =>
                (Has(rec.FinalText.ToString(), "1.2.3") && Unchanged("README.md"),
                 "回答に 1.2.3・README 不変")),

            new("search-bug", "app.py に含まれるバグを指摘して。", rec =>
            {
                var t = rec.FinalText.ToString();
                var identified = Has(t, "a + b") || Has(t, "足し") || Has(t, "加算") ||
                                 Has(t, "引き") || Has(t, "減算") || Has(t, "+ b");
                var edited = !Unchanged("app.py");   // 「指摘して」なので本来は編集すべきでない
                return (identified, identified ? (edited ? "バグ特定（※依頼外の編集をした）" : "バグ特定・編集なし")
                                               : "バグを特定できず");
            }),

            new("create-file", "notes/hello.txt というファイルを作って、中身に「Loomoのテスト」と書いて。", rec =>
                (Has(ReadOrNull("notes/hello.txt"), "Loomo"),
                 "notes/hello.txt 作成・内容OK")),

            new("edit-file", "README.md のバージョンを 1.2.3 から 2.0.0 に書き換えて。", rec =>
            {
                var r = ReadOrNull("README.md");
                // バージョンだけを置換すること。他の本文（見出し）を壊した全文上書きは不合格にする。
                return (Has(r, "2.0.0") && !Has(r, "1.2.3") && Has(r, "Sample Project"),
                        "README が 2.0.0・1.2.3 残存なし・本文保持");
            }),

            new("multi-step", "src/util.txt の2行目を読み取って、その内容を大文字にして書き戻して。", rec =>
            {
                var u = Norm(ReadOrNull("src/util.txt"));
                return (u.Contains("alpha") && u.Contains("BETA") && u.Contains("gamma") && !u.Contains("beta"),
                        $"util.txt='{u.Replace("\n", "\\n")}'");
            }),

            // ---- 追加ケース：任せられる範囲をさらに探索 ----
            new("rename-file", "app.py を main.py にリネームして。", rec =>
                (ReadOrNull("app.py") == null && Has(ReadOrNull("main.py"), "def add"),
                 "app.py→main.py・内容保持")),

            new("append-file", "README.md の末尾に「Generated by Loomo」という1行を追記して。元の内容は残して。", rec =>
            {
                var r = ReadOrNull("README.md");
                return (Has(r, "Generated by Loomo") && Has(r, "Version: 1.2.3") && Has(r, "Sample Project"),
                        "追記あり・元内容保持");
            }),

            new("delete-line", "src/util.txt から beta の行を削除して。残りはそのまま。", rec =>
            {
                var u = ReadOrNull("src/util.txt");
                return (u != null && !Has(u, "beta") && Has(u, "alpha") && Has(u, "gamma"),
                        "beta 削除・alpha/gamma 保持");
            }),

            new("count-lines", "src/util.txt は何行ありますか？数だけ簡潔に。", rec =>
                (Has(rec.FinalText.ToString(), "3") && Unchanged("src/util.txt"),
                 "回答に 3・ファイル不変")),

            new("multi-file-bump", "バージョン番号 1.2.3 を 2.0.0 に、README.md と config.json の両方で書き換えて。", rec =>
            {
                var r = ReadOrNull("README.md"); var c = ReadOrNull("config.json");
                // 両ファイルでバージョンを置換しつつ、他の本文（README見出し・configのname）を保持すること。
                return (Has(r, "2.0.0") && Has(c, "2.0.0") && !Has(r, "1.2.3") && !Has(c, "1.2.3") &&
                        Has(r, "Sample Project") && Has(c, "loomo"),
                        "両ファイルが 2.0.0・本文保持");
            }),

            new("sum-numbers", "numbers.txt にある数値の合計を計算して、合計だけ教えて。", rec =>
                (Has(rec.FinalText.ToString(), "15") && Unchanged("numbers.txt"),
                 "合計 15・ファイル不変")),

            new("read-guard", "src/util.txt の中身をそのまま見せて。ファイルは絶対に編集しないで。", rec =>
                (Has(rec.FinalText.ToString(), "alpha") && Unchanged("src/util.txt"),
                 "内容提示・ファイル不変（誤編集ガード）")),

            new("create-nested", "docs/guide/intro.md を作って、先頭に「# Intro」と書いて。", rec =>
                (Has(ReadOrNull("docs/guide/intro.md"), "# Intro"),
                 "ネストディレクトリに作成")),
        };

        var header = new StringBuilder();
        header.AppendLine("# Loomo エージェント能力ハーネス結果");
        header.AppendLine($"- 実行日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        header.AppendLine($"- モデル: {settings.Local.Model}");
        header.AppendLine($"- ワークスペース: {ws}");
        header.AppendLine();

        var body = new StringBuilder();
        var verdicts = new List<(string Name, int Pass, int Trials, string Detail, long Ms, int Iters)>();

        // タスク後の全ファイル状態を相対パスで列挙（新規ファイルも自動で拾う）。モデルの自己申告に依存しない検証。
        void DumpFiles(StringBuilder sb)
        {
            sb.AppendLine("- 実ファイル状態:");
            foreach (var f in Directory.GetFiles(ws, "*", SearchOption.AllDirectories).OrderBy(p => p))
            {
                var rel = Path.GetRelativePath(ws, f).Replace(Path.DirectorySeparatorChar, '/');
                var state = Trunc(File.ReadAllText(f).Replace("\r", "").Replace("\n", "\\n"), 200);
                sb.AppendLine($"    - {rel}: `{state}`");
            }
        }

        // 同一タスクを複数回試行できる（HARNESS_REPEATS、既定1）。リトライ温度や常駐エンジンの状態持ち越しで
        // 難タスクの単発結果はぶれる（greedy でも run 間で再現しないことがある）ため、R>1 で成功率を測ると
        // 能力を安定して評価できる。R=1 のときは従来どおりの単発レポート。
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("HARNESS_REPEATS"), out var rp) && rp > 1 ? rp : 1;
        if (repeats > 1) header.AppendLine($"- 各タスク試行回数: {repeats}").AppendLine();

        foreach (var task in tasks)
        {
            var pass = 0;
            TurnRecord rec = null!;
            (bool ok, string detail) last = (false, "");
            for (var trial = 0; trial < repeats; trial++)
            {
                SeedWorkspace();   // 各試行を同一初期状態から開始
                var convo = new Conversation();
                rec = new TurnRecord(task.Name, task.Prompt);
                var sw = Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try
                {
                    await foreach (var ev in orch.RunTurnAsync(convo, task.Prompt, task.Name, cts.Token))
                        rec.Observe(ev);
                }
                catch (Exception ex) { rec.Error = "EXCEPTION: " + ex.Message; }
                sw.Stop();
                rec.ElapsedMs = sw.ElapsedMilliseconds;

                try { last = task.Oracle(rec); }
                catch (Exception ex) { last = (false, "oracle例外: " + ex.Message); }
                rec.Verdict = last;
                if (last.ok) pass++;
                _out.WriteLine($"[{task.Name}] {(repeats > 1 ? $"trial {trial + 1}/{repeats} " : "")}" +
                               $"{(last.ok ? "PASS" : "FAIL")} in {sw.ElapsedMilliseconds}ms, iters={rec.Iterations} — {last.detail}");
            }

            verdicts.Add((task.Name, pass, repeats, last.detail, rec.ElapsedMs, rec.Iterations));
            // 本文には最後の試行の詳細（ツール呼び出し列・最終回答・ファイル状態）を残す。
            rec.WriteTo(body);
            DumpFiles(body);
            body.AppendLine();
        }

        // 先頭にサマリ表。R>1 なら成功率と flaky（0<成功<試行）を 🟠 で可視化する。前後比較の一目把握用。
        var passed = verdicts.Count(x => x.Pass == x.Trials);
        var anyPass = verdicts.Count(x => x.Pass > 0);
        header.AppendLine(repeats > 1
            ? $"## サマリ: 全試行PASS {passed}/{verdicts.Count}（1回以上PASS {anyPass}/{verdicts.Count}）"
            : $"## サマリ: {passed}/{verdicts.Count} PASS");
        header.AppendLine();
        header.AppendLine("| タスク | 判定 | 成功率 | 所要 | 反復 | 詳細 |");
        header.AppendLine("|---|---|---:|---:|---:|---|");
        foreach (var x in verdicts)
        {
            var mark = x.Pass == x.Trials ? "✅" : x.Pass == 0 ? "❌" : "🟠";
            header.AppendLine($"| {x.Name} | {mark} | {x.Pass}/{x.Trials} | {x.Ms}ms | {x.Iters} | {x.Detail} |");
        }
        header.AppendLine();

        var full = header.ToString() + body.ToString();
        // モデルごとにファイルを分け、別モデルのレポートを上書きしないようにする
        // （既定 phi4-mini は従来名のまま、それ以外は -<model> サフィックス）。
        var suffix = ModelFolderName == "phi-4-mini-instruct-cpu-int4" ? "" : "-" + ModelFolderName;
        var fileName = $"harness-report{suffix}.md";
        var outPath = Path.Combine(AppContext.BaseDirectory, fileName);
        File.WriteAllText(outPath, full);
        _out.WriteLine($"REPORT: {outPath}  ({passed}/{verdicts.Count} PASS)");
        // リポジトリ直下にもコピー（読みやすいよう）
        try { File.WriteAllText(Path.Combine(@"C:\Projects\Loomo", fileName), full); } catch { }
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
        public (bool ok, string detail)? Verdict { get; set; }
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
            if (Verdict is { } vd) sb.AppendLine($"- 判定: {(vd.ok ? "✅ PASS" : "❌ FAIL")} — {vd.detail}");
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
