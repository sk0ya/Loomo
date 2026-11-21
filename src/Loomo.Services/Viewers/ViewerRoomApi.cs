using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Core.Processes;
using sk0ya.Loomo.Services.Terminal;

namespace sk0ya.Loomo.Services.Viewers;

/// <summary>Viewerの頼みを断る理由。文はそのままViewerのページ（の <c>Promise</c> の失敗）へ返る。</summary>
public sealed class ViewerApiException(string message) : Exception(message);

/// <summary>本体の機能をViewerの口として足す（§44.3 の <c>motion</c>・§44.4 の <c>orders</c>）。<c>&lt;Api&gt;.&lt;名前&gt;</c> の頼みが届き、
/// Viewerが <see cref="Api"/> を挙げていなければ届く前に断られる。</summary>
public interface IViewerApiExtension
{
    string Api { get; }

    /// <param name="method">口の名前を除いた部分（<c>motion.state</c> なら <c>state</c>）。</param>
    Task<JsonNode?> CallAsync(string method, JsonObject? args, CancellationToken cancel);
}

/// <summary>Viewerの口のうち、画面が要るもの（§44.2）。実装は App。どのスレッドから呼ばれてもよい——実装が Dispatcher を通す。</summary>
public interface IViewerRoom
{
    /// <summary>前に出ているエディタのファイル（<c>{ path, line, column }</c>。行と桁は 1 始まり）。無ければ null。</summary>
    Task<JsonNode?> ActiveFileAsync();

    /// <summary>ワークスペースの中のファイルをエディタに出す。外・無いものは false。</summary>
    Task<bool> OpenFileAsync(string path, int line);

    /// <summary>http(s) の URL をブラウザペインの新しいタブに出す。</summary>
    Task<bool> OpenUrlAsync(string url);

    /// <summary>リポジトリの一覧と状態（すでに読んであるものを引くだけ。§40）。</summary>
    Task<JsonNode> GitStatusAsync();

    void Toast(string text);
}

/// <summary>
/// Viewerのページから届いた頼み（<c>window.loomo.*</c>）を捌く（§44.2）。<b>線引きはここ</b>：
/// 口は <c>viewer.json</c> が挙げたものだけ・読めるのはワークスペースの中だけ・回せるのは名前つきで書いたコマンドだけ
/// （行は固定で、Viewerから渡る値は環境変数と変数 <c>$LoomoInput</c> にしか入らない）・見えるターミナルには打たない・書く口は無い。
/// WPF に依らない（画面が要るものは <see cref="IViewerRoom"/>）。
/// </summary>
public sealed class ViewerRoomApi(ViewerStore store, IWorkspaceService workspace, IViewerRoom room,
    IReadOnlyList<IViewerApiExtension>? extensions = null)
{
    public const int MaxReadBytes = 2 * 1024 * 1024;
    public const int MaxListEntries = 2000;
    public const int MaxArgs = 16;
    private const int MaxOutputChars = 400_000;

    /// <param name="emit">途中経過（コマンドの出力の 1 行）をページへ流す。別スレッドから呼ばれる。</param>
    /// <param name="target">人間がパスを渡して開いたタブなら、そのパス（<c>target.*</c> が読めるのはこれ 1 つだけ）。</param>
    public async Task<JsonNode?> CallAsync(
        ViewerManifest viewer, string method, JsonObject? args, Action<string, JsonNode> emit, CancellationToken cancel,
        string? target = null)
    {
        switch (method)
        {
            case "target.info":
            {
                Require(viewer, ViewerApi.Target);
                var path = TargetPath(target);
                return await Task.Run(() => TargetInfo(path), cancel).ConfigureAwait(false);
            }

            case "target.read":
            {
                Require(viewer, ViewerApi.Target);
                var path = TargetPath(target);
                if (Directory.Exists(path))
                    throw new ViewerApiException("The target is a folder; use target.list().");
                return await Task.Run(() => ReadText(path), cancel).ConfigureAwait(false);
            }

            case "target.bytes":
            {
                Require(viewer, ViewerApi.Target);
                var path = TargetPath(target);
                if (Directory.Exists(path))
                    throw new ViewerApiException("The target is a folder; use target.list().");
                return await Task.Run(() => ReadBase64(path), cancel).ConfigureAwait(false);
            }

            case "target.list":
            {
                Require(viewer, ViewerApi.Target);
                var path = TargetPath(target);
                if (!Directory.Exists(path))
                    throw new ViewerApiException("The target is a file; use target.read() or target.bytes().");
                return await Task.Run(() => ListFolder(path), cancel).ConfigureAwait(false);
            }

            case "workspace.folders":
                Require(viewer, ViewerApi.Workspace);
                return new JsonArray(workspace.Folders.Select(folder => (JsonNode)new JsonObject
                {
                    ["path"] = folder,
                    ["name"] = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                }).ToArray());

            case "workspace.activeFile":
                Require(viewer, ViewerApi.Workspace);
                return await room.ActiveFileAsync().ConfigureAwait(false);

            case "open":
            {
                Require(viewer, ViewerApi.Open);
                var path = WorkspaceFile(Text(args, "path"), mustBeFile: true);
                if (!await room.OpenFileAsync(path, Math.Max(0, Number(args, "line"))).ConfigureAwait(false))
                    throw new ViewerApiException("The file could not be opened.");
                return true;
            }

            case "openUrl":
            {
                Require(viewer, ViewerApi.Open);
                if (!Uri.TryCreate(Text(args, "url"), UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new ViewerApiException("Only http:// and https:// URLs can be opened.");
                return await room.OpenUrlAsync(uri.AbsoluteUri).ConfigureAwait(false);
            }

            case "fs.read":
            {
                Require(viewer, ViewerApi.FsRead);
                var path = WorkspaceFile(Text(args, "path"), mustBeFile: true);
                return await Task.Run(() => ReadText(path), cancel).ConfigureAwait(false);
            }

            case "fs.list":
            {
                Require(viewer, ViewerApi.FsRead);
                var path = WorkspaceFile(Text(args, "path"), mustBeFile: false);
                return await Task.Run(() => ListFolder(path), cancel).ConfigureAwait(false);
            }

            case "git.status":
                Require(viewer, ViewerApi.Git);
                return await room.GitStatusAsync().ConfigureAwait(false);

            case "run":
                Require(viewer, ViewerApi.Run);
                return await RunAsync(viewer, args, emit, cancel).ConfigureAwait(false);

            case "state.get":
                return JsonNode.Parse(store.ReadState(viewer.Id));

            case "state.set":
                if (args is null || !args.ContainsKey("value"))
                    throw new ViewerApiException("state.set needs a value.");
                if (!store.WriteState(viewer.Id, args["value"]?.ToJsonString() ?? "null"))
                    throw new ViewerApiException($"The state could not be saved (limit {ViewerStore.MaxStateBytes / 1024} KB).");
                return true;

            case "toast":
                if (Text(args, "text") is { Length: > 0 } text)
                    room.Toast(text.Length > 300 ? text[..300] + "…" : text);
                return true;

            default:
                var dot = method.IndexOf('.');
                if (dot > 0 && extensions?.FirstOrDefault(extension => extension.Api == method[..dot]) is { } owner)
                {
                    Require(viewer, owner.Api);
                    return await owner.CallAsync(method[(dot + 1)..], args, cancel).ConfigureAwait(false);
                }
                throw new ViewerApiException($"Unknown method: {method}");
        }
    }

    public const int MaxBytesRead = 32 * 1024 * 1024;

    /// <summary>このタブに渡されたパス。ほかのパスは読ませない（ページから届くパスは受け取らない）。</summary>
    private static string TargetPath(string? target)
    {
        if (string.IsNullOrEmpty(target))
            throw new ViewerApiException("This viewer was not opened for a file (there is no target).");
        if (!File.Exists(target) && !Directory.Exists(target))
            throw new ViewerApiException("The target no longer exists: " + target);
        return target;
    }

    private static JsonNode TargetInfo(string path)
    {
        var isDirectory = Directory.Exists(path);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        return new JsonObject
        {
            ["path"] = info.FullName,
            ["name"] = info.Name,
            ["isDirectory"] = isDirectory,
            ["size"] = isDirectory ? 0 : ((FileInfo)info).Length,
            ["modified"] = info.LastWriteTimeUtc.ToString("o"),
        };
    }

    private static JsonNode ReadBase64(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxBytesRead)
                throw new ViewerApiException($"The file is larger than {MaxBytesRead / 1024 / 1024} MB.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return JsonValue.Create(Convert.ToBase64String(memory.GetBuffer(), 0, (int)memory.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ViewerApiException("The file could not be read: " + ex.Message);
        }
    }

    private static void Require(ViewerManifest viewer, string api)
    {
        if (!viewer.Allows(api))
            throw new ViewerApiException($"This viewer does not declare \"{api}\" in the \"api\" list of viewer.json.");
    }

    /// <summary>ワークスペースの中の、いまあるパスへ解く。相対パスは各フォルダーから探す（<c>workspace.Contains</c> で線を引く）。</summary>
    private string WorkspaceFile(string? path, bool mustBeFile)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ViewerApiException("path is required.");
        try
        {
            var candidates = Path.IsPathRooted(path)
                ? [Path.GetFullPath(path)]
                : workspace.Folders.Select(folder => Path.GetFullPath(Path.Combine(folder, path)));
            foreach (var full in candidates)
            {
                if (workspace.Contains(full) && (mustBeFile ? File.Exists(full) : Directory.Exists(full)))
                    return full;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        throw new ViewerApiException("That path is not in the user's workspace (or does not exist).");
    }

    private static JsonNode ReadText(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxReadBytes)
                throw new ViewerApiException($"The file is larger than {MaxReadBytes / 1024 / 1024} MB.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonValue.Create(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ViewerApiException("The file could not be read: " + ex.Message);
        }
    }

    private static JsonNode ListFolder(string path)
    {
        try
        {
            var entries = new JsonArray();
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos().Take(MaxListEntries))
            {
                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                entries.Add(new JsonObject
                {
                    ["name"] = info.Name,
                    ["path"] = info.FullName,
                    ["isDirectory"] = isDirectory,
                    ["size"] = isDirectory ? 0 : ((FileInfo)info).Length,
                    ["modified"] = info.LastWriteTimeUtc.ToString("o"),
                });
            }
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ViewerApiException("The folder could not be listed: " + ex.Message);
        }
    }

    private async Task<JsonNode> RunAsync(ViewerManifest viewer, JsonObject? args, Action<string, JsonNode> emit, CancellationToken cancel)
    {
        var name = Text(args, "name") ?? "";
        if (viewer.Command(name) is not { } command)
            throw new ViewerApiException($"No command named \"{name}\" in the \"commands\" of viewer.json. A viewer can only run the commands it declares.");
        var values = (args?["args"] as JsonArray)?.Select(node => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            null => "",
            _ => node.ToJsonString(),
        }).ToArray() ?? [];
        if (values.Length > MaxArgs)
            throw new ViewerApiException($"At most {MaxArgs} args.");
        var cwd = command.Cwd == ViewerCommand.CwdViewer ? store.FolderOf(viewer.Id) : workspace.PrimaryFolder;
        if (cwd is null || !Directory.Exists(cwd))
            throw new ViewerApiException("No workspace folder is open to run the command in.");
        var token = Text(args, "run") ?? "";

        var result = await ViewerCommandRunner.RunAsync(command, cwd, values, Text(args, "stdin"),
            line => emit("run.output", new JsonObject { ["run"] = token, ["line"] = line }), cancel).ConfigureAwait(false);
        return new JsonObject
        {
            ["exitCode"] = result.ExitCode,
            ["output"] = result.Output.Length > MaxOutputChars ? result.Output[^MaxOutputChars..] : result.Output,
            ["timedOut"] = result.TimedOut,
        };
    }

    private static string? Text(JsonObject? args, string name)
        => args?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Number(JsonObject? args, string name)
        => args?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
}

public sealed record ViewerCommandResult(int ExitCode, string Output, bool TimedOut);

/// <summary>
/// Viewerのコマンドを裏の PowerShell で回す（§44.2）。見えるターミナルには流さない（<c>TerminalService.RunCommandAsync</c> と同じ殻）。
/// <b>行は <c>viewer.json</c> のまま</b>——Viewerから渡る値は <c>LOOMO_ARG_1</c>…（と個数の <c>LOOMO_ARGC</c>）・<c>$LoomoInput</c> にだけ入るので、
/// 人間が許した行と違うものは走らない。出力は専用スレッドで読む（<see cref="ChildProcessIo"/>）。
/// </summary>
public static class ViewerCommandRunner
{
    private const string InputPreamble =
        "$LoomoInput = ''; try { $LoomoInput = (New-Object System.IO.StreamReader([Console]::OpenStandardInput(), "
        + "(New-Object System.Text.UTF8Encoding $false))).ReadToEnd() } catch { }; ";

    public static async Task<ViewerCommandResult> RunAsync(
        ViewerCommand command, string cwd, IReadOnlyList<string> args, string? stdin, Action<string> onLine, CancellationToken cancel,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = TerminalService.ShellExecutable,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-Command");
        // Viewerが渡した文字（stdin）は、先に UTF-8 で読み切って $LoomoInput に入れておく。PowerShell の $input は
        // コンソールのコードページ（日本語環境では cp932）で読むので化け、起動後に InputEncoding を変えても効かない。
        info.ArgumentList.Add(TerminalService.Utf8Preamble + InputPreamble + command.Command);
        info.Environment["LOOMO_ARGC"] = args.Count.ToString();
        for (var i = 0; i < args.Count; i++)
            info.Environment[$"LOOMO_ARG_{i + 1}"] = args[i];
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                info.Environment[name] = value;
        }
        // 常駐ノードが出力のパイプを握ると EOF が来ない（dotnet build をViewerから回したとき）。
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var gate = new object();
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ViewerCommandResult(-1, "The command could not be started: " + ex.Message, false);
        }
        ChildProcessIo.TrySetBackgroundPriority(process);

        void Append(string line)
        {
            var clean = TerminalTextSanitizer.RemoveAnsiEscapes(line);
            lock (gate)
                output.AppendLine(clean);
            try { onLine(clean); } catch { /* Viewerが閉じたあとの 1 行は捨てる */ }
        }
        var pumps = Task.WhenAll(
            ChildProcessIo.PumpLinesAsync(process.StandardOutput, Append, "Viewerのコマンド:stdout"),
            ChildProcessIo.PumpLinesAsync(process.StandardError, Append, "Viewerのコマンド:stderr"));
        try
        {
            if (!string.IsNullOrEmpty(stdin))
                await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();   // 入力待ちで固まらせない
        }
        catch (IOException) { /* すぐ終わるコマンドは読む前に閉じる */ }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(command.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            // 終了と読み切りは別物。切り離された孫がパイプを握っていると EOF が来ないので、ここも期限の内側。
            if (await Task.WhenAny(pumps, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false) != pumps)
                throw new OperationCanceledException();
            lock (gate)
                return new ViewerCommandResult(process.ExitCode, output.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* もう終わっている */ }
            await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            lock (gate)
                return new ViewerCommandResult(-1, output.ToString(), timeout.IsCancellationRequested && !cancel.IsCancellationRequested);
        }
    }
}

/// <summary>Viewerの書き方（§44.2）。MCP の <c>viewer_guide</c> がモデルへ返す——だから英語。</summary>
public static class ViewerGuide
{
    public static string Text(ViewerStore store) => $$"""
        # Growing a "viewer" in Loomo

        A viewer is a small HTML tool that lives in a pane of Loomo, the room the user is sitting in. You create it by writing
        files; Loomo never needs a rebuild or restart. Use a viewer when the user asks for a small tool, view or dashboard
        ("I want a list of the processes holding ports", "show me X at a glance").

        ## Where
        Folder: {{store.Root}}\<id>\      (id: lowercase letters, digits, hyphens; max 40 chars)
          viewer.json    what the viewer is and what it may do
          index.html   the page (plus any css/js/images next to it; relative URLs work)
        Write the files with your normal file tools, then call the `show_viewer` tool with the id.
        While the viewer is open, saving a file in its folder reloads it.

        ## viewer.json
        {
          "title": "Ports",
          "description": "Processes that hold listening ports",
          "api": ["run", "open"],
          "commands": {
            "ports": { "command": "Get-NetTCPConnection -State Listen | Select-Object LocalPort,OwningProcess | ConvertTo-Json", "timeoutSeconds": 20 }
          }
        }
        - "api": the room APIs the page uses. Declare only what you need. A viewer that declares only workspace, open, fs.read
          and git opens at once; declaring run, net, motion or orders makes the user approve the list once before it opens.
            workspace  folders of the workspace, the file and line in front of the user
            open       open a workspace file in the editor / an http(s) URL in the browser pane
            fs.read    read files and list folders inside the workspace (read-only; there is no write API)
            target     read the one file (or folder) the user opened this tab for (see "Viewers for a file" below)
            git        repositories and their status
            run        run the commands declared under "commands"
            motion     Loomo's recorded "motion" of the workspace (see the motion_guide tool); read and record
            orders     Loomo's standing orders and their tray; usually only the built-in "orders" viewer needs this
            net        let the page reach the network (without it every request outside the viewer folder is blocked,
                       so do not link CDN scripts or fonts unless you declare this)
        - "commands": fixed PowerShell command lines, run hidden (never in the user's visible terminal). The page cannot change
          the command line. Values from the page arrive as environment variables $env:LOOMO_ARG_1 … $env:LOOMO_ARG_n
          ($env:LOOMO_ARGC = count); the text passed as `stdin` is in the PowerShell variable $LoomoInput (UTF-8;
          do not use $input). Optional per command: "cwd": "workspace" (default) | "viewer",
          "timeoutSeconds" (default 60, max 600). Prefer ConvertTo-Json output so the page can parse it.
        - For a viewer that needs approval, changing "api" or "commands" asks the user again. Changing index.html does not.

        ## The page API: window.loomo  (all methods return Promises)
        loomo.workspace.folders()              -> [{ path, name }]
        loomo.workspace.activeFile()           -> { path, line, column } | null
        loomo.open(path, line?)                -> opens in the editor (path absolute or workspace-relative)
        loomo.openUrl(url)                     -> opens in a new browser-pane tab
        loomo.fs.read(path)                    -> string (UTF-8, max 2 MB)
        loomo.fs.list(dir)                     -> [{ name, path, isDirectory, size, modified }]
        loomo.git.status()                     -> [{ root, name, branch, ahead, behind, changed: [{ path, kind }] }]
        loomo.run(name, { args?, stdin?, onLine? }) -> { exitCode, output, timedOut }   (onLine gets each output line live)
        loomo.state.get() / loomo.state.set(v) -> small JSON storage for this viewer (max 256 KB)
        loomo.toast(text)                      -> a notification in the room
        loomo.target.info()                    -> { path, name, isDirectory, size, modified }   (needs "target")
        loomo.target.read()                    -> string (UTF-8, max 2 MB)
        loomo.target.bytes()                   -> base64 string (max 32 MB; for binary formats)
        loomo.target.list()                    -> [{ name, path, isDirectory, size, modified }] when the target is a folder
        loomo.on(event, handler)               -> events: "activeFileChanged" { path } (needs "workspace"), "gitChanged" (needs "git")
        A rejected Promise carries a message that says what was not allowed.

        ## Viewers for a file
        The user can open any path in Loomo's Viewer pane. HTML pages, programs and the formats Loomo already previews are
        shown directly. For any other kind of file, Loomo asks Claude Code to write a viewer for that file *kind*, with the
        id "file-<extension>" (for example file-sqlite; file-folder for folders, file-noext without an extension). The same
        viewer is reused for every file of that kind, so it must not hard-code one path: read the file with loomo.target.*
        (declare "target" in "api"). It is re-rendered when the file changes on disk (the page is reloaded).

        ## Look
        Loomo injects CSS variables that follow the user's theme; use them so the viewer looks like part of the room:
          --loomo-bg  --loomo-panel  --loomo-fg  --loomo-fg-dim  --loomo-accent  --loomo-border  --loomo-font-size
        Keep it dense and plain: no big headers, no margins around the page, system fonts, monospace for data.
        Do not add a title bar: the pane tab already shows the title.

        ## Minimal index.html
        <!doctype html><meta charset="utf-8">
        <style>body{margin:8px;background:var(--loomo-panel);color:var(--loomo-fg);font:var(--loomo-font-size) system-ui}</style>
        <button id="go">Refresh</button><pre id="out"></pre>
        <script>
          async function refresh() {
            const r = await loomo.run("ports");
            document.getElementById("out").textContent = r.output;
          }
          document.getElementById("go").onclick = refresh; refresh();
        </script>

        ## Existing viewers
        {{Existing(store)}}
        """;

    private static string Existing(ViewerStore store)
    {
        var viewers = store.List();
        return viewers.Count == 0
            ? "(none yet)"
            : string.Join("\n", viewers.Select(viewer => viewer.Manifest is { } manifest
                ? $"- {manifest.Id}: {manifest.Title}" + (manifest.Description.Length > 0 ? $" — {manifest.Description}" : "")
                : $"- {viewer.Id}: (broken) {viewer.Error}"));
    }
}
