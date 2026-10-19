using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Core.Harness;

/// <summary>
/// 記録を<b>追記分ずつ</b>読む読み手（§39.8 差分読み）。<see cref="ClaudeTranscriptParser.Parse"/> と同じ読み方で、
/// 行を食わせ続けられる——作業中の Claude の記録は数秒おきに伸びるので、毎回先頭から読み直すと記録の長さに比例して
/// 重くなる。追記を跨いで持ち越すものは三つ：結果を待っているツール呼び出し（ファイルごと）、usage の
/// <c>message.id</c> ごとの重複除去、行番号。
/// <para>ファイルは食わせた順に並ぶ（本体が先、サブエージェントは初めて現れた順）。<see cref="Snapshot"/> の
/// ツール呼び出しの通し番号もこの順で、まとめて読んだ <see cref="ClaudeTranscriptParser.Parse"/> と一致する。</para>
/// スレッド安全ではない（読み手 1 つを 1 か所から使う）。
/// </summary>
public sealed class ClaudeTranscriptReader
{
    private readonly ClaudeTranscriptParser.ParseState _state = new();
    private readonly List<FileCursor> _files = new();

    /// <summary><paramref name="path"/> に書き足された行を読む。行番号はこのファイルでの続きから数える。</summary>
    public void Feed(string path, IEnumerable<string> lines, bool isSubagent)
    {
        var file = _files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            file = new FileCursor(path, isSubagent);
            _files.Add(file);
        }
        foreach (var line in lines)
        {
            file.Lines++;
            ClaudeTranscriptParser.ReadLine(_state, file.Pending, file.Calls, path, line, file.Lines, isSubagent);
        }
    }

    /// <summary><paramref name="path"/> をこれまでに何行読んだか（まだなら 0）。</summary>
    public int LinesRead(string path)
        => _files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))?.Lines ?? 0;

    /// <summary>いままで読んだぶんの Run。読み手はこの後も伸びるが、返した Run は変わらない。</summary>
    public HarnessRun Snapshot(string runId, string transcriptPath)
        => ClaudeTranscriptParser.Build(runId, transcriptPath, _state,
            _files.SelectMany(f => f.Calls), _files.Count(f => f.IsSubagent));

    private sealed class FileCursor(string path, bool isSubagent)
    {
        public string Path { get; } = path;
        public bool IsSubagent { get; } = isSubagent;
        public int Lines;
        public readonly Dictionary<string, ClaudeTranscriptParser.CallBuilder> Pending = new(StringComparer.Ordinal);
        public readonly List<ClaudeTranscriptParser.CallBuilder> Calls = new();
    }
}
