using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>引き継ぎの結果（§24.48）。</summary>
public sealed record GitWorktreeCarryResult(
    IReadOnlyList<string> Linked, IReadOnlyList<string> Copied, IReadOnlyList<string> Skipped, IReadOnlyList<string> Failed)
{
    public static readonly GitWorktreeCarryResult Empty = new([], [], [], []);

    public bool IsEmpty => Linked.Count + Copied.Count + Skipped.Count + Failed.Count == 0;
}

/// <summary>
/// 新しいワークツリーへ、Git に載らないもの（<c>node_modules</c>・<c>.env</c>・<c>appsettings.Development.json</c> など）を
/// 引き継ぐ（§24.48）。フォルダーは<b>ジャンクション</b>で置き（依存関係を入れ直さず、大きな成果物を複製しない。
/// 管理者権限も開発者モードも要らない）、ファイルは<b>コピー</b>する（設定はワークツリーごとに書き換えたいことが多い）。
/// <para>
/// <b>消すときの危険</b>：ジャンクションを残したままワークツリーごと消すと、消す側がリンクの先へ降りて
/// 元のワークツリーの中身まで消しかねない。作ったジャンクションはワークツリーの git ディレクトリ
/// （<c>.git/worktrees/&lt;名前&gt;/loomo-carried</c>）に書き残し、消す前に <see cref="Unlink"/> でリンクだけ外す。
/// </para>
/// </summary>
public static class GitWorktreeCarryOver
{
    /// <summary>覚えておく場所（リポジトリ共通の設定。どのワークツリーから作っても同じ値が出る）。</summary>
    public const string ConfigKey = "loomo.worktreecarry";

    /// <summary>作ったジャンクションの控え（ワークツリーの git ディレクトリの中）。</summary>
    public const string RecordFileName = "loomo-carried";

    /// <summary>入力（<c>;</c>・改行区切り）を、ワークツリーの中の相対パスの並びへ。外へ出るもの・絶対パス・<c>.git</c> は捨てる。</summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<string>();
        foreach (var raw in text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim().Replace('/', '\\').Trim('\\');
            if (entry.Length == 0 || Path.IsPathRooted(entry) || entry.Contains(':'))
                continue;
            var segments = entry.Split('\\');
            if (segments.Any(s => s is "" or "." or "..")
                || segments[0].Equals(".git", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!result.Contains(entry, StringComparer.OrdinalIgnoreCase))
                result.Add(entry);
        }
        return result;
    }

    /// <summary>設定へ書く形（<see cref="Parse"/> の逆）。</summary>
    public static string Format(IEnumerable<string> entries) => string.Join("; ", entries);

    /// <summary>引き継ぐ。元に無いもの・新しいワークツリーに既にあるもの（Git に載っているもの）は飛ばす。</summary>
    /// <param name="createJunction">ジャンクションを作る手段（テストで差し替える）。null なら <c>mklink /J</c>。</param>
    public static GitWorktreeCarryResult Apply(
        string sourceRoot, string targetRoot, IReadOnlyList<string> entries, string? recordFile,
        Func<string, string, bool>? createJunction = null)
    {
        if (entries.Count == 0) return GitWorktreeCarryResult.Empty;
        createJunction ??= CreateJunction;
        List<string> linked = [], copied = [], skipped = [], failed = [];
        foreach (var entry in entries)
        {
            var source = Path.Combine(sourceRoot, entry);
            var target = Path.Combine(targetRoot, entry);
            try
            {
                if (Directory.Exists(target) || File.Exists(target) || (!Directory.Exists(source) && !File.Exists(source)))
                {
                    skipped.Add(entry);
                    continue;
                }
                if (Path.GetDirectoryName(target) is { } parent)
                    Directory.CreateDirectory(parent);
                if (Directory.Exists(source))
                {
                    if (createJunction(target, source)) linked.Add(entry);
                    else failed.Add(entry);
                }
                else
                {
                    File.Copy(source, target);
                    copied.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(entry);
            }
        }
        if (linked.Count > 0 && recordFile is not null)
        {
            try { File.AppendAllLines(recordFile, linked); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 控えが無ければ外すときに探すだけ */ }
        }
        return new GitWorktreeCarryResult(linked, copied, skipped, failed);
    }

    /// <summary>消す前に、作ったジャンクションだけを外す（リンクの先には触らない）。外せた数。</summary>
    public static int Unlink(string worktreeRoot, string? recordFile)
    {
        if (recordFile is null || !File.Exists(recordFile)) return 0;
        var count = 0;
        string[] entries;
        try { entries = File.ReadAllLines(recordFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        foreach (var entry in Parse(string.Join(";", entries)))
        {
            var path = Path.Combine(worktreeRoot, entry);
            try
            {
                var info = new DirectoryInfo(path);
                // 控えにあっても、いまジャンクション（再解析ポイント）でないものは消さない——人が本物に置き換えたのかもしれない。
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    info.Delete(recursive: false);   // リンクだけ消える（先の中身はそのまま）
                    count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return count;
    }

    /// <summary><c>cmd /c mklink /J</c>。.NET にはジャンクションを作る API が無い（シンボリックリンクは権限が要る）。</summary>
    private static bool CreateJunction(string link, string target)
    {
        try
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                return false;
            }
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
