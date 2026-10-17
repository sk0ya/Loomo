using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>Claude Code の IDE 連携で IDE 側が答えるツール群（§38）。名前・引数・戻りの形は Claude Code が
/// VS Code / JetBrains 拡張に求めるものに合わせる（公開仕様は無く、互換実装 claudecode.nvim の PROTOCOL.md を
/// 正本にした）。戻りは MCP の <c>{content:[{type:"text",text}]}</c> で、構造のある答えは JSON 文字列を text に入れる。
/// 通信（WebSocket・JSON-RPC）は <see cref="ClaudeIdeServer"/>、中身はここ——テストで通信抜きに叩けるように分けた。</summary>
public sealed class ClaudeIdeTools(IClaudeIdeHost host, Func<IReadOnlyList<string>> workspaceFolders)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingDiffs = new(StringComparer.Ordinal);
    private ClaudeIdeSelection? _latestSelection;

    /// <summary>最後に Claude へ知らせた選択（<c>getLatestSelection</c> の答え）。</summary>
    public void RememberSelection(ClaudeIdeSelection selection) => _latestSelection = selection;

    public static JsonArray ToolList() =>
    [
        Tool("openFile", "Open a file in the editor and optionally select a range of text",
            Props(("filePath", "string", "Path to the file to open"),
                ("preview", "boolean", "Whether to open the file in preview mode"),
                ("startText", "string", "Text pattern to find the start of the selection range"),
                ("endText", "string", "Text pattern to find the end of the selection range"),
                ("selectToEndOfLine", "boolean", "Extend the selection to the end of the line"),
                ("makeFrontmost", "boolean", "Whether to make the file the active editor tab")),
            "filePath"),
        Tool("openDiff", "Open a diff view comparing the current file with proposed contents, and wait for the user to accept or reject it",
            Props(("old_file_path", "string", "Path to the old file to compare"),
                ("new_file_path", "string", "Path to the new file to compare"),
                ("new_file_contents", "string", "Contents for the new file version"),
                ("tab_name", "string", "Name for the diff tab")),
            "old_file_path", "new_file_path", "new_file_contents", "tab_name"),
        Tool("getCurrentSelection", "Get the current text selection in the active editor", Props()),
        Tool("getLatestSelection", "Get the most recent text selection (even if not in the active editor)", Props()),
        Tool("getOpenEditors", "Get information about currently open editors", Props()),
        Tool("getWorkspaceFolders", "Get all workspace folders currently open in the IDE", Props()),
        Tool("getDiagnostics", "Get language diagnostics (errors, warnings) from the IDE",
            Props(("uri", "string", "Optional file URI to get diagnostics for. If not provided, gets diagnostics for all open files."))),
        Tool("checkDocumentDirty", "Check if a document has unsaved changes",
            Props(("filePath", "string", "Path to the file to check")), "filePath"),
        Tool("saveDocument", "Save a document with unsaved changes",
            Props(("filePath", "string", "Path to the file to save")), "filePath"),
        Tool("close_tab", "Close a tab by name",
            Props(("tab_name", "string", "Name of the tab to close")), "tab_name"),
        Tool("closeAllDiffTabs", "Close all diff tabs in the editor", Props()),
    ];

    public async Task<JsonObject> CallAsync(string name, JsonObject? args, CancellationToken cancellationToken)
    {
        args ??= [];
        try
        {
            return name switch
            {
                "openFile" => await OpenFileAsync(args),
                "openDiff" => await OpenDiffAsync(args, cancellationToken),
                "getCurrentSelection" => Json(SelectionResult(await host.GetCurrentSelectionAsync(), "No active editor found")),
                "getLatestSelection" => Json(SelectionResult(_latestSelection ?? await host.GetCurrentSelectionAsync(),
                    "No selection available")),
                "getOpenEditors" => Json(await OpenEditorsAsync()),
                "getWorkspaceFolders" => Json(WorkspaceFoldersResult()),
                "getDiagnostics" => Json(await DiagnosticsAsync(Str(args, "uri"))),
                "checkDocumentDirty" => Json(await DirtyAsync(Required(args, "filePath"))),
                "saveDocument" => Json(await SaveAsync(Required(args, "filePath"))),
                "close_tab" => CloseTab(Required(args, "tab_name")),
                "closeAllDiffTabs" => Text($"CLOSED_{CloseAllDiffs()}_DIFF_TABS"),
                _ => Error($"Unknown tool: {name}"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private async Task<JsonObject> OpenFileAsync(JsonObject args)
    {
        var path = ToLocalPath(Required(args, "filePath"));
        await host.OpenFileAsync(path, Str(args, "startText"), Str(args, "endText"));
        return Text($"Opened file: {path}");
    }

    /// <summary>人間が決めるまで返らない。同じ名前で重ねて来たら前のを却下で畳む（Claude は答えを待っていない）。</summary>
    private async Task<JsonObject> OpenDiffAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var tabName = Required(args, "tab_name");
        var path = ToLocalPath(Str(args, "new_file_path") ?? Required(args, "old_file_path"));
        var contents = Str(args, "new_file_contents") ?? "";
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_pendingDiffs.TryRemove(tabName, out var previous))
            previous.Cancel();
        _pendingDiffs[tabName] = closed;
        try
        {
            var decision = await host.ShowProposedEditAsync(tabName, path, contents, closed.Token);
            return decision.Accepted
                ? Contents("FILE_SAVED", decision.FinalContents ?? contents)
                : Contents("DIFF_REJECTED", tabName);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Contents("DIFF_REJECTED", tabName);
        }
        finally
        {
            _pendingDiffs.TryRemove(new KeyValuePair<string, CancellationTokenSource>(tabName, closed));
        }
    }

    private JsonObject CloseTab(string tabName)
    {
        if (_pendingDiffs.TryRemove(tabName, out var pending))
            pending.Cancel();
        return Text("TAB_CLOSED");
    }

    private int CloseAllDiffs()
    {
        var count = 0;
        foreach (var key in _pendingDiffs.Keys.ToArray())
        {
            if (_pendingDiffs.TryRemove(key, out var pending))
            {
                pending.Cancel();
                count++;
            }
        }
        return count;
    }

    private async Task<JsonObject> OpenEditorsAsync()
    {
        var tabs = new JsonArray();
        foreach (var editor in await host.GetOpenEditorsAsync())
        {
            tabs.Add(new JsonObject
            {
                ["uri"] = ToFileUri(editor.FilePath),
                ["isActive"] = editor.IsActive,
                ["label"] = Path.GetFileName(editor.FilePath),
                ["languageId"] = LanguageId(editor.FilePath),
                ["isDirty"] = editor.IsDirty,
            });
        }
        return new JsonObject { ["tabs"] = tabs };
    }

    private JsonObject WorkspaceFoldersResult()
    {
        var folders = workspaceFolders();
        var array = new JsonArray();
        foreach (var folder in folders)
            array.Add(new JsonObject
            {
                ["name"] = Path.GetFileName(folder.TrimEnd('\\', '/')),
                ["uri"] = ToFileUri(folder),
                ["path"] = folder,
            });
        return new JsonObject
        {
            ["success"] = true,
            ["folders"] = array,
            ["rootPath"] = folders.Count > 0 ? folders[0] : null,
        };
    }

    private async Task<JsonArray> DiagnosticsAsync(string? uri)
    {
        var filter = string.IsNullOrEmpty(uri) ? null : ToLocalPath(uri);
        var result = new JsonArray();
        foreach (var file in await host.GetDiagnosticsAsync(filter))
        {
            var items = new JsonArray();
            foreach (var d in file.Diagnostics)
                items.Add(new JsonObject
                {
                    ["message"] = d.Message,
                    ["severity"] = d.Severity.ToString(),
                    ["range"] = Range(d.StartLine, d.StartCharacter, d.EndLine, d.EndCharacter),
                    ["source"] = d.Source,
                    ["code"] = d.Code,
                });
            result.Add(new JsonObject { ["uri"] = ToFileUri(file.FilePath), ["diagnostics"] = items });
        }
        return result;
    }

    private async Task<JsonObject> DirtyAsync(string filePath)
    {
        var path = ToLocalPath(filePath);
        return await host.IsDirtyAsync(path) is { } dirty
            ? new JsonObject { ["success"] = true, ["filePath"] = path, ["isDirty"] = dirty, ["isUntitled"] = false }
            : new JsonObject { ["success"] = false, ["message"] = $"Document not open: {path}" };
    }

    private async Task<JsonObject> SaveAsync(string filePath)
    {
        var path = ToLocalPath(filePath);
        return await host.SaveAsync(path)
            ? new JsonObject { ["success"] = true, ["filePath"] = path, ["saved"] = true, ["message"] = "Document saved successfully" }
            : new JsonObject { ["success"] = false, ["message"] = $"Document not open: {path}" };
    }

    public static JsonObject SelectionResult(ClaudeIdeSelection? selection, string missingMessage)
        => selection is null
            ? new JsonObject { ["success"] = false, ["message"] = missingMessage }
            : SelectionParams(selection, success: true);

    /// <summary><c>selection_changed</c> 通知の params と、選択系ツールの答えに共通の形。</summary>
    public static JsonObject SelectionParams(ClaudeIdeSelection selection, bool success = false)
    {
        var node = new JsonObject();
        if (success)
            node["success"] = true;
        node["text"] = selection.Text;
        node["filePath"] = selection.FilePath;
        node["fileUrl"] = ToFileUri(selection.FilePath);
        var range = Range(selection.StartLine, selection.StartCharacter, selection.EndLine, selection.EndCharacter);
        range["isEmpty"] = selection.IsEmpty;
        node["selection"] = range;
        return node;
    }

    /// <summary><c>file:///C:/a%20b/c.cs</c> の形。</summary>
    public static string ToFileUri(string path) => new Uri(UpperDrive(Path.GetFullPath(path))).AbsoluteUri;

    /// <summary>Claude はパスを生で渡すことも URI で渡すこともある。<c>file:///c%3A/…</c>（ドライブのコロンを
    /// 符号化した形）も受ける。</summary>
    public static string ToLocalPath(string pathOrUri)
    {
        if (!pathOrUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(pathOrUri);
        var decoded = Uri.UnescapeDataString(pathOrUri["file:".Length..]).TrimStart('/');
        return UpperDrive(Path.GetFullPath(decoded.Replace('/', Path.DirectorySeparatorChar)));
    }

    // エディタのタブは C:\ で持つので、c:\ のまま比べると同じファイルを別物と見る。
    private static string UpperDrive(string path)
        => path.Length >= 2 && path[1] == ':' && char.IsLower(path[0])
            ? char.ToUpperInvariant(path[0]) + path[1..]
            : path;

    private static string LanguageId(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" => "typescript",
        ".tsx" => "typescriptreact",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".jsx" => "javascriptreact",
        ".json" => "json",
        ".md" => "markdown",
        ".xaml" or ".xml" or ".csproj" or ".props" or ".targets" => "xml",
        ".py" => "python",
        ".rs" => "rust",
        ".go" => "go",
        ".ps1" or ".psm1" => "powershell",
        ".html" or ".htm" => "html",
        ".css" => "css",
        ".yml" or ".yaml" => "yaml",
        _ => "plaintext",
    };

    private static JsonObject Range(int startLine, int startCharacter, int endLine, int endCharacter) => new()
    {
        ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startCharacter },
        ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter },
    };

    private static string? Str(JsonObject args, string key)
        => args[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Required(JsonObject args, string key)
        => Str(args, key) is { Length: > 0 } text ? text : throw new ArgumentException($"Missing required argument: {key}");

    private static JsonObject Text(string text) => Contents(text);

    private static JsonObject Json(JsonNode node) => Contents(node.ToJsonString(JsonOptions));

    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonObject Contents(params string[] texts)
    {
        var content = new JsonArray();
        foreach (var text in texts)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return new JsonObject { ["content"] = content };
    }

    private static JsonObject Error(string message)
    {
        var result = Contents($"Error: {message}");
        result["isError"] = true;
        return result;
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return new JsonObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
    }

    private static JsonObject Props(params (string Name, string Type, string Description)[] props)
    {
        var node = new JsonObject();
        foreach (var (name, type, description) in props)
            node[name] = new JsonObject { ["type"] = type, ["description"] = description };
        return node;
    }
}
