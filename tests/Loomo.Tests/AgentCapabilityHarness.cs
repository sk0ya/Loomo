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
using sk0ya.Loomo.Services;
using Xunit;
using Xunit.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 実モデル（既定は Qwen3-4B GGUF Q4_K_M）＋実ツール（run_powershell/write_file/edit_file）で
/// AgentOrchestrator をヘッドレス駆動し、「何をどこまで任せられるか」を実測するハーネス。
/// GUI を介さず、ターミナルは実 TerminalService（独立 PowerShell プロセス実行）、
/// ワークスペースは一時フォルダに差し替える。
/// 既定では Skip（手動実行）。RUN_AGENT_HARNESS=1 で有効化。
/// </summary>
public sealed class AgentCapabilityHarness
{
    private readonly ITestOutputHelper _out;
    public AgentCapabilityHarness(ITestOutputHelper output) => _out = output;

    /// <summary>計測対象モデルのフォルダ名。環境変数 HARNESS_MODEL で切り替え可（既定は <see cref="AiSettings.DefaultLocalModel"/>）。
    /// ONNX はフォルダ名（例 <c>qwen3-4b-cpu-int4</c>）、llama.cpp も GGUF を収めたフォルダ名
    /// （例 <c>qwen3-4b-q4_k_m</c>）を渡す。バックエンドは <see cref="LocalInferenceRouter"/> がパスの拡張子で
    /// 振り分けるため、レポート名にはこのクリーンなトークンをそのまま使う。</summary>
    private static string ModelFolderName =>
        Environment.GetEnvironmentVariable("HARNESS_MODEL") is { Length: > 0 } m
            ? m : AiSettings.DefaultLocalModel;

    private static string ModelRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "models", ModelFolderName);

    /// <summary>ルータへ渡す実モデルパス。ONNX フォルダ（<c>genai_config.json</c> を含む）はフォルダパス、
    /// GGUF を収めたフォルダはその <c>.gguf</c> ファイルパスを返す（ルータが拡張子で llama.cpp へ振り分ける）。</summary>
    private static string ModelPath
    {
        get
        {
            var root = ModelRoot;
            if (Directory.Exists(root) && !File.Exists(Path.Combine(root, "genai_config.json")))
            {
                var gguf = Directory.EnumerateFiles(root, "*.gguf").OrderBy(p => p).FirstOrDefault();
                if (gguf is not null) return gguf;
            }
            return root;
        }
    }

    /// <summary>レポート・試行ログの出力先。リポジトリ直下を散らかさないよう docs/reports に集約する。</summary>
    private static string ReportDir
    {
        get
        {
            var dir = @"C:\Projects\Loomo\docs\reports";
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>各タスクの初期状態（タスクごとにこの内容へ再シードして隔離する）。
    /// オラクルが「不変であるべきファイル」を照合する基準にもなる。キーは "/" 区切りの相対パス。</summary>
    private static readonly Dictionary<string, string> SeedFiles = new()
    {
        ["README.md"]    = "# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n",
        ["app.py"]       = "def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n",
        ["src/util.txt"] = "alpha\nbeta\ngamma\n",
        ["config.json"]  = "{\n  \"name\": \"loomo\",\n  \"version\": \"1.2.3\"\n}\n",
        ["numbers.txt"]  = "3\n7\n5\n",
        ["todo.md"]      = "TODO: write docs\nTODO: add tests\nDone: setup project\n",
    };

    private sealed record HarnessTask(string Name, string Prompt, Func<TurnRecord, (bool ok, string detail)> Oracle);

    [Fact]
    public void Default_model_matches_app_default_when_harness_model_is_not_set()
    {
        var old = Environment.GetEnvironmentVariable("HARNESS_MODEL");
        try
        {
            Environment.SetEnvironmentVariable("HARNESS_MODEL", null);
            Assert.Equal(AiSettings.DefaultLocalModel, ModelFolderName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARNESS_MODEL", old);
        }
    }

    [Fact]
    public async Task RunHarness()
    {
        if (Environment.GetEnvironmentVariable("RUN_AGENT_HARNESS") != "1")
            return; // 通常の dotnet test では走らせない（重い・モデル必須）

        Assert.True(Directory.Exists(ModelPath) || File.Exists(ModelPath), $"model not found: {ModelPath}");

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
        // ターミナルは実 TerminalService をそのまま使う（可視ターミナル未 Attach なので UI には触れない）。
        // UTF-8 前置句・stdin 即EOF（rg がパス省略時に stdin を読んで永久に待つのを防ぐ）・
        // キャンセル時のプロセスツリー kill・2分安全網まで、実機と同一挙動で評価するため。
        // （旧 HeadlessTerminal は stdin を閉じず子も kill しなかったため、モデルの `rg gamma -r .` が
        //   stdin 待ちで 5 分タイムアウト→rg が漏れて testhost のパイプを握る、が find-text の実敗因だった。）
        var terminal = new TerminalService();
        terminal.SetWorkingDirectory(ws);
        var editor = new HeadlessEditor();
        // バックエンドはルータが modelPath で振り分ける（.gguf → llama.cpp / フォルダ → ONNX）。
        using var engine = new LocalInferenceRouter(new OnnxGenAiEngine(), new LlamaCppEngine());

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

        // --- 暖機（HARNESS_WARMUP=1 で有効・既定オフ）：実アプリの LocalLlmWarmupService と同じ経路で
        //     安定プレフィックス（system+tools、会話は空）を prefill しておく。再帰メモリ搭載モデル
        //     （Qwen3.5 等）は LlamaCppEngine がここで確定した安定プレフィックスをスナップショットし、
        //     以降の実ターンでプロンプト先頭が一致すれば復元して再利用する（それ以外のモデルも通常の
        //     KV プレフィックス再利用の恩恵を初回ターンから受けられる）。既定はオフ＝従来どおりの
        //     コールドスタート計測（他モデルとの既存比較を壊さないため opt-in）。
        if (Environment.GetEnvironmentVariable("HARNESS_WARMUP") == "1")
        {
            var modelProfile = ModelProfiles.Resolve(settings.Local.Model);
            var stablePrompt = ChatPrompt.Build(
                modelProfile.Format, settings, AgentProfiles.Root, workspace.RootPath, new Conversation(), tools.Definitions);
            var maxLength = ModelProfiles.EffectiveNumCtx(settings.Local.Model, settings.Local.NumCtx);
            using var warmupCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await engine.WarmableFor(settings.Local.ModelPath!).PrimeAsync(
                settings.Local.ModelPath!, stablePrompt, maxLength, modelProfile.Sampling, warmupCts.Token);
        }

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

            // ---- 汎化検証ケース：システムプロンプトの few-shot 例と語彙・ファイル名・操作が被らない群。
            //      「プロンプトが評価セットに過適合していないか」を測るために、例に無い操作
            //      （コピー・削除・最大値・件数・横断検索・全置換・JSONキー追加・意味的修正）を選ぶ。 ----
            new("fix-bug", "app.py のバグを修正して。", rec =>
            {
                // 空白の揺れ（a+b / a + b）を吸収して比較。コメント行は残っていても消えていてもよいが、
                // return が加算に直り、減算が消え、print 行が保持されていること。
                var a = Norm(ReadOrNull("app.py")).Replace(" ", "");
                return (a.Contains("returna+b") && !a.Contains("a-b") && a.Contains("print(add(2,3))"),
                        "return a+b へ修正・print 行保持");
            }),

            new("replace-word", "src/util.txt の gamma を delta に置き換えて。", rec =>
            {
                var u = Norm(ReadOrNull("src/util.txt"));
                return (u.Contains("delta") && !u.Contains("gamma") && u.Contains("alpha") && u.Contains("beta"),
                        "gamma→delta・他行保持");
            }),

            new("copy-file", "README.md を docs/readme-copy.md という名前でコピーして。", rec =>
                (Has(ReadOrNull("docs/readme-copy.md"), "Sample Project") && Unchanged("README.md"),
                 "コピー作成・元ファイル不変")),

            new("delete-file", "numbers.txt を削除して。", rec =>
                (ReadOrNull("numbers.txt") == null && Unchanged("README.md", "src/util.txt"),
                 "numbers.txt 削除・他ファイル不変")),

            new("max-number", "numbers.txt の中で一番大きい数値はどれ？数だけ答えて。", rec =>
                (Has(rec.FinalText.ToString(), "7") && Unchanged("numbers.txt"),
                 "回答に 7・ファイル不変")),

            new("count-files", "このワークスペースには .txt ファイルが何個ある？数だけ答えて。", rec =>
                // .txt は numbers.txt と src/util.txt の2つ（サブフォルダ込みで数える必要がある）。
                (Has(rec.FinalText.ToString(), "2") && Unchanged("numbers.txt", "src/util.txt"),
                 "回答に 2・ファイル不変")),

            new("find-text", "「gamma」という単語を含むファイルはどれ？ファイル名を教えて。", rec =>
            {
                // gamma を含むのは src/util.txt だけ。「全ファイルを列挙して util も含まれていた」偽陽性を
                // 通さないよう、含まれないファイルへの言及が無いことまで要求する（緩いオラクルは虚偽PASSを通す）。
                var t = rec.FinalText.ToString();
                var ok = Has(t, "util") && !Has(t, "config") && !Has(t, "numbers") && !Has(t, "README")
                         && !Has(t, "app.py") && !Has(t, "todo") && Unchanged("src/util.txt");
                return (ok, "util.txt のみ特定・ファイル不変");
            }),

            new("create-script", "scripts/run.ps1 を作って、内容は「dotnet build」と「dotnet test」の2行にして。", rec =>
            {
                var s = ReadOrNull("scripts/run.ps1");
                return (Has(s, "dotnet build") && Has(s, "dotnet test"),
                        "スクリプト作成・2コマンド入り");
            }),

            new("replace-all", "todo.md の TODO を全部 DONE に置き換えて。", rec =>
            {
                // edit_file は一意一致が必要なので、行ごとの2回編集か read→write_file 全文書き直しが正解経路。
                var t = Norm(ReadOrNull("todo.md"));
                return (t.Contains("DONE: write docs") && t.Contains("DONE: add tests")
                        && t.Contains("Done: setup project") && !t.Contains("TODO"),
                        "TODO 2箇所→DONE・Done 行保持");
            }),

            new("insert-json-key", "config.json に \"license\": \"MIT\" というエントリを追加して。他の内容はそのまま。", rec =>
            {
                // 文字列含有でなく JSON として厳格に判定（緩いオラクルは壊れた JSON の虚偽 PASS を通す）。
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(ReadOrNull("config.json") ?? "");
                    var root = doc.RootElement;
                    var ok = root.TryGetProperty("license", out var lic) && lic.GetString() == "MIT"
                          && root.TryGetProperty("name", out var name) && name.GetString() == "loomo"
                          && root.TryGetProperty("version", out var ver) && ver.GetString() == "1.2.3";
                    return (ok, "license=MIT 追加・既存キー保持・JSON妥当");
                }
                catch (Exception ex) { return (false, "JSON不正: " + ex.Message.Split('\n')[0]); }
            }),
        };

        // ===== ワークフロー単発ステップのスイート（HARNESS_SUITE=workflow）=====
        // ワークフローの本質は WorkflowStepLibrary の単発ステップ（要約・翻訳・整形…）で、これらは
        // 旧共有プロンプトの「日本語の文章のみ／Markdown禁止」と矛盾していた当の対象。実ライブラリの
        // ステップ文をそのまま使い（UI と同じく対象を末尾へ貼る）、出力の言語・形式をオラクルで判定する。
        // ファイルには触らないのでオラクルは最終回答（rec.FinalText）だけを見る。
        string Lib(string name) => WorkflowStepLibrary.Catalog.First(c => c.Name == name).Prompt;
        bool HasJp(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "[぀-ヿ一-龯]");
        int Bullets(string s) => s.Split('\n')
            .Count(l => System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), "^[-・*]\\s+"));
        int CharCount(string s, char c) => s.Count(ch => ch == c);

        const string longJa =
            "Loomo はローカルで動く AI エージェントです。ターミナル・エディタ・フォルダツリーを操作できます。" +
            "小型のモデルでも動くよう、ツールは4つに絞っています。会話履歴はコンテキスト窓に収まるよう自動で切り詰めます。" +
            "起動時にモデルを暖機して初回応答を速くしています。";
        const string techPara =
            "ONNX Runtime GenAI を使い、Phi-4-mini や Qwen3 のような小型モデルを CPU 上でローカル実行する。" +
            "ツール呼び出しはモデルが本文に JSON で書き、パーサが復元する。KV プレフィックスの再利用で prefill を節約する。";

        var workflowTasks = new HarnessTask[]
        {
            new("wf-translate-en", Lib("英語に翻訳") + "私たちは新しい機能を来週リリースする予定です。", rec =>
            {
                var t = rec.FinalText.ToString().Trim();
                var ok = System.Text.RegularExpressions.Regex.IsMatch(t, "[A-Za-z]") && !HasJp(t);
                return (ok, ok ? "英語で出力（日本語混入なし）" : "英語化できず: " + Trunc(t.Replace("\n", "\\n"), 80));
            }),

            // 連鎖版（{{prev}} に長めの日本語）：指示も対象も日本語＝素通しコピーに倒れやすい当の失敗ケース。
            new("wf-translate-prev", WorkflowPrompt.Resolve(Lib("前段を英訳"), new[] { longJa }), rec =>
            {
                var t = rec.FinalText.ToString().Trim();
                var ok = System.Text.RegularExpressions.Regex.IsMatch(t, "[A-Za-z]") && !HasJp(t);
                return (ok, ok ? "英語で出力（日本語混入なし）" : "英語化できず: " + Trunc(t.Replace("\n", "\\n"), 80));
            }),

            // 単独「英語に翻訳」（ステップ1）に Markdown 表を貼ったケース。構造化された対象だと
            // セルを訳さず表ごと素通しに倒れやすい当の失敗ケース（報告例）。
            new("wf-translate-table", Lib("英語に翻訳") +
                "| カテゴリ | 説明 |\n" +
                "|---------|------|\n" +
                "| Loomo | Windows専用の開発ワークスペースで、エディタ・ブラウザ・ターミナルを一室に織り込む。 |\n" +
                "| ステージモード | 1つのペインを全面に配置し、残りのペインを右端の「袖」で表示する。 |\n" +
                "| ビルド | .NET 9 SDKが必要で、`dotnet build`や`dotnet run`などのコマンドを実行します。|", rec =>
            {
                var t = rec.FinalText.ToString().Trim();
                var ok = System.Text.RegularExpressions.Regex.IsMatch(t, "[A-Za-z]") && !HasJp(t);
                return (ok, ok ? "英語で出力（日本語混入なし）" : "英語化できず: " + Trunc(t.Replace("\n", "\\n"), 80));
            }),

            new("wf-summary-3", Lib("3行に要約") + longJa, rec =>
            {
                var b = Bullets(rec.FinalText.ToString());
                return (b >= 2, $"箇条書き {b} 行（- 始まり）");
            }),

            new("wf-bullets", Lib("箇条書きに整理") + longJa, rec =>
            {
                var b = Bullets(rec.FinalText.ToString());
                return (b >= 2, $"箇条書き {b} 行（- 始まり）");
            }),

            new("wf-keywords", Lib("キーワード抽出") + techPara, rec =>
            {
                var t = rec.FinalText.ToString().Trim();
                var seps = CharCount(t, ',') + CharCount(t, '、');
                return (seps >= 3, $"カンマ区切り（区切り {seps} 個）");
            }),

            new("wf-comment-code", Lib("コメントを付ける") + "def add(a, b):\n    return a + b\n", rec =>
            {
                var t = rec.FinalText.ToString();
                var ok = (t.Contains('#') || t.Contains("//")) && t.Contains("add");
                return (ok, ok ? "コメント付きコードを出力" : "コード/コメントなし");
            }),

            new("wf-regex", Lib("正規表現を作る") + "メールアドレスにマッチさせたい", rec =>
            {
                var t = rec.FinalText.ToString();
                var hits = new[] { "@", "\\", "[", "+", ".", "*" }.Count(x => t.Contains(x));
                return (hits >= 2, $"正規表現らしさ {hits}/6");
            }),

            new("wf-table", WorkflowPrompt.Resolve(Lib("前段を表にする"),
                new[] { "りんご 120円\nバナナ 80円\nぶどう 300円" }), rec =>
            {
                var t = rec.FinalText.ToString();
                var ok = CharCount(t, '|') >= 3 && t.Contains("---");
                return (ok, ok ? "Markdown 表を出力" : "表になっていない: " + Trunc(t.Replace("\n", "\\n"), 80));
            }),

            new("wf-polite", Lib("丁寧に書き換え") + "明日までにこれ送って。", rec =>
            {
                var t = rec.FinalText.ToString();
                var ok = HasJp(t) && (t.Contains("ます") || t.Contains("ください") || t.Contains("です"));
                return (ok, ok ? "丁寧な日本語へ" : "丁寧体になっていない");
            }),
        };

        if ((Environment.GetEnvironmentVariable("HARNESS_SUITE") ?? "agent").ToLowerInvariant() == "workflow")
            tasks = workflowTasks;

        // 実行モード（HARNESS_PREAMBLE）：チャット／ワークフローそれぞれの追加プロンプト（turnPreamble）を
        // 載せて精度を測り分ける。none/未指定なら共有プロンプト単体（ベースライン）。
        var preambleMode = (Environment.GetEnvironmentVariable("HARNESS_PREAMBLE") ?? "none").ToLowerInvariant();
        string? turnPreamble = preambleMode switch
        {
            "chat" => AiSettings.ChatTurnPreamble,
            "workflow" => AiSettings.WorkflowTurnPreamble,
            _ => null,
        };

        // 構成タグ（HARNESS_REPORT_SUFFIX）：同一モデルでプロンプト等の構成違いを別レポートに分けて
        // 上書きを防ぐ（例: baseline / candidate）。ヘッダにも記録して後から取り違えないようにする。
        var reportTag = Environment.GetEnvironmentVariable("HARNESS_REPORT_SUFFIX");

        // タスク名フィルタ（HARNESS_TASKS、カンマ区切り）：testhost のネイティブクラッシュ（長時間推論で
        // 既知）後に、残ったタスクだけを再実行して結果を継ぎ足すための再開手段。
        if (Environment.GetEnvironmentVariable("HARNESS_TASKS") is { Length: > 0 } only)
        {
            var names = only.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            tasks = tasks.Where(t => names.Contains(t.Name)).ToArray();
        }

        var header = new StringBuilder();
        header.AppendLine("# Loomo エージェント能力ハーネス結果");
        header.AppendLine($"- 実行日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        header.AppendLine($"- モデル: {settings.Local.Model}");
        header.AppendLine($"- スイート: {(Environment.GetEnvironmentVariable("HARNESS_SUITE") ?? "agent")}");
        header.AppendLine($"- 追加プロンプト(モード): {preambleMode}");
        header.AppendLine($"- 暖機(HARNESS_WARMUP): {(Environment.GetEnvironmentVariable("HARNESS_WARMUP") == "1" ? "あり" : "なし（コールドスタート）")}");
        if (!string.IsNullOrEmpty(reportTag)) header.AppendLine($"- 構成タグ: {reportTag}");
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
                    await foreach (var ev in orch.RunTurnAsync(convo, task.Prompt, task.Name, cts.Token,
                                       turnPreamble: turnPreamble))
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
                // 試行ごとの判定を逐次追記する（ITestOutputHelper はクラッシュで失われるため別ファイルに永続化）。
                // testhost がネイティブクラッシュしても、ここまでの成功率（x/R）をこのログから復元できる。
                try
                {
                    File.AppendAllText(
                        Path.Combine(ReportDir,
                            $"harness-report-{ModelFolderName}{(string.IsNullOrEmpty(reportTag) ? "" : "-" + reportTag)}-trials.log"),
                        $"{task.Name}\ttrial {trial + 1}/{repeats}\t{(last.ok ? "PASS" : "FAIL")}\t{sw.ElapsedMilliseconds}ms\titers={rec.Iterations}\t{last.detail}{Environment.NewLine}");
                }
                catch { }
            }

            verdicts.Add((task.Name, pass, repeats, last.detail, rec.ElapsedMs, rec.Iterations));
            // 本文には最後の試行の詳細（ツール呼び出し列・最終回答・ファイル状態）を残す。
            rec.WriteTo(body);
            DumpFiles(body);
            body.AppendLine();
            // タスクごとに途中経過を書き出す。重いモデルでネイティブクラッシュしても完了分の結果が残るよう、
            // 最終レポート（ループ後にサマリ付きで1回書く）とは別に partial を逐次更新する。
            try { File.WriteAllText(Path.Combine(ReportDir, $"harness-report-{ModelFolderName}{(string.IsNullOrEmpty(reportTag) ? "" : "-" + reportTag)}-partial.md"), body.ToString()); } catch { }
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
        // モデルごとにファイルを分け、別モデルのレポートを上書きしないようにする。
        var suffix = "-" + ModelFolderName;
        if (!string.IsNullOrEmpty(reportTag)) suffix += "-" + reportTag;
        var fileName = $"harness-report{suffix}.md";
        var outPath = Path.Combine(AppContext.BaseDirectory, fileName);
        File.WriteAllText(outPath, full);
        _out.WriteLine($"REPORT: {outPath}  ({passed}/{verdicts.Count} PASS)");
        // docs/reports にもコピー（読みやすいよう）
        try { File.WriteAllText(Path.Combine(ReportDir, fileName), full); } catch { }
    }

    /// <summary>
    /// ワークフロー連鎖の実機再現。単発タスクではなく、各ステップの実出力を <c>{{prev}}</c> で次段へ
    /// 送る本物のチェーン（WorkflowViewModel.RunStepAsync と同じ turnPreamble・解決規則）を回し、
    /// 各段の「解決後の指示文」と「生出力」を全部レポートに出す。どの段で崩れるかを実データで特定する。
    /// 既定では Skip。RUN_WORKFLOW_CHAIN=1 で有効化。
    /// </summary>
    [Fact]
    public async Task RunWorkflowChain()
    {
        if (Environment.GetEnvironmentVariable("RUN_WORKFLOW_CHAIN") != "1")
            return; // 重い・モデル必須

        Assert.True(Directory.Exists(ModelPath) || File.Exists(ModelPath), $"model not found: {ModelPath}");

        var ws = Path.Combine(Path.GetTempPath(), "loomo-wfchain-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(ws);

        var settings = new AiSettings();
        settings.Local.Model = ModelFolderName;
        settings.Local.ModelPath = ModelPath;
        settings.Local.MaxTokens = 1024;
        settings.Safety.AutoApprove = true;

        var workspace = new HeadlessWorkspace(ws);
        var terminal = new TerminalService();
        terminal.SetWorkingDirectory(ws);
        var editor = new HeadlessEditor();
        using var engine = new LocalInferenceRouter(new OnnxGenAiEngine(), new LlamaCppEngine());
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

        // ユーザー報告の対象 README（ステップ1の要約対象）。
        const string readme = """
            # Loomo

            **エディタ・ブラウザ・ターミナルを一室に織り込む、Windows 専用の開発ワークスペース。**

            標準は **ステージモード** ── 1 つを全面の「舞台」に立て、残りは右端の「袖」でライブミニチュア表示する。

            ![ステージモード（標準）：エディタが舞台、ターミナル・ブラウザ・Git などが右の袖にミニチュア表示](docs/images/stage-mode.png)

            ### タイル ── 全ペインを 2D に自由配置・分割・リサイズ

            ![タイルレイアウト：エクスプローラ／エディタ／ブラウザ／ターミナルを並べて表示](docs/images/tile-layout.png)

            ## ビルド

            前提: [.NET 9 SDK](https://dotnet.microsoft.com/)（Windows）。

            ## もっと詳しく

            - 設計の正本: [`docs/設計書.md`](docs/設計書.md)（ステージ・袖・素材の流れは §21〜§24）
            - エージェントループの性能知見: [`docs/エージェントループ知見.md`](docs/エージェントループ知見.md)
            - 開発ガイド: [`CLAUDE.md`](CLAUDE.md)

            > 名前の由来 = **Loom**（織機＝複数の道具を一枚に織り上げる）× **Room**（作業空間）。
            """;

        string Lib(string name) => WorkflowStepLibrary.Catalog.First(c => c.Name == name).Prompt;

        var report = new StringBuilder();
        report.AppendLine($"# ワークフロー連鎖再現 — {ModelFolderName}");
        report.AppendLine($"- 実行日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        // 1ステップ実行→出力を返す（WorkflowViewModel.RunStepAsync と同じ turnPreamble）。
        async Task<string> Run(string label, string prompt)
        {
            var convo = new Conversation();
            var rec = new TurnRecord(label, prompt);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var sw = Stopwatch.StartNew();
            try
            {
                await foreach (var ev in orch.RunTurnAsync(convo, prompt, "wfchain-" + label, cts.Token,
                                   turnPreamble: AiSettings.WorkflowTurnPreamble))
                    rec.Observe(ev);
            }
            catch (Exception ex) { rec.Error = "EXCEPTION: " + ex.Message; }
            sw.Stop();
            var output = rec.FinalText.ToString();
            report.AppendLine($"## {label}（{sw.ElapsedMilliseconds}ms, iters={rec.Iterations}）");
            report.AppendLine("### 指示文（{{prev}} 解決後）");
            report.AppendLine("~~~");
            report.AppendLine(prompt);
            report.AppendLine("~~~");
            report.AppendLine("### 出力");
            report.AppendLine("~~~");
            report.AppendLine(output);
            report.AppendLine("~~~");
            if (rec.Error != null) report.AppendLine($"- エラー: {rec.Error}");
            report.AppendLine();
            _out.WriteLine($"[{label}] {sw.ElapsedMilliseconds}ms iters={rec.Iterations} outLen={output.Length}");
            return output;
        }

        // 報告された流れ: 要約 → 表。ここまでは共通の上流。
        var s1 = await Run("step1-要約", WorkflowPrompt.Resolve(Lib("3行に要約") + readme, Array.Empty<string>()));
        var s2 = await Run("step2-表", WorkflowPrompt.Resolve(Lib("前段を表にする"), new[] { s1 }));

        // ステップ3 = 英訳。同じ step2 の表に対し、旧（日本語）指示と新（英語＝現ライブラリ）指示を A/B。
        const string oldJaTranslate =
            "前のステップの出力を自然な英語に翻訳してください。訳文だけを出力してください。\n\n{{prev}}";
        await Run("step3-旧[日本語指示]", WorkflowPrompt.Resolve(oldJaTranslate, new[] { s1, s2 }));
        await Run("step3-新[英語指示]", WorkflowPrompt.Resolve(Lib("前段を英訳"), new[] { s1, s2 }));

        var outPath = Path.Combine(ReportDir, $"workflow-chain-{ModelFolderName}.md");
        File.WriteAllText(outPath, report.ToString());
        _out.WriteLine($"REPORT: {outPath}");
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
        private string? _selectedPath;
        public HeadlessWorkspace(string root) => _root = Path.GetFullPath(root);
        public string? RootPath => _root;
        public string? SelectedPath
        {
            get => _selectedPath;
            set
            {
                if (_selectedPath == value)
                    return;
                _selectedPath = value;
                SelectionChanged?.Invoke(this, value);
            }
        }
        public void OpenFolder(string rootPath) => RootChanged?.Invoke(this, RootPath);
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

    private sealed class HeadlessEditor : IEditorService
    {
        public string? ActiveFilePath { get; private set; }
        public Task OpenFileAsync(string path) { ActiveFilePath = path; return Task.CompletedTask; }
        public Task<string> GetActiveContentAsync() => Task.FromResult("");
        public Task<string> GetSelectedTextAsync() => Task.FromResult("");
        public Task OpenDocumentAsync(EditorDocument document) => Task.CompletedTask;
    }

    private sealed class AutoApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string summary, CancellationToken ct) => Task.FromResult(true);
    }
}
