using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Tasks;

/// <summary>実行直前に組み立てた1タスク分のコマンド。</summary>
/// <param name="Task">元のタスク。</param>
/// <param name="Cwd">変数を展開して絶対化した作業フォルダー（matcher の <c>${cwd}</c>）。</param>
/// <param name="CommandLine">タスク自身のコマンド行（表示用）。<c>dependsOn</c> だけの複合タスクは null。</param>
/// <param name="TerminalLine">可視ターミナル（PowerShell）へ送る1行。作業フォルダーへの移動を含む。複合タスクは null。</param>
public sealed record TaskInvocation(WorkspaceTask Task, string Cwd, string? CommandLine, string? TerminalLine);

/// <summary>
/// タスクを可視ターミナル（PowerShell）へ送る1行にする純粋関数（設計書 §31.17）。
///
/// <para><b>作業フォルダーは <c>Set-Location</c> で移す。</b><c>Push-Location; try {…} finally { Pop-Location }</c>
/// で戻したくなるが、そうするとプロンプトが見る <c>$?</c> が Pop-Location の成功で上書きされ、OSC 133 の
/// 終了コードが常に 0 になる（ビルド失敗が「成功」に見える）。TS IDE の npm スクリプト実行と同じく、
/// 人間がその場で <c>cd</c> したのと同じ結果を受け入れる——ターミナルが今どこにいるかは画面に出ている。</para>
///
/// <para><b>変数</b>：<c>${workspaceFolder}</c>（<c>${workspaceRoot}</c>）・<c>${workspaceFolderBasename}</c>・
/// <c>${cwd}</c>・<c>${file}</c> 系・<c>${env:NAME}</c>・<c>${pathSeparator}</c>（<c>${/}</c>）・<c>${input:id}</c>
/// （実行前に人間が答えた値・§31.17.6。答えが無ければ断る）。<c>${command:…}</c> / <c>${config:…}</c> は
/// 拡張機能や VS Code の設定が要るので未対応——黙って空にすると別のコマンドが走るので、実行を断る
/// （<see cref="TaskConfigurationException"/>）。</para>
/// </summary>
public static class TaskCommandBuilder
{
    private static readonly Regex Variable = new(@"\$\{([^}]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static TaskInvocation Build(WorkspaceTask task, string? activeFile, TaskInputValues? inputs = null)
    {
        inputs ??= TaskInputValues.Empty;
        var cwd = ResolveCwd(task, activeFile, inputs);
        if (task.Command is null) return new TaskInvocation(task, cwd, null, null);

        var command = Expand(task.Command, task, activeFile, cwd, inputs);
        var args = task.Args.Select(a => Expand(a, task, activeFile, cwd, inputs)).ToList();
        var line = new StringBuilder();
        if (task.Kind == WorkspaceTaskKind.Process)
        {
            // シェルに解釈させない：実行ファイルも引数も全部引用して呼び出し演算子で起動する。
            line.Append("& ").Append(QuoteAlways(command));
            foreach (var arg in args) line.Append(' ').Append(QuoteAlways(arg));
        }
        else
        {
            // shell：command は書かれたとおりのコマンド行（"npm run build" 等）。args は必要な分だけ引用する。
            line.Append(command);
            foreach (var arg in args) line.Append(' ').Append(QuoteArgument(arg));
        }
        var commandLine = line.ToString();
        return new TaskInvocation(task, cwd, commandLine, $"Set-Location -LiteralPath {QuoteAlways(cwd)}; {commandLine}");
    }

    /// <summary><c>options.cwd</c> を展開して絶対化する。相対ならワークスペースフォルダー基準。</summary>
    public static string ResolveCwd(WorkspaceTask task, string? activeFile, TaskInputValues? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(task.Cwd)) return Path.GetFullPath(task.WorkspaceFolder);
        var expanded = Expand(task.Cwd, task, activeFile, task.WorkspaceFolder, inputs ?? TaskInputValues.Empty);
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(task.WorkspaceFolder, expanded));
    }

    internal static string Expand(string text, WorkspaceTask task, string? activeFile, string cwd, TaskInputValues inputs)
        => Variable.Replace(text, m => ResolveVariable(m.Groups[1].Value, task, activeFile, cwd, inputs));

    private static string ResolveVariable(string name, WorkspaceTask task, string? activeFile, string cwd, TaskInputValues inputs)
    {
        var folder = task.WorkspaceFolder.TrimEnd('\\', '/');
        switch (name)
        {
            case "workspaceFolder":
            case "workspaceRoot":
                return folder;
            case "workspaceFolderBasename":
                return Path.GetFileName(folder);
            case "cwd":
                return cwd;
            case "pathSeparator":
            case "/":
                return Path.DirectorySeparatorChar.ToString();
        }
        if (name.StartsWith("env:", StringComparison.Ordinal))
            return Environment.GetEnvironmentVariable(name[4..]) ?? "";
        if (name.StartsWith("input:", StringComparison.Ordinal))
        {
            if (task.Inputs.TryGetValue(name[6..], out var input) && inputs.TryGet(input, out var value)) return value;
            throw new TaskConfigurationException($"${{{name}}} の値がありません（タスクの一覧から実行すると、実行前に訊きます）。");
        }
        if (name.StartsWith("file", StringComparison.Ordinal) || name is "relativeFile" or "relativeFileDirname")
        {
            if (string.IsNullOrWhiteSpace(activeFile))
                throw new TaskConfigurationException($"${{{name}}} を使うタスクは、ファイルをエディタで開いてから実行してください。");
            var file = Path.GetFullPath(activeFile);
            return name switch
            {
                "file" => file,
                "fileBasename" => Path.GetFileName(file),
                "fileBasenameNoExtension" => Path.GetFileNameWithoutExtension(file),
                "fileExtname" => Path.GetExtension(file),
                "fileDirname" => Path.GetDirectoryName(file) ?? "",
                "fileDirnameBasename" => Path.GetFileName(Path.GetDirectoryName(file) ?? ""),
                "fileWorkspaceFolder" => folder,
                "relativeFile" => Path.GetRelativePath(folder, file),
                "relativeFileDirname" => Path.GetRelativePath(folder, Path.GetDirectoryName(file) ?? folder),
                _ => throw new TaskConfigurationException($"変数 ${{{name}}} は未対応です。"),
            };
        }
        throw new TaskConfigurationException($"変数 ${{{name}}} は未対応です（拡張機能・設定の変数は使えません）。");
    }

    /// <summary>PowerShell の単一引用（中の <c>'</c> は <c>''</c>）。展開もエスケープも起きない最も強い引用。</summary>
    public static string QuoteAlways(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>空白や PowerShell の特殊文字を含むときだけ引用する（<c>build</c> は裸、<c>My App</c> は引用）。</summary>
    public static string QuoteArgument(string value)
    {
        if (value.Length == 0) return "''";
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || "'\"`$;(){}|&<>@#,".Contains(ch))
                return QuoteAlways(value);
        }
        return value;
    }
}

/// <summary>tasks.json の内容のせいで実行できない（未対応の変数・dependsOn の循環など）。メッセージは利用者向け。</summary>
public sealed class TaskConfigurationException(string message) : Exception(message);

/// <summary><c>dependsOn</c> を辿って実行順を決める純粋関数（設計書 §31.17）。</summary>
public static class TaskPlanner
{
    /// <summary>依存を先に、指定タスクを最後に並べる（深さ優先の後順）。同じタスクは1回だけ。
    /// <c>dependsOrder: "parallel"</c> も順に流す——可視ターミナルは1本で、同時に2つ打ち込むと出力が混ざる。
    /// 参照はまず同じフォルダーのタスクから、無ければ他のフォルダーから探す（マルチルート）。</summary>
    public static IReadOnlyList<WorkspaceTask> Order(WorkspaceTask root, IReadOnlyList<WorkspaceTask> all)
    {
        var order = new List<WorkspaceTask>();
        var done = new HashSet<WorkspaceTask>(ReferenceEqualityComparer.Instance);
        var visiting = new List<WorkspaceTask>();

        void Visit(WorkspaceTask task)
        {
            if (done.Contains(task)) return;
            if (visiting.Any(t => ReferenceEquals(t, task)))
                throw new TaskConfigurationException(
                    "dependsOn が循環しています: " + string.Join(" → ", visiting.SkipWhile(t => !ReferenceEquals(t, task)).Append(task).Select(t => t.Label)));
            visiting.Add(task);
            foreach (var label in task.DependsOn)
            {
                var dependency = all.FirstOrDefault(t => t.Label == label &&
                        string.Equals(t.WorkspaceFolder, task.WorkspaceFolder, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(t => t.Label == label)
                    ?? throw new TaskConfigurationException($"「{task.Label}」の dependsOn「{label}」が見つかりません。");
                Visit(dependency);
            }
            visiting.RemoveAt(visiting.Count - 1);
            done.Add(task);
            order.Add(task);
        }

        Visit(root);
        return order;
    }
}
