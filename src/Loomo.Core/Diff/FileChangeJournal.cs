using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.Core.Diff;

/// <summary>
/// エージェントのファイル変更ツール（write_file / edit_file）による1回の変更記録。
/// 実行前後の全文を保持し、Diff セッションが差分表示・巻き戻しに使う。
/// </summary>
/// <param name="Path">canonical な絶対パス（<c>IFileMutationTool.ResolveTargetPath</c> の結果）。</param>
/// <param name="IsNew">実行前にファイルが存在しなかった（新規作成）。</param>
/// <param name="OldContent">実行前の全文。新規作成・巨大ファイル・読取失敗は null。</param>
/// <param name="NewContent">実行後の全文。巨大ファイル・読取失敗は null。</param>
public sealed record FileChangeRecord(
    DateTimeOffset Ts,
    string SessionId,
    string TurnId,
    string ToolName,
    string Path,
    bool IsNew,
    string? OldContent,
    string? NewContent);

/// <summary>
/// AI のファイル変更を記録するインメモリジャーナル。<see cref="Agent.AgentOrchestrator"/> が
/// ツール成功時に書き込み、Diff セッションが読む。プロセス内のみ（永続化しない）。
/// </summary>
public interface IFileChangeJournal
{
    /// <summary>記録が増減したとき。UI スレッドとは限らないので購読側でディスパッチすること。</summary>
    event EventHandler? Changed;

    /// <summary>現在の記録（古い順）のコピーを返す。</summary>
    IReadOnlyList<FileChangeRecord> Snapshot();

    void Record(FileChangeRecord record);

    /// <summary>指定パスの記録を消す（巻き戻し後に「変更済み」表示を残さないため）。</summary>
    void RemoveForPath(string path);

    void Clear();
}

public sealed class FileChangeJournal : IFileChangeJournal
{
    /// <summary>全文スナップショットを保持するため記録数に上限を設ける（古いものから捨てる）。</summary>
    private const int MaxRecords = 500;

    /// <summary>これを超えるファイルは全文を保持しない（差分表示不可・記録自体は残す）。</summary>
    public const long MaxContentBytes = 2_000_000;

    private readonly object _gate = new();
    private readonly List<FileChangeRecord> _records = new();

    public event EventHandler? Changed;

    public IReadOnlyList<FileChangeRecord> Snapshot()
    {
        lock (_gate)
            return _records.ToList();
    }

    public void Record(FileChangeRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count > MaxRecords)
                _records.RemoveRange(0, _records.Count - MaxRecords);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveForPath(string path)
    {
        bool removed;
        lock (_gate)
            removed = _records.RemoveAll(r =>
                string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        bool removed;
        lock (_gate)
        {
            removed = _records.Count > 0;
            _records.Clear();
        }
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// ジャーナル用にファイル全文を読む。(存在したか, 内容) を返し、
    /// 不在・巨大（<see cref="MaxContentBytes"/> 超）・読取失敗は内容 null。例外は投げない。
    /// </summary>
    public static (bool Exists, string? Content) SafeReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (false, null);
            if (info.Length > MaxContentBytes) return (true, null);
            return (true, File.ReadAllText(path));
        }
        catch
        {
            return (false, null);
        }
    }
}
