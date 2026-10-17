using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// Claude Code のフック（hooks）から Loomo への通り道（§24.1.1）。タイトルの印だけでは「ターンが終わった」と
/// 「承認を待っている」が区別できない（どちらも ✳）。Claude Code の <c>Notification</c> フックは承認を求めるときに
/// 発火するので、それを Loomo へ届ける。
/// <para>フックのスクリプトは Loomo のターミナルが渡す <c>LOOMO_TERMINAL_TAB</c>（どのタブか）と
/// <c>LOOMO_CLAUDE_HOOK_PIPE</c>（宛先）を読み、フックの入力 JSON をタブ ID と一緒に1行で送る。どちらも無い
/// （Loomo の外で起動した claude）なら何もせず終わる——設定はユーザー全体に入るので、外では無害であることが前提。</para>
/// <para>パイプ名はユーザーごとに固定。端末のシェルは Loomo より長生きする（§34）ので、再起動後も古いシェルの
/// 環境変数が指す名前で待てるように。</para>
/// </summary>
public sealed class ClaudeHookBridge : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    public ClaudeHookBridge(string? pipeName = null)
    {
        PipeName = pipeName ?? $"loomo-claude-hook-{Environment.UserName}";
    }

    public string PipeName { get; }

    /// <summary>フックが1件届いた（タブ ID・フックの入力 JSON）。パイプのスレッドから呼ばれる。</summary>
    public event Action<Guid, JsonObject>? HookReceived;

    public void Start() => _ = Task.Run(() => ListenLoopAsync(_stop.Token));

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                // 非同期ハンドル必須（同期だと読み待ちが他を塞ぐ、memory: named-pipe-sync-deadlock）。
                server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                continue;
            }
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                return;
            }
            catch (IOException)
            {
                server.Dispose();
                continue;
            }
            _ = Task.Run(() => ReadOneAsync(server, cancellationToken), cancellationToken);
        }
    }

    private async Task ReadOneAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using var _ = server;
        try
        {
            using var reader = new StreamReader(server, new UTF8Encoding(false));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (Parse(line) is { } message)
                HookReceived?.Invoke(message.Tab, message.Event);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    /// <summary>フックのスクリプトが送る1行（<c>{"tab":"…","event":{…}}</c>）を読む。</summary>
    public static (Guid Tab, JsonObject Event)? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            var root = JsonNode.Parse(line) as JsonObject;
            if (root?["tab"]?.GetValue<string>() is { } tab && Guid.TryParse(tab, out var id)
                && root["event"] is JsonObject hookEvent)
                return (id, hookEvent);
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    /// <summary>この通知は承認を求めているか。新しい版は <c>notification_type</c> を持ち、古い版は文言だけ
    /// （"Claude needs your permission to use Bash"）なので両方を見る。</summary>
    public static bool IsPermissionRequest(JsonObject hookEvent)
    {
        if ((string?)hookEvent["hook_event_name"] is { } name && name != "Notification")
            return false;
        if ((string?)hookEvent["notification_type"] is { } type)
            return type == "permission_prompt";
        return ((string?)hookEvent["message"])?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true;
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>Claude Code のユーザー設定（<c>~/.claude/settings.json</c>）へ Loomo のフックを足す・外す（§24.1.1）。
/// 既存の設定・他のフックには触らず、Loomo のスクリプトを指す項目だけを足し引きする。</summary>
public static class ClaudeHookInstaller
{
    public const string ScriptFileName = "loomo-claude-hook.ps1";

    /// <summary>Loomo が聞くフック。Notification＝承認待ちの区別、SessionStart＝どのタブがどのセッションか
    /// （再開・コンテキスト量の表示に使う）。</summary>
    public static readonly string[] Events = ["Notification", "SessionStart"];

    /// <summary>フックのスクリプト本体。LOOMO_* が無ければ（Loomo の外の claude）即終了する。</summary>
    public const string Script = """
        # Loomo が置いた Claude Code のフック（§24.1.1）。Loomo のターミナルの外では何もしない。
        $pipe = $env:LOOMO_CLAUDE_HOOK_PIPE
        $tab = $env:LOOMO_TERMINAL_TAB
        if (-not $pipe -or -not $tab) { exit 0 }
        try {
            # [Console]::In は WinPS 5.1 だと OEM コードページで読むので、バイトのまま受けて UTF-8 で開く。
            $buffer = New-Object System.IO.MemoryStream
            [Console]::OpenStandardInput().CopyTo($buffer)
            $payload = [System.Text.Encoding]::UTF8.GetString($buffer.ToArray())
            $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipe, [System.IO.Pipes.PipeDirection]::Out)
            $client.Connect(500)
            $writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false))
            $writer.WriteLine('{"tab":"' + $tab + '","event":' + $payload.Trim() + '}')
            $writer.Flush()
            $client.Dispose()
        } catch { }
        exit 0
        """;

    public static string DefaultScriptPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "claude", ScriptFileName);

    public static string SettingsPath(string? claudeConfigDirectory = null)
        => Path.Combine(claudeConfigDirectory ?? ClaudeIdeServer.DefaultClaudeConfigDirectory(), "settings.json");

    public static string CommandFor(string scriptPath)
        => $"powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";

    /// <summary>入っているか（聞くフックすべてに Loomo のスクリプトを指す項目があるか）。</summary>
    public static bool IsInstalled(JsonObject settings, string scriptPath)
        => Events.All(name => HasHook(settings, name, scriptPath));

    private static bool HasHook(JsonObject settings, string eventName, string scriptPath)
        => EnumerateCommands(settings, eventName).Any(command => command.Contains(scriptPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>設定へ足す（足りないイベントにだけ）。何も足さなければ false。</summary>
    public static bool AddTo(JsonObject settings, string scriptPath)
    {
        var added = false;
        foreach (var name in Events)
        {
            if (HasHook(settings, name, scriptPath))
                continue;
            AddHook(settings, name, scriptPath);
            added = true;
        }
        return added;
    }

    private static void AddHook(JsonObject settings, string eventName, string scriptPath)
    {
        if (settings["hooks"] is not JsonObject hooks)
            settings["hooks"] = hooks = new JsonObject();
        if (hooks[eventName] is not JsonArray entries)
            hooks[eventName] = entries = new JsonArray();
        entries.Add(new JsonObject
        {
            ["matcher"] = "",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = CommandFor(scriptPath),
                ["timeout"] = 10,
            }),
        });
    }

    /// <summary>Loomo のフックだけを外す。空になった入れ物も片付ける。</summary>
    public static bool RemoveFrom(JsonObject settings, string scriptPath)
    {
        if (settings["hooks"] is not JsonObject hooks)
            return false;
        var removed = false;
        foreach (var name in Events)
        {
            if (hooks[name] is JsonArray entries && RemoveFrom(entries, scriptPath))
                removed = true;
            if (hooks[name] is JsonArray { Count: 0 })
                hooks.Remove(name);
        }
        if (hooks.Count == 0)
            settings.Remove("hooks");
        return removed;
    }

    private static bool RemoveFrom(JsonArray notification, string scriptPath)
    {
        var removed = false;
        foreach (var entry in notification.OfType<JsonObject>().ToArray())
        {
            if (entry["hooks"] is not JsonArray inner)
                continue;
            foreach (var hook in inner.OfType<JsonObject>().ToArray())
            {
                if (((string?)hook["command"])?.Contains(scriptPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    inner.Remove(hook);
                    removed = true;
                }
            }
            if (inner.Count == 0)
                notification.Remove(entry);
        }
        return removed;
    }

    /// <summary>スクリプトを置き、設定ファイルへ足す（初回だけ元の設定を <c>.loomo-backup</c> に残す）。
    /// 入れたなら true、既に入っていたなら false。</summary>
    public static bool Install(string? claudeConfigDirectory = null, string? scriptPath = null)
    {
        scriptPath ??= DefaultScriptPath();
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        // PowerShell 5.1 は BOM の無い UTF-8 を ANSI として読むので、日本語のコメントのために BOM を付ける。
        File.WriteAllText(scriptPath, Script, new UTF8Encoding(true));
        var settingsPath = SettingsPath(claudeConfigDirectory);
        var settings = Load(settingsPath);
        if (!AddTo(settings, scriptPath))
            return false;
        Save(settingsPath, settings);
        return true;
    }

    public static bool Uninstall(string? claudeConfigDirectory = null, string? scriptPath = null)
    {
        scriptPath ??= DefaultScriptPath();
        var settingsPath = SettingsPath(claudeConfigDirectory);
        if (!File.Exists(settingsPath))
            return false;
        var settings = Load(settingsPath);
        if (!RemoveFrom(settings, scriptPath))
            return false;
        Save(settingsPath, settings);
        return true;
    }

    public static bool IsInstalled(string? claudeConfigDirectory = null, string? scriptPath = null)
    {
        var settingsPath = SettingsPath(claudeConfigDirectory);
        return File.Exists(settingsPath) && IsInstalled(Load(settingsPath), scriptPath ?? DefaultScriptPath());
    }

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();
        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new InvalidDataException($"{path} がオブジェクトではありません。");
    }

    private static void Save(string path, JsonObject settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = path + ".loomo-backup";
        if (File.Exists(path) && !File.Exists(backup))
            File.Copy(path, backup);
        var json = settings.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var temp = path + ".loomo-tmp";
        File.WriteAllText(temp, json + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static IEnumerable<string> EnumerateCommands(JsonObject settings, string eventName)
    {
        if (settings["hooks"] is not JsonObject hooks || hooks[eventName] is not JsonArray notification)
            yield break;
        foreach (var entry in notification.OfType<JsonObject>())
        {
            if (entry["hooks"] is not JsonArray inner)
                continue;
            foreach (var hook in inner.OfType<JsonObject>())
                if ((string?)hook["command"] is { } command)
                    yield return command;
        }
    }
}
