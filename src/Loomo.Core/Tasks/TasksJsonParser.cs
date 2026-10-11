using System.Text.Json;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Tasks;

/// <summary>
/// <c>.vscode/tasks.json</c> を読む純粋関数（設計書 §31.17）。Loomo 独自の書式は作らず、既存のリポジトリに
/// 置いてある tasks.json をそのまま使えることを優先する。読むのは実用上ほぼ全部を占める部分集合：
/// <c>label</c>・<c>type</c>（shell / process / npm）・<c>command</c>・<c>args</c>・<c>options.cwd</c>・
/// <c>group</c>（文字列 / <c>{kind,isDefault}</c>）・<c>isBackground</c>・<c>problemMatcher</c>
/// （組み込み名 / インライン定義 / <c>base</c> 付きの上書き / その配列）・<c>dependsOn</c>・<c>detail</c>、
/// それに Windows 上書き（<c>windows: {command,args,options}</c>）と最上位の <c>options.cwd</c>・<c>inputs</c>
/// （promptString / pickString。§31.17.6）。
///
/// <para>tasks.json は JSONC（コメント・末尾カンマ可）なので、その2つを許して読む。
/// 読めない項目・未対応の指定は捨てて <see cref="WorkspaceTasksFile.Warnings"/> に理由を積む。</para>
/// </summary>
public static class TasksJsonParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static WorkspaceTasksFile Parse(string json, string workspaceFolder)
    {
        var warnings = new List<string>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            return new WorkspaceTasksFile([], [$"tasks.json を読めません: {ex.Message}"]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new WorkspaceTasksFile([], ["tasks.json の最上位がオブジェクトではありません。"]);

            var globalCwd = ReadCwd(root);
            if (TryGetObject(root, "windows", out var rootWindows) && ReadCwd(rootWindows) is { } windowsCwd)
                globalCwd = windowsCwd;

            if (!root.TryGetProperty("tasks", out var tasksElement) || tasksElement.ValueKind != JsonValueKind.Array)
                return new WorkspaceTasksFile([], warnings);
            var inputs = ReadInputs(root, warnings);

            var tasks = new List<WorkspaceTask>();
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var element in tasksElement.EnumerateArray())
            {
                index++;
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (ReadTask(element, workspaceFolder, globalCwd, index, warnings) is not { } read) continue;
                var task = read with { Inputs = inputs };
                if (!labels.Add(task.Label))
                {
                    warnings.Add($"ラベル「{task.Label}」が重複しています（後の定義は無視します）。");
                    continue;
                }
                tasks.Add(task);
            }
            return new WorkspaceTasksFile(tasks, warnings);
        }
    }

    private static WorkspaceTask? ReadTask(
        JsonElement element, string workspaceFolder, string? globalCwd, int index, List<string> warnings)
    {
        var type = GetString(element, "type")?.Trim().ToLowerInvariant() ?? "shell";
        var label = GetString(element, "label") ?? GetString(element, "taskName");
        string? command = GetString(element, "command");
        var args = ReadArgs(element);
        var cwd = ReadCwd(element) ?? globalCwd;

        // Windows 上書き（このアプリは Windows 専用なので常に適用する）。
        if (TryGetObject(element, "windows", out var windows))
        {
            command = GetString(windows, "command") ?? command;
            if (windows.TryGetProperty("args", out _)) args = ReadArgs(windows);
            cwd = ReadCwd(windows) ?? cwd;
        }

        WorkspaceTaskKind kind;
        switch (type)
        {
            case "shell":
                kind = WorkspaceTaskKind.Shell;
                break;
            case "process":
                kind = WorkspaceTaskKind.Process;
                break;
            case "npm":
                // npm 自動検出タスクの手書き形（{"type":"npm","script":"build","path":"web/"}）。
                // PowerShell では npm が npm.ps1 に解決されて `--` を食うので npm.cmd を明示する（TS IDE と同じ）。
                var script = GetString(element, "script");
                if (string.IsNullOrWhiteSpace(script))
                {
                    warnings.Add($"{Describe(label, index)}: npm タスクに script がありません。");
                    return null;
                }
                kind = WorkspaceTaskKind.Shell;
                command = "npm.cmd run " + QuoteIfNeeded(script);
                args = [];
                if (GetString(element, "path") is { Length: > 0 } npmPath)
                    cwd ??= "${workspaceFolder}/" + npmPath.TrimEnd('/', '\\');
                label ??= $"npm: {script}";
                break;
            default:
                warnings.Add($"{Describe(label, index)}: type \"{type}\" は未対応です（shell / process / npm）。");
                return null;
        }

        var dependsOn = ReadDependsOn(element);
        if (string.IsNullOrWhiteSpace(label))
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                warnings.Add($"{index} 番目のタスクに label も command もありません。");
                return null;
            }
            label = command;   // VS Code と同じく、ラベルが無ければコマンドで呼ぶ
        }
        if (string.IsNullOrWhiteSpace(command) && dependsOn.Count == 0)
        {
            warnings.Add($"「{label}」: command も dependsOn もありません。");
            return null;
        }

        if (element.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object &&
            options.TryGetProperty("env", out _))
            warnings.Add($"「{label}」: options.env は未対応です（可視ターミナルのシェルへ環境変数を残してしまうため）。");

        var (group, isDefault) = ReadGroup(element);
        var matchers = element.TryGetProperty("problemMatcher", out var matcherElement)
            ? ReadMatchers(matcherElement, label!, warnings)
            : [];

        return new WorkspaceTask
        {
            Label = label!,
            WorkspaceFolder = workspaceFolder,
            Kind = kind,
            Command = string.IsNullOrWhiteSpace(command) ? null : command,
            Args = args,
            Cwd = cwd,
            Group = group,
            IsDefault = isDefault,
            IsBackground = element.TryGetProperty("isBackground", out var bg) && bg.ValueKind == JsonValueKind.True,
            Matchers = matchers,
            DependsOn = dependsOn,
            Detail = GetString(element, "detail"),
        };
    }

    /// <summary>最上位の <c>inputs</c>。id の無い項目・未知の type・選択肢の無い pickString は捨てて警告する。
    /// <c>command</c> 型は読んでおき、参照したタスクを実行するときに理由を添えて断る（「見つからない」より直しやすい）。</summary>
    private static IReadOnlyDictionary<string, TaskInput> ReadInputs(JsonElement root, List<string> warnings)
    {
        var inputs = new Dictionary<string, TaskInput>(StringComparer.Ordinal);
        if (!root.TryGetProperty("inputs", out var array) || array.ValueKind != JsonValueKind.Array) return inputs;
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(element, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                warnings.Add("inputs に id の無い項目があります（無視します）。");
                continue;
            }
            var type = GetString(element, "type");
            TaskInputKind kind;
            switch (type)
            {
                case "promptString": kind = TaskInputKind.PromptString; break;
                case "pickString": kind = TaskInputKind.PickString; break;
                case "command": kind = TaskInputKind.Command; break;
                default:
                    warnings.Add($"入力「{id}」: type \"{type}\" は未対応です（promptString / pickString）。");
                    continue;
            }
            var options = kind == TaskInputKind.PickString ? ReadInputOptions(element) : [];
            if (kind == TaskInputKind.PickString && options.Count == 0)
            {
                warnings.Add($"入力「{id}」: pickString に options がありません。");
                continue;
            }
            if (!inputs.TryAdd(id, new TaskInput
                {
                    Id = id,
                    Kind = kind,
                    Description = GetString(element, "description"),
                    Default = GetString(element, "default"),
                    Options = options,
                    Password = element.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.True,
                }))
                warnings.Add($"入力「{id}」が重複しています（後の定義は無視します）。");
        }
        return inputs;
    }

    /// <summary>pickString の <c>options</c>：文字列、または <c>{label, value}</c>（label が無ければ value を出す）。</summary>
    private static IReadOnlyList<TaskInputOption> ReadInputOptions(JsonElement element)
    {
        if (!element.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) return [];
        var list = new List<TaskInputOption>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind == JsonValueKind.String) list.Add(new TaskInputOption(option.GetString()!, option.GetString()!));
            else if (option.ValueKind == JsonValueKind.Object && GetString(option, "value") is { } value)
                list.Add(new TaskInputOption(GetString(option, "label") is { Length: > 0 } label ? label : value, value));
        }
        return list;
    }

    private static string Describe(string? label, int index)
        => string.IsNullOrWhiteSpace(label) ? $"{index} 番目のタスク" : $"「{label}」";

    private static string? ReadCwd(JsonElement element)
        => TryGetObject(element, "options", out var options) ? GetString(options, "cwd") : null;

    private static IReadOnlyList<string> ReadArgs(JsonElement element)
    {
        if (!element.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var arg in args.EnumerateArray())
        {
            // {"value": "...", "quoting": "..."} 形式は値だけ読む（引用は TaskCommandBuilder が決める）。
            if (arg.ValueKind == JsonValueKind.String) list.Add(arg.GetString()!);
            else if (arg.ValueKind == JsonValueKind.Object && GetString(arg, "value") is { } value) list.Add(value);
            else if (arg.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                list.Add(arg.GetRawText());
        }
        return list;
    }

    private static IReadOnlyList<string> ReadDependsOn(JsonElement element)
    {
        if (!element.TryGetProperty("dependsOn", out var depends)) return [];
        if (depends.ValueKind == JsonValueKind.String) return [depends.GetString()!];
        if (depends.ValueKind != JsonValueKind.Array) return [];
        return depends.EnumerateArray()
            .Where(d => d.ValueKind == JsonValueKind.String)
            .Select(d => d.GetString()!)
            .Where(d => d.Length > 0)
            .ToList();
    }

    private static (string? Group, bool IsDefault) ReadGroup(JsonElement element)
    {
        if (!element.TryGetProperty("group", out var group)) return (null, false);
        if (group.ValueKind == JsonValueKind.String) return (group.GetString(), false);
        if (group.ValueKind != JsonValueKind.Object) return (null, false);
        var kind = GetString(group, "kind");
        var isDefault = group.TryGetProperty("isDefault", out var d) &&
            // 新しい VS Code は isDefault にグロブ文字列（"**/*.ts" 等）も書ける。指定がある＝既定とみなす。
            (d.ValueKind == JsonValueKind.True || (d.ValueKind == JsonValueKind.String && d.GetString()!.Length > 0));
        return (kind, isDefault);
    }

    internal static IReadOnlyList<ProblemMatcher> ReadMatchers(JsonElement element, string label, List<string> warnings)
    {
        var list = new List<ProblemMatcher>();
        void Add(JsonElement item)
        {
            if (ReadMatcher(item, label, warnings) is { } matcher) list.Add(matcher);
        }

        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Add(item);
        else
            Add(element);
        return list;
    }

    private static ProblemMatcher? ReadMatcher(JsonElement element, string label, List<string> warnings)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var name = element.GetString()!;
            if (BuiltInProblemMatchers.Find(name) is { } builtIn) return builtIn;
            warnings.Add($"「{label}」: problem matcher \"{name}\" は未対応です（対応: {string.Join(" ", BuiltInProblemMatchers.Names)}）。");
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object) return null;

        ProblemMatcher? baseMatcher = null;
        if (GetString(element, "base") is { } baseName)
        {
            baseMatcher = BuiltInProblemMatchers.Find(baseName);
            if (baseMatcher is null)
            {
                warnings.Add($"「{label}」: problem matcher の base \"{baseName}\" は未対応です。");
                return null;
            }
        }

        IReadOnlyList<ProblemPattern>? patterns = baseMatcher?.Patterns;
        if (element.TryGetProperty("pattern", out var patternElement))
        {
            patterns = ReadPatterns(patternElement, label, warnings);
            if (patterns is null) return null;
        }
        if (patterns is null || patterns.Count == 0)
        {
            warnings.Add($"「{label}」: problem matcher に pattern がありません。");
            return null;
        }

        var matcher = baseMatcher is null
            ? new ProblemMatcher(GetString(element, "owner") ?? "task", patterns)
            : baseMatcher with { Patterns = patterns };
        if (GetString(element, "owner") is { } owner) matcher = matcher with { Owner = owner };
        if (GetString(element, "severity") is { } severity &&
            ProblemMatcherEngine.ParseSeverity(severity) is { } parsed)
            matcher = matcher with { DefaultSeverity = parsed };
        if (element.TryGetProperty("fileLocation", out var location))
            matcher = ApplyFileLocation(matcher, location);
        if (TryGetObject(element, "background", out var background))
        {
            if (ReadBackground(background, label, warnings) is not { } bg) return null;
            matcher = matcher with { Background = bg };
        }
        return matcher;
    }

    /// <summary><c>background: { activeOnStart, beginsPattern, endsPattern }</c>。パターンは文字列か
    /// <c>{ "regexp": … }</c>。読めない正規表現はこの matcher ごと捨てる（区切れない watch で問題が積み上がり続けるより良い）。</summary>
    private static ProblemMatcherBackground? ReadBackground(JsonElement element, string label, List<string> warnings)
    {
        Regex? Read(string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()
                : value.ValueKind == JsonValueKind.Object ? GetString(value, "regexp") : null;
            if (string.IsNullOrEmpty(text)) return null;
            try { return new Regex(text, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex)
            {
                warnings.Add($"「{label}」: background.{name} を読めません: {ex.Message}");
                throw new FormatException();
            }
        }

        try
        {
            var begins = Read("beginsPattern");
            var ends = Read("endsPattern");
            var activeOnStart = element.TryGetProperty("activeOnStart", out var active) && active.ValueKind == JsonValueKind.True;
            return new ProblemMatcherBackground(begins, ends, activeOnStart);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ProblemMatcher ApplyFileLocation(ProblemMatcher matcher, JsonElement element)
    {
        string? mode = null;
        string? baseDir = null;
        if (element.ValueKind == JsonValueKind.String) mode = element.GetString();
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToList();
            if (items.Count > 0) mode = items[0];
            if (items.Count > 1) baseDir = items[1];
        }

        var kind = mode?.ToLowerInvariant() switch
        {
            "absolute" => ProblemFileLocation.Absolute,
            "relative" => ProblemFileLocation.Relative,
            // "search"（ファイルを探索する）は自動判定で代用する。
            "autodetect" or "search" => ProblemFileLocation.AutoDetect,
            _ => matcher.FileLocation,
        };
        return matcher with
        {
            FileLocation = kind,
            FileLocationBase = baseDir ?? (kind == matcher.FileLocation ? matcher.FileLocationBase : "${workspaceFolder}"),
        };
    }

    private static IReadOnlyList<ProblemPattern>? ReadPatterns(JsonElement element, string label, List<string> warnings)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            // 名前付きパターン（"$tsc" 等）は組み込み matcher の行パターンとして読む。
            if (BuiltInProblemMatchers.Find(element.GetString()!) is { } builtIn) return builtIn.Patterns;
            warnings.Add($"「{label}」: pattern \"{element.GetString()}\" は未対応です。");
            return null;
        }

        var raw = element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList()
            : element.ValueKind == JsonValueKind.Object ? [element] : [];
        if (raw.Count == 0) return null;

        var patterns = new List<ProblemPattern>();
        for (var i = 0; i < raw.Count; i++)
        {
            var p = raw[i];
            var regexText = GetString(p, "regexp");
            if (string.IsNullOrEmpty(regexText))
            {
                warnings.Add($"「{label}」: pattern に regexp がありません。");
                return null;
            }
            Regex regex;
            try
            {
                regex = new Regex(regexText, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                warnings.Add($"「{label}」: regexp を読めません: {ex.Message}");
                return null;
            }
            patterns.Add(new ProblemPattern(regex)
            {
                File = GetInt(p, "file") ?? 0,
                Location = GetInt(p, "location") ?? 0,
                Line = GetInt(p, "line") ?? 0,
                Column = GetInt(p, "column") ?? 0,
                EndLine = GetInt(p, "endLine") ?? 0,
                EndColumn = GetInt(p, "endColumn") ?? 0,
                Severity = GetInt(p, "severity") ?? 0,
                Code = GetInt(p, "code") ?? 0,
                Message = GetInt(p, "message"),
                Loop = i == raw.Count - 1 && p.TryGetProperty("loop", out var loop) && loop.ValueKind == JsonValueKind.True,
            });
        }

        if (patterns.Count == 1)
        {
            // 1行パターンの省略時既定は VS Code と同じ：file=1、位置は location=2、
            // message は location 指定なら 4、line/column 指定なら 5。
            var p = patterns[0];
            var hasLocation = p.Location > 0;
            var hasLine = p.Line > 0;
            if (!hasLocation && !hasLine) p = p with { Location = 2 };
            if (p.File == 0) p = p with { File = 1 };
            if (p.Message is null) p = p with { Message = p.Location > 0 ? 4 : 5 };
            patterns[0] = p;
        }
        return patterns;
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
        => element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number) ? number : null;

    private static string QuoteIfNeeded(string value) => TaskCommandBuilder.QuoteArgument(value);
}
