using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>Claude Code から見た「IDE」としての Loomo（§38）。<see cref="ClaudeIdeServer"/> が受けたツール呼び出しを
/// ここへ渡す。実装は App（エディタ・診断・差分を持つ側）で、<b>どの呼び出しもサーバーのスレッドから来る</b>ので
/// UI へは実装側が渡す。行・列はすべて 0 始まり。</summary>
public interface IClaudeIdeHost
{
    /// <summary>今アクティブなエディタの選択（選択が無ければキャレット位置の空の選択）。エディタが無ければ null。</summary>
    Task<ClaudeIdeSelection?> GetCurrentSelectionAsync();

    Task<IReadOnlyList<ClaudeIdeEditor>> GetOpenEditorsAsync();

    /// <summary>開いているファイルの診断。<paramref name="filePath"/> を渡せばそのファイルだけ。</summary>
    Task<IReadOnlyList<ClaudeIdeFileDiagnostics>> GetDiagnosticsAsync(string? filePath);

    /// <summary>ファイルを開く。<paramref name="startText"/> があれば、その文字列（から <paramref name="endText"/> まで）を選ぶ。</summary>
    Task OpenFileAsync(string filePath, string? startText, string? endText);

    /// <summary>未保存の編集があるか。開いていなければ null。</summary>
    Task<bool?> IsDirtyAsync(string filePath);

    /// <summary>開いているタブを保存する。開いていなければ false。</summary>
    Task<bool> SaveAsync(string filePath);

    /// <summary>Claude の提案（<paramref name="newContents"/>）を差分で見せ、人間が受け入れるか却下するまで待つ。
    /// <paramref name="cancellationToken"/> は Claude 側がタブを閉じた（ターミナルで答えた）合図で、そのときは
    /// 表示を片付けて却下として返す。受け入れてもファイルは書かない——書くのは Claude。</summary>
    Task<ClaudeIdeDiffDecision> ShowProposedEditAsync(
        string tabName, string filePath, string newContents, CancellationToken cancellationToken);
}

public sealed record ClaudeIdeSelection(
    string FilePath, string Text, int StartLine, int StartCharacter, int EndLine, int EndCharacter)
{
    public bool IsEmpty => StartLine == EndLine && StartCharacter == EndCharacter;
}

public sealed record ClaudeIdeEditor(string FilePath, bool IsActive, bool IsDirty);

public sealed record ClaudeIdeDiagnostic(
    string Message, ClaudeIdeSeverity Severity,
    int StartLine, int StartCharacter, int EndLine, int EndCharacter,
    string? Source, string? Code);

public sealed record ClaudeIdeFileDiagnostics(string FilePath, IReadOnlyList<ClaudeIdeDiagnostic> Diagnostics);

public enum ClaudeIdeSeverity { Error, Warning, Info, Hint }

/// <summary>差分の結論。受け入れたときは人間が手を入れた後の最終本文も返す（そのまま Claude が書く）。</summary>
public sealed record ClaudeIdeDiffDecision(bool Accepted, string? FinalContents)
{
    public static ClaudeIdeDiffDecision Rejected { get; } = new(false, null);
}
