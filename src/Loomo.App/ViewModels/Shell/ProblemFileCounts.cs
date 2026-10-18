using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>1ファイル（またはフォルダーの配下全体）のエラーと警告の数（§24.33）。情報・ヒントは数えない
/// ——ツリーとタブに出す印は「直す必要があるか」を周辺視野で知らせるためのもので、ヒントまで出すと全部に付く。</summary>
public sealed record ProblemMark(int Errors, int Warnings)
{
    public bool HasErrors => Errors > 0;

    /// <summary>印に出す数。エラーがあればエラーの数、無ければ警告の数。</summary>
    public string Text => (Errors > 0 ? Errors : Warnings).ToString();

    public string ToolTip => Errors > 0 && Warnings > 0
        ? $"エラー {Errors} 件・警告 {Warnings} 件"
        : Errors > 0 ? $"エラー {Errors} 件" : $"警告 {Warnings} 件";
}

/// <summary>
/// 問題の数をパスで引く索引（§24.33）。ファイルツリーの行とタブの一覧が印を出すのに使う。
/// フォルダーは配下の合計で引ける（畳んだフォルダーにも「この中に壊れているものがある」を出す）。
/// IDE ペイン（.NET）と TS IDE ペインの Problems は同じ LSP の診断をそれぞれ持っているので、
/// 合わせるときは同じ位置・同じコード・同じ本文のものを 1 件に数える。
/// </summary>
public sealed class ProblemFileCounts
{
    public static readonly ProblemFileCounts Empty = new(new Dictionary<string, ProblemMark>(StringComparer.OrdinalIgnoreCase));

    // ファイルとフォルダーの両方の鍵を持つ（フォルダーは配下の合計）。鍵は末尾の区切り無しの絶対パス。
    private readonly Dictionary<string, ProblemMark> _marks;

    private ProblemFileCounts(Dictionary<string, ProblemMark> marks) => _marks = marks;

    public int Count => _marks.Count;

    public static ProblemFileCounts From(IEnumerable<ProblemItemViewModel> problems)
    {
        var perFile = new Dictionary<string, (int Errors, int Warnings)>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var problem in problems)
        {
            if (problem.Severity is not (ProblemSeverity.Error or ProblemSeverity.Warning)) continue;
            if (Normalize(problem.FilePath) is not { } path) continue;
            if (!seen.Add($"{path}|{problem.Line1}|{problem.Column1}|{problem.Code}|{problem.Message}")) continue;
            var (errors, warnings) = perFile.GetValueOrDefault(path);
            perFile[path] = problem.Severity == ProblemSeverity.Error ? (errors + 1, warnings) : (errors, warnings + 1);
        }
        if (perFile.Count == 0) return Empty;

        var marks = new Dictionary<string, ProblemMark>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, (errors, warnings)) in perFile)
        {
            marks[file] = new ProblemMark(errors, warnings);
            // 親フォルダーへ足し上げる（ドライブの根まで。浅いので回数は知れている）。
            for (var dir = Path.GetDirectoryName(file); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                var key = Path.TrimEndingDirectorySeparator(dir);
                var sum = marks.GetValueOrDefault(key);
                marks[key] = new ProblemMark((sum?.Errors ?? 0) + errors, (sum?.Warnings ?? 0) + warnings);
            }
        }
        return new ProblemFileCounts(marks);
    }

    /// <summary>ファイルかフォルダーの印。問題が無ければ null。</summary>
    public ProblemMark? For(string? path)
        => Normalize(path) is { } key ? _marks.GetValueOrDefault(key) : null;

    /// <summary>中身が同じか（同じなら UI を触らない——診断はキー入力ごとに届くので、毎回ツリーを歩かない）。</summary>
    public bool SameAs(ProblemFileCounts other)
        => ReferenceEquals(this, other)
            || (_marks.Count == other._marks.Count
                && _marks.All(kv => other._marks.TryGetValue(kv.Key, out var mark) && mark == kv.Value));

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
