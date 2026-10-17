using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// 「このファイルを Loomo のエディタで開き、閉じるまで待つ」（<c>code --wait</c> 相当、§38.5）。Loomo のターミナルの
/// <c>EDITOR</c>／<c>VISUAL</c> はこの口を叩くスクリプトを指す——Claude Code の Ctrl+G（プロンプトを外部エディタで
/// 書く）も、<c>git commit</c> のメッセージも、部屋のエディタで書ける。
/// <para>スクリプトはパスを1行で送り、Loomo がタブを閉じたら返る1行を待つ。Loomo に繋がらなければ
/// メモ帳で開いて待つ（エディタが開かないまま固まるのが一番困る）。</para>
/// </summary>
public sealed class LoomoEditBridge : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, Task> _editAndWait;

    /// <param name="editAndWait">パスを受けてエディタで開き、人間が閉じたら完了する（UI 側の実装）。</param>
    public LoomoEditBridge(Func<string, Task> editAndWait, string? pipeName = null)
    {
        _editAndWait = editAndWait;
        PipeName = pipeName ?? $"loomo-edit-{Environment.UserName}";
    }

    public string PipeName { get; }

    public void Start() => _ = Task.Run(() => ListenLoopAsync(_stop.Token));

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
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
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                server.Dispose();
                if (ex is OperationCanceledException)
                    return;
                continue;
            }
            _ = Task.Run(() => ServeAsync(server, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using var _ = server;
        try
        {
            using var reader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (ParsePath(line) is not { } path)
                return;
            await _editAndWait(path).ConfigureAwait(false);
            var done = Encoding.UTF8.GetBytes("done\n");
            await server.WriteAsync(done, cancellationToken).ConfigureAwait(false);
            await server.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public static string? ParsePath(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            return (JsonNode.Parse(line) as JsonObject)?["path"]?.GetValue<string>() is { Length: > 0 } path
                ? Path.GetFullPath(path)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return null; }
    }

    public void Dispose() => _stop.Cancel();

    // ── EDITOR に置くスクリプト ─────────────────────────────────
    public const string ScriptFileName = "loomo-edit.ps1";
    public const string ShimFileName = "loomo-edit.cmd";

    public const string Script = """
        # Loomo のエディタでファイルを開き、タブを閉じるまで待つ（§38.5）。Loomo に繋がらなければメモ帳で待つ。
        param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Files)
        $path = ($Files | Where-Object { $_ -and -not $_.StartsWith('-') } | Select-Object -Last 1)
        if (-not $path) { exit 1 }
        $full = [System.IO.Path]::GetFullPath($path)
        $pipe = $env:LOOMO_EDIT_PIPE
        try {
            if (-not $pipe) { throw 'no pipe' }
            $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
            $client.Connect(1500)
            $writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false))
            $writer.AutoFlush = $true
            $writer.WriteLine((@{ path = $full } | ConvertTo-Json -Compress))
            $reader = [System.IO.StreamReader]::new($client)
            [void]$reader.ReadLine()
            $client.Dispose()
        } catch {
            Start-Process notepad.exe -ArgumentList "`"$full`"" -Wait
        }
        exit 0
        """;

    /// <summary>EDITOR に入れる .cmd（.ps1 は直接は起動できない道具が多いので、.cmd で包む）。</summary>
    public const string Shim = "@powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0loomo-edit.ps1\" %*\r\n";

    /// <summary>スクリプトを置いて、EDITOR に入れる文字列（.cmd のフルパス）を返す。</summary>
    public static string InstallScripts(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "claude");
        Directory.CreateDirectory(directory);
        WriteIfChanged(Path.Combine(directory, ScriptFileName), Script, new UTF8Encoding(true));
        var shim = Path.Combine(directory, ShimFileName);
        WriteIfChanged(shim, Shim, new UTF8Encoding(false));
        return shim.Contains(' ') ? $"\"{shim}\"" : shim;
    }

    private static void WriteIfChanged(string path, string content, Encoding encoding)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content)
                return;
            File.WriteAllText(path, content, encoding);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
