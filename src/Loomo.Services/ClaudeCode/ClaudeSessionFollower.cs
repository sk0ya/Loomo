using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using sk0ya.Loomo.Core.Harness;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// 動いている Claude Code のセッション1つを<b>追記分だけ読んで</b>追いかける（§39.8 差分読み）。作業中の記録は数秒おきに
/// 伸び、9MB の記録を毎回先頭から読み直すと 1 回 120〜150ms・~20MB の確保になっていた（§39.7）。前回の読み終わりの
/// 位置から先だけを読み、パーサー（<see cref="ClaudeTranscriptReader"/>）・要約・本文はそれぞれ追記分を足すだけで
/// 全体を読んだのと同じになる形で持ち越す。
/// <list type="bullet">
/// <item><b>サブエージェントの記録</b>（<c>&lt;session&gt;/subagents/*.jsonl</c>）も同じように追う。現れた順に足す。</item>
/// <item>読むのは<b>改行で終わった行まで</b>。書きかけの最終行は次の回に回す（途中で切った JSON を壊れた行として捨てない）。</item>
/// <item>どれかのファイルが<b>縮んだ</b>（書き直された）ら、最初から読み直す。</item>
/// </list>
/// <see cref="Refresh"/> は裏のスレッドから呼ばれ、重なりうるので内部で直列にする。
/// </summary>
public sealed class ClaudeSessionFollower
{
    private readonly object _gate = new();
    private readonly string _sessionId;
    private readonly string _subagentFolder;
    private ClaudeTranscriptReader _reader = new();
    private ClaudeSessionCatalog.SummaryAccumulator _summary = new();
    private List<ClaudeReplyText> _replies = new();
    private List<ClaudeSubagentText> _subagentTexts = new();
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _subagentPaths = new();
    private (long Length, DateTime WriteUtc, long SubagentStamp)? _stamp;
    private int _version;
    private ClaudeSessionDigest? _digest;

    public ClaudeSessionFollower(string transcriptPath, string? sessionId = null)
    {
        TranscriptPath = transcriptPath;
        _sessionId = sessionId ?? Path.GetFileNameWithoutExtension(transcriptPath);
        _subagentFolder = Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "", _sessionId, "subagents");
    }

    public string TranscriptPath { get; }

    /// <summary>
    /// 書き足された分を読んで、いまの姿を返す。<c>Version</c> は中身が変わるたびに増える（変わっていなければ前回と同じ値・
    /// 同じ digest）——呼び手は自分が最後に描いた版と比べれば、重なった読み直しのどれが結果を捨てても取りこぼさない。
    /// 記録が無い・読めないときは digest が null。
    /// </summary>
    public (int Version, ClaudeSessionDigest? Digest) Refresh()
    {
        lock (_gate)
        {
            FileInfo info;
            try { info = new FileInfo(TranscriptPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Missing(); }
            if (!info.Exists)
                return Missing();

            var subagents = ListSubagents();
            var stamp = (info.Length, info.LastWriteTimeUtc, subagents.Stamp);
            if (_stamp == stamp && _digest is not null)
                return (_version, _digest);

            try
            {
                if (!ReadAppended(TranscriptPath, isSubagent: false))
                    return RestartAndRead();
                foreach (var path in subagents.Paths)
                {
                    if (!_subagentPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        _subagentPaths.Add(path);
                }
                foreach (var path in _subagentPaths)
                {
                    if (File.Exists(path) && !ReadAppended(path, isSubagent: true))
                        return RestartAndRead();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (_version, _digest);   // 次の回にもう一度（読めたところまでは持ち越している）
            }
            _stamp = stamp;
            _digest = Compose(info.LastWriteTimeUtc);
            _version++;
            return (_version, _digest);
        }
    }

    /// <summary>行を直接食わせる（I/O なし。テストと <see cref="ClaudeSessionDigest.Build"/> 用）。
    /// 本体なら <paramref name="path"/> は <see cref="TranscriptPath"/>。</summary>
    public void Feed(string path, IEnumerable<string> lines, bool isSubagent)
    {
        lock (_gate)
            FeedLines(path, lines as IReadOnlyList<string> ?? lines.ToList(), isSubagent);
    }

    /// <summary>いままで読んだぶんから digest を組む。</summary>
    public ClaudeSessionDigest Compose(DateTime lastWriteUtc)
    {
        lock (_gate)
        {
            var run = _reader.Snapshot(_sessionId, TranscriptPath);
            return ClaudeSessionDigest.Compose(_sessionId, TranscriptPath, lastWriteUtc, run,
                _summary.ToSummary(_sessionId, TranscriptPath, lastWriteUtc), _replies, _subagentTexts);
        }
    }

    private void FeedLines(string path, IReadOnlyList<string> lines, bool isSubagent)
    {
        if (!isSubagent)
        {
            var baseLine = _reader.LinesRead(path);
            for (var i = 0; i < lines.Count; i++)
            {
                _summary.Add(lines[i]);
                if (ClaudeSessionDigest.ReadReply(lines[i], baseLine + i + 1) is { } reply)
                    _replies.Add(reply);
            }
        }
        else
        {
            var agent = SubagentText(path);
            foreach (var line in lines)
            {
                if (ClaudeSessionDigest.ReadReply(line, 0) is { } reply)
                    agent.Replies.Add(reply.Text);
                else if (agent.Prompt is null)
                    ClaudeSessionDigest.ReadSubagentStart(line, agent);
            }
        }
        _reader.Feed(path, lines, isSubagent);
    }

    /// <summary>サブエージェントの記録ごとの読みもの。初めて見たときに名前（meta.json）を読む。</summary>
    private ClaudeSubagentText SubagentText(string path)
    {
        var agent = _subagentTexts.FirstOrDefault(a => string.Equals(a.TranscriptPath, path, StringComparison.OrdinalIgnoreCase));
        if (agent is not null)
            return agent;
        var name = Path.GetFileNameWithoutExtension(path);
        agent = new ClaudeSubagentText
        {
            TranscriptPath = path,
            AgentId = name.StartsWith("agent-", StringComparison.Ordinal) ? name["agent-".Length..] : name,
            Name = ClaudeSessionDigest.ReadSubagentName(Path.ChangeExtension(path, ".meta.json")),
        };
        _subagentTexts.Add(agent);
        return agent;
    }

    /// <summary>前回の読み終わりから、改行で終わった行までを読んで食わせる。ファイルが縮んでいたら false。</summary>
    private bool ReadAppended(string path, bool isSubagent)
    {
        var offset = _offsets.GetValueOrDefault(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length < offset)
            return false;
        if (length == offset)
            return true;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length - offset];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                break;
            read += n;
        }
        var end = Array.LastIndexOf(buffer, (byte)'\n', read - 1 < 0 ? 0 : read - 1);
        if (read == 0 || end < 0)
            return true;   // まだ行が書き終わっていない
        var start = offset == 0 && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        var text = Encoding.UTF8.GetString(buffer, start, end + 1 - start);
        _offsets[path] = offset + end + 1;
        FeedLines(path, SplitLines(text), isSubagent);
        return true;
    }

    /// <summary>改行で終わる文字列を行に分ける（<see cref="StreamReader.ReadLine"/> と同じく \r\n も1つの区切り）。</summary>
    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            var stop = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..stop]);
            start = i + 1;
        }
        if (start < text.Length)
            lines.Add(text[start..].TrimEnd('\r'));
        return lines;
    }

    private (IReadOnlyList<string> Paths, long Stamp) ListSubagents()
    {
        if (!Directory.Exists(_subagentFolder))
            return ([], 0);
        try
        {
            var files = new DirectoryInfo(_subagentFolder).GetFiles("*.jsonl").OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            long stamp = files.Count;
            foreach (var file in files)
                stamp = unchecked(stamp * 31 + (file.Length ^ file.LastWriteTimeUtc.Ticks));
            return (files.Select(f => f.FullName).ToList(), stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], 0);
        }
    }

    private (int, ClaudeSessionDigest?) RestartAndRead()
    {
        Reset();
        // 縮んだのは書き直しなので、もう一度だけ最初から読む（Refresh は lock 済み。Monitor は再入できる）。
        return Refresh();
    }

    private (int, ClaudeSessionDigest?) Missing()
    {
        if (_digest is null && _stamp is null)
            return (_version, null);
        Reset();
        _version++;
        return (_version, null);
    }

    private void Reset()
    {
        _reader = new ClaudeTranscriptReader();
        _summary = new ClaudeSessionCatalog.SummaryAccumulator();
        _replies = new List<ClaudeReplyText>();
        _subagentTexts = new List<ClaudeSubagentText>();
        _offsets.Clear();
        _subagentPaths.Clear();
        _stamp = null;
        _digest = null;
    }
}
