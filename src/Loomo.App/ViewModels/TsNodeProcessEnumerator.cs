using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>アタッチ候補の Node.js プロセス 1 行。<see cref="InspectPort"/> はコマンドラインの
/// <c>--inspect</c> 系フラグから取れたデバッグポート（無ければ null＝アタッチ不可）。</summary>
public sealed class TsNodeProcessViewModel
{
    public TsNodeProcessViewModel(int pid, string commandLine, int? inspectPort)
    {
        Pid = pid;
        CommandLine = commandLine;
        InspectPort = inspectPort;
    }

    public int Pid { get; }
    public string CommandLine { get; }
    public int? InspectPort { get; }

    /// <summary>リスト表示用。ポートが取れていれば先頭に出す。コマンドラインは長いので末尾側を優先して詰める。</summary>
    public string Display
    {
        get
        {
            var cmd = CommandLine.Length > 90 ? "…" + CommandLine[^89..] : CommandLine;
            return InspectPort is { } p ? $":{p}  node ({Pid})  {cmd}" : $"—    node ({Pid})  {cmd}";
        }
    }

    public bool CanAttach => InspectPort is not null;
}

/// <summary>実行中の node.exe を列挙し、コマンドラインから <c>--inspect</c> のデバッグポートを推定する。
/// コマンドライン取得は WMI（<c>Get-CimInstance Win32_Process</c>）を非表示 PowerShell で叩く
/// （P/Invoke や System.Management 依存を増やさず、既存の <see cref="ITerminalService"/> 経路を使う）。</summary>
internal static class TsNodeProcessEnumerator
{
    /// <summary><c>--inspect</c> / <c>--inspect-brk</c>（<c>=host:port</c> / <c>=port</c> 付き可）。</summary>
    private static readonly Regex InspectFlag = new(
        @"--inspect(?:-brk)?(?:=(?:(?<host>[^\s:=]+|\[[^\]]+\]):)?(?<port>\d+))?(?=\s|$)",
        RegexOptions.Compiled);

    private const string Command =
        "Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" | " +
        "Select-Object ProcessId, CommandLine | ConvertTo-Json -Compress";

    /// <summary>node プロセスを列挙する（インスペクタポート付きが先、次いで PID 順）。失敗時は空。</summary>
    public static async Task<IReadOnlyList<TsNodeProcessViewModel>> EnumerateAsync(ITerminalService terminal)
    {
        try
        {
            var result = await terminal.RunCommandAsync(Command, CancellationToken.None);
            if (!result.Success) return Array.Empty<TsNodeProcessViewModel>();
            return Parse(result.Output);
        }
        catch
        {
            return Array.Empty<TsNodeProcessViewModel>();
        }
    }

    /// <summary>ConvertTo-Json の出力（0 件=空 / 1 件=オブジェクト / 複数=配列）をパースする（テスト用に分離）。</summary>
    internal static IReadOnlyList<TsNodeProcessViewModel> Parse(string output)
    {
        var text = output.Trim();
        // 出力にバナー等が混ざっても最初の JSON 要素（{ か [）から読む。
        var start = text.IndexOfAny(['{', '[']);
        if (start < 0) return Array.Empty<TsNodeProcessViewModel>();
        text = text[start..];

        var list = new List<TsNodeProcessViewModel>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object) AddFrom(root, list);
            else if (root.ValueKind == JsonValueKind.Array)
                foreach (var e in root.EnumerateArray())
                    AddFrom(e, list);
        }
        catch { /* JSON でない出力（WMI 失敗等）は空扱い */ }

        list.Sort((a, b) =>
        {
            var byPort = (b.InspectPort is not null).CompareTo(a.InspectPort is not null);
            return byPort != 0 ? byPort : a.Pid.CompareTo(b.Pid);
        });
        return list;
    }

    private static void AddFrom(JsonElement e, List<TsNodeProcessViewModel> list)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        var pid = e.TryGetProperty("ProcessId", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        if (pid <= 0) return;
        var cmd = e.TryGetProperty("CommandLine", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "" : "";
        list.Add(new TsNodeProcessViewModel(pid, cmd, ParseInspectPort(cmd)));
    }

    /// <summary>コマンドラインから inspect ポートを推定する（テスト用に分離）。
    /// フラグ無し → null、<c>--inspect</c> のみ → 9229、<c>=port</c> / <c>=host:port</c> → その値。</summary>
    internal static int? ParseInspectPort(string commandLine)
    {
        var m = InspectFlag.Match(commandLine);
        if (!m.Success) return null;
        return m.Groups["port"].Success && int.TryParse(m.Groups["port"].Value, out var port) ? port : 9229;
    }
}
