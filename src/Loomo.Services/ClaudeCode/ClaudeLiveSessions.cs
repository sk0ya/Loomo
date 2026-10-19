using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// 動いている Claude Code のプロセス1つ。Claude Code は起動中のプロセスごとに <c>~/.claude/sessions/&lt;pid&gt;.json</c> を
/// 書き（2.1.30x で確認）、いまのセッション ID・場所・開始時刻を持つ。<c>/clear</c> で新しいセッションになれば
/// セッション ID も書き換わる。
/// </summary>
public sealed record ClaudeLiveSession(int Pid, string SessionId, string Cwd, DateTimeOffset? StartedAt);

/// <summary>ターミナルのタブ1つ分の手がかり。</summary>
/// <param name="WorkingDirectory">タブの場所（cwd）。</param>
/// <param name="ClaudeStartedUtc">そのタブで <c>claude</c> を打った時刻（OSC 133 の C）。常駐の復元直後などは分からない（null）。</param>
public sealed record ClaudeTabClue(string? WorkingDirectory, DateTime? ClaudeStartedUtc);

/// <summary>タブとセッションの結び付けの結果。<see cref="Session"/> が null なら結び付けられなかった（<see cref="Reason"/>）。</summary>
public sealed record ClaudeSessionMatch(ClaudeLiveSession? Session, string? Reason);

/// <summary>
/// ターミナルのタブで動いている Claude Code が<b>どのセッションか</b>を確かめる（§39.4.2）。
/// <para>SessionStart のフック（§38.4）が届いていればタブ ID 付きで確定するので、これはフックの無いタブのためのもの。
/// 以前は「その場所の記録フォルダーでいちばん新しい記録」を当て推量していたが、同じフォルダーで Loomo の外
/// （別の端末）でも claude を回していると、そちらの会話を出していた——<b>推量はしない</b>。</para>
/// <para>確かめ方：動いている claude のプロセス（<c>sessions/&lt;pid&gt;.json</c>）のうち、(1) <b>この Loomo の端末の中</b>で
/// 動いている（親をたどると Pty.Host か Loomo 自身に行き着く）、(2) 場所がタブと同じ、(3) ほかのタブに結び付いて
/// いない、ものだけを候補にする。1つなら確定。複数ならタブで claude を打った時刻とセッションの開始時刻で選び、
/// それでも決まらなければ<b>結び付けない</b>（別の会話を出すより、出さないほうがよい）。</para>
/// </summary>
public static class ClaudeLiveSessions
{
    /// <summary>claude を打った時刻とセッションの開始時刻の許容差。プロセスの起動に数秒かかる。</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(15);

    /// <summary>Loomo の端末の親プロセス（常駐の Pty.Host）の実行ファイル名（拡張子なし）。</summary>
    public const string PtyHostProcessName = "sk0ya.Loomo.Pty.Host";

    public static string DefaultSessionsFolder()
        => Path.Combine(Path.GetDirectoryName(ClaudeTranscriptLocator.DefaultProjectsRoot())!, "sessions");

    /// <summary><c>sessions/*.json</c> を読む（読めないもの・壊れたものは飛ばす）。</summary>
    public static IReadOnlyList<ClaudeLiveSession> Read(string? sessionsFolder = null)
    {
        var folder = sessionsFolder ?? DefaultSessionsFolder();
        if (!Directory.Exists(folder))
            return [];
        var result = new List<ClaudeLiveSession>();
        string[] files;
        try { files = Directory.GetFiles(folder, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        foreach (var file in files)
        {
            try
            {
                if (Parse(File.ReadAllText(file)) is { } session)
                    result.Add(session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>1ファイルの中身を読む（対話のセッションだけ。バックグラウンドのサービス等は対象外）。</summary>
    public static ClaudeLiveSession? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() != "interactive")
                return null;
            if (!root.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var pidValue)
                || !root.TryGetProperty("sessionId", out var id) || id.GetString() is not { Length: > 0 } sessionId
                || !root.TryGetProperty("cwd", out var cwd) || cwd.GetString() is not { Length: > 0 } cwdValue)
                return null;
            DateTimeOffset? started = root.TryGetProperty("startedAt", out var s) && s.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            return new ClaudeLiveSession(pidValue, sessionId, cwdValue, started);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// タブのセッションを決める（I/O なし。テストは値で書ける）。
    /// </summary>
    /// <param name="sessions">動いている claude（<see cref="Read"/>）。</param>
    /// <param name="insideLoomo">その PID が Loomo の端末の中で動いているか（<see cref="IsInsideLoomoTerminal"/>）。</param>
    /// <param name="tab">タブの手がかり。</param>
    /// <param name="claimedByOtherTabs">ほかのタブに結び付いているセッション ID（フックで確定したもの）。</param>
    public static ClaudeSessionMatch Match(
        IReadOnlyList<ClaudeLiveSession> sessions,
        Func<int, bool> insideLoomo,
        ClaudeTabClue tab,
        IReadOnlySet<string> claimedByOtherTabs)
    {
        if (tab.WorkingDirectory is not { Length: > 0 } cwd)
            return new(null, "タブの場所が分からないので、どのセッションか確かめられません。");
        var candidates = sessions
            .Where(s => SamePath(s.Cwd, cwd) && !claimedByOtherTabs.Contains(s.SessionId) && insideLoomo(s.Pid))
            .ToList();
        if (candidates.Count == 0)
            return new(null, "このタブの Claude Code のセッションが見つかりません。");
        if (candidates.Count == 1)
            return new(candidates[0], null);

        // 同じ場所で、Loomo の端末の中に claude が複数。claude を打った時刻で選ぶ。
        if (tab.ClaudeStartedUtc is { } typed)
        {
            var near = candidates
                .Where(s => s.StartedAt is { } at && (at.UtcDateTime - typed).Duration() <= StartTolerance)
                .ToList();
            if (near.Count == 1)
                return new(near[0], null);
        }
        return new(null, $"同じ場所で Claude Code が {candidates.Count} つ動いていて、このタブのセッションを特定できません。"
            + "パレットの「Claude Code のフックを設定」を入れると、次に起動する claude から確実に結び付きます。");
    }

    /// <summary>セッションの記録ファイル（<c>projects/&lt;slug(cwd)&gt;/&lt;sessionId&gt;.jsonl</c>）。</summary>
    public static string TranscriptPath(ClaudeLiveSession session, string? projectsRoot = null)
        => Path.Combine(projectsRoot ?? ClaudeTranscriptLocator.DefaultProjectsRoot(),
            ClaudeTranscriptLocator.Slug(session.Cwd), session.SessionId + ".jsonl");

    private static bool SamePath(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// その PID が Loomo の端末の中で動いているか：親をたどって Pty.Host（常駐の端末）か、Loomo 自身
    /// （<paramref name="loomoPid"/>、常駐でない端末）に行き着くか。プロセスの一覧は呼び出し側が1回撮って渡す
    /// （<see cref="SnapshotParents"/>）。親の PID は再利用されうるので、深さに上限を設け、輪になったら止める。
    /// </summary>
    public static bool IsInsideLoomoTerminal(int pid, IReadOnlyDictionary<int, (int Parent, string Name)> processes, int loomoPid)
    {
        var seen = new HashSet<int>();
        var current = pid;
        for (var depth = 0; depth < 32 && seen.Add(current); depth++)
        {
            if (!processes.TryGetValue(current, out var entry))
                return false;
            if (current != pid && (current == loomoPid
                || Path.GetFileNameWithoutExtension(entry.Name).Equals(PtyHostProcessName, StringComparison.OrdinalIgnoreCase)))
                return true;
            current = entry.Parent;
        }
        return false;
    }

    /// <summary>いま動いているプロセスの「PID → (親の PID, 実行ファイル名)」。</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyDictionary<int, (int Parent, string Name)> SnapshotParents()
    {
        var result = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandle)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                return result;
            do result[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
