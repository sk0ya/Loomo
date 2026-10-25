using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Core.Pty;

/// <summary>書庫に残した、閉じたターミナルの画面 1 つ（§24.72）。</summary>
public sealed record TerminalArchiveEntry(
    string TextPath, Guid SessionId, Guid? WorkspaceId, string? Title, string? WorkingDirectory,
    DateTime ClosedUtc, int LineCount, bool HeadOmitted, int MaskedSecrets);

/// <summary>
/// 閉じたターミナルの画面の書庫（§24.72・iTerm2 3.7.0 の Session archives）。
/// <para>書くのは常駐ホスト（<c>sk0ya.Loomo.Pty.Host</c>）——シェルが終わる瞬間に画面を持っているのはホストだけで、Loomo の端末の
/// コントロールには画面の文字を取り出す公開の口が無い。ホストは終わるセッションの画面を文字にして（<c>HeadlessTerminal.CreatePlainTextSnapshot</c>）
/// 1 セッション 1 組（本文の .txt と、題・場所・時刻の .json）で書き、Loomo は読むだけ。</para>
/// <para>状態ファイル（workspaces.json）には入れない（§23.3.1 の 72MB 事故の形にしない）。1 つは <see cref="MaxChars"/> 字まで（超えたら末尾を残す）、
/// 全体は新しい <see cref="MaxEntries"/> 組まで。書く前に秘密らしい文字列を伏せる（§24.66）。</para>
/// </summary>
public static class TerminalArchive
{
    /// <summary>1 つの上限（文字）。スクロールバックの上限（1 万行）を超えない量。</summary>
    public const int MaxChars = 1_000_000;

    /// <summary>残す組の数。</summary>
    public const int MaxEntries = 40;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>既定の置き場（<c>%APPDATA%\Loomo\terminal-archive</c>）。ホストと Loomo が同じ場所を見る。</summary>
    public static string DefaultDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "terminal-archive");

    private sealed record Meta(Guid SessionId, Guid? WorkspaceId, string? Title, string? WorkingDirectory,
                               DateTime ClosedUtc, int LineCount, bool HeadOmitted, int MaskedSecrets);

    /// <summary>画面を書庫へ書く。中身が空（空白とプロンプトだけ…ではなく、空白だけ）なら書かない。書いたら古いものを間引く。</summary>
    public static TerminalArchiveEntry? Write(
        string directory, Guid sessionId, Guid? workspaceId, string? title, string? workingDirectory,
        string screenText, DateTime closedUtc)
    {
        var text = screenText.Replace("\r\n", "\n").TrimEnd();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var headOmitted = false;
        if (text.Length > MaxChars)
        {
            var cut = text.IndexOf('\n', text.Length - MaxChars);
            text = cut >= 0 ? text[(cut + 1)..] : text[^MaxChars..];
            headOmitted = true;
        }
        text = SecretRedactor.Redact(text, out var masked);
        var lines = text.Count(c => c == '\n') + 1;

        Directory.CreateDirectory(directory);
        var stem = $"{closedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{sessionId:N}";
        var textPath = Path.Combine(directory, stem + ".txt");
        File.WriteAllText(textPath, text.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        var meta = new Meta(sessionId, workspaceId, title, workingDirectory, closedUtc, lines, headOmitted, masked);
        File.WriteAllText(Path.Combine(directory, stem + ".json"), JsonSerializer.Serialize(meta, Json), new UTF8Encoding(false));
        Prune(directory, MaxEntries);
        return ToEntry(textPath, meta);
    }

    /// <summary>書庫の中身を新しい順に。読めない組は飛ばす。</summary>
    public static IReadOnlyList<TerminalArchiveEntry> List(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<TerminalArchiveEntry>();
        var entries = new List<TerminalArchiveEntry>();
        foreach (var json in Directory.EnumerateFiles(directory, "*.json"))
        {
            var textPath = Path.ChangeExtension(json, ".txt");
            if (!File.Exists(textPath))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<Meta>(File.ReadAllText(json), Json) is { } meta)
                    entries.Add(ToEntry(textPath, meta));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
        return entries.OrderByDescending(e => e.ClosedUtc).ToList();
    }

    /// <summary>新しい <paramref name="keep"/> 組だけ残す（名前の先頭が時刻なので、名前の順＝時刻の順）。</summary>
    public static void Prune(string directory, int keep)
    {
        var stems = Directory.EnumerateFiles(directory, "*.txt")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderByDescending(s => s, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();
        foreach (var stem in stems)
        {
            TryDelete(Path.Combine(directory, stem + ".txt"));
            TryDelete(Path.Combine(directory, stem + ".json"));
        }
    }

    /// <summary>一覧の 1 行に出す題（題が無ければ場所のフォルダー名、それも無ければ「ターミナル」）。</summary>
    public static string DisplayTitle(TerminalArchiveEntry entry)
        => entry.Title is { Length: > 0 } title ? title
            : entry.WorkingDirectory is { Length: > 0 } cwd ? Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : cwd
            : "ターミナル";

    /// <summary>エディタで開くときの名前。</summary>
    public static string DocumentName(TerminalArchiveEntry entry)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var title = new string(DisplayTitle(entry).Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        if (title.Length > 40) title = title[..40];
        return $"閉じた画面-{title}-{entry.ClosedUtc.ToLocalTime().ToString("MMdd-HHmm", CultureInfo.InvariantCulture)}.log";
    }

    private static TerminalArchiveEntry ToEntry(string textPath, Meta meta)
        => new(textPath, meta.SessionId, meta.WorkspaceId, meta.Title, meta.WorkingDirectory, meta.ClosedUtc,
            meta.LineCount, meta.HeadOmitted, meta.MaskedSecrets);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
