using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.Services.Browser;

/// <summary>ページのエラーや警告 1 件（§45.5 P2 `browser_errors`）。</summary>
public sealed record BrowserConsoleEntry(string Kind, string Url, int Line, string Text, DateTimeOffset At);

/// <summary>
/// ブラウザのページのコンソールの記録（直近の数件だけ）。DevTools の <c>Runtime.exceptionThrown</c>・<c>Runtime.consoleAPICalled</c>
/// のイベントを、この型で読んで溜める。読むのは<b>エラーと警告だけ</b>（ログは読まない）。
/// <para>ページのコードには何も足さない（DevTools の購読だけ）。Loomo が見始めたあとのものだけが残る。</para>
/// </summary>
public sealed class BrowserConsoleLog
{
    public const int Capacity = 30;
    public const int MaxText = 300;

    private readonly object _gate = new();
    private readonly List<BrowserConsoleEntry> _entries = new();

    public void Add(BrowserConsoleEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
                _entries.RemoveRange(0, _entries.Count - Capacity);
        }
    }

    /// <summary>新しいものから。読む道具に返す分。</summary>
    public IReadOnlyList<BrowserConsoleEntry> Recent(int max)
    {
        lock (_gate)
            return _entries.AsEnumerable().Reverse().Take(max).ToList();
    }

    /// <summary><c>Runtime.exceptionThrown</c> の JSON から、未処理の例外を 1 件にする。読めなければ null。</summary>
    public static BrowserConsoleEntry? ParseException(string json, DateTimeOffset at)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("exceptionDetails", out var details) || details.ValueKind != JsonValueKind.Object)
                return null;
            var description = details.TryGetProperty("exception", out var exception) && exception.ValueKind == JsonValueKind.Object
                              && exception.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
            var text = description ?? (details.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "");
            var url = details.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            var line = details.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var n) ? n + 1 : 0;
            return new BrowserConsoleEntry("例外", url, line, Clip(text), at);
        }
        catch (JsonException) { return null; }
    }

    /// <summary><c>Runtime.consoleAPICalled</c> の JSON から、error と warning だけを 1 件にする。ほかは null。</summary>
    public static BrowserConsoleEntry? ParseConsole(string json, DateTimeOffset at)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : null;
            var kind = type switch { "error" => "エラー", "warning" => "警告", _ => null };
            if (kind is null)
                return null;
            var parts = new List<string>();
            if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                foreach (var arg in args.EnumerateArray())
                {
                    if (arg.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                        parts.Add(v.GetString() ?? "");
                    else if (arg.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                        parts.Add(desc.GetString() ?? "");
                    else if (arg.TryGetProperty("value", out var raw))
                        parts.Add(raw.ToString());
                }
            }
            var url = "";
            var line = 0;
            if (root.TryGetProperty("stackTrace", out var st) && st.ValueKind == JsonValueKind.Object
                && st.TryGetProperty("callFrames", out var frames) && frames.ValueKind == JsonValueKind.Array
                && frames.GetArrayLength() > 0)
            {
                var first = frames[0];
                url = first.TryGetProperty("url", out var fu) && fu.ValueKind == JsonValueKind.String ? fu.GetString() ?? "" : "";
                line = first.TryGetProperty("lineNumber", out var fl) && fl.TryGetInt32(out var fln) ? fln + 1 : 0;
            }
            return new BrowserConsoleEntry(kind, url, line, Clip(string.Join(" ", parts)), at);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// このページのエラーを読んでよいか（§45.5）。ローカルのページ（localhost・Loomo のファイル・ワークスペースのファイル）だけ。
    /// <c>file://</c> は<b>ワークスペースのフォルダーの中</b>のものだけ（ワークスペース外のファイルは読まない）。
    /// ほかのサイトのページの中身は、サインインしていることがあるので読まない。
    /// </summary>
    public static bool IsLocalPage(string? url, IReadOnlyList<string>? workspaceFolders)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.IsFile)
            return sk0ya.Loomo.Core.Files.WorkspacePaths.Contains(workspaceFolders, uri.LocalPath);
        return uri.Host is "localhost" or "127.0.0.1" or "::1" || uri.Host.EndsWith(".loomo", StringComparison.OrdinalIgnoreCase);
    }

    private static string Clip(string? text)
    {
        text ??= "";
        return text.Length > MaxText ? text[..MaxText] + "…" : text;
    }
}
