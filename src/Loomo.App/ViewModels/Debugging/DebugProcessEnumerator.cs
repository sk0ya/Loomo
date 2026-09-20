using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>アタッチ候補となる実行中プロセスの 1 行。<see cref="IsManaged"/> は coreclr 等のロードを検出できたか
/// （検出できなければ未管理扱い。アクセス権限不足や 32bit 不一致で判定不能なものも未管理側になる）。</summary>
public sealed class DebugProcessViewModel
{
    public DebugProcessViewModel(int pid, string name, string? title, bool isManaged)
    {
        Pid = pid;
        Name = name;
        Title = title;
        IsManaged = isManaged;
    }

    public int Pid { get; }
    public string Name { get; }
    public string? Title { get; }
    public bool IsManaged { get; }

    /// <summary>リスト表示用（名前 (PID) — ウィンドウタイトル）。</summary>
    public string Display => string.IsNullOrEmpty(Title) ? $"{Name} ({Pid})" : $"{Name} ({Pid}) — {Title}";
}

/// <summary>実行中プロセスを列挙し、coreclr 等のロード有無で .NET 判定を付けるユーティリティ。
/// 判定（モジュール検査）が重いので呼び出し側はバックグラウンドで回す。</summary>
internal static class DebugProcessEnumerator
{
    /// <summary>全プロセスを列挙し、coreclr 等のロード有無で .NET 判定を付ける。判定不能なものは未管理扱い。</summary>
    public static List<DebugProcessViewModel> Enumerate()
    {
        var self = Environment.ProcessId;
        var list = new List<DebugProcessViewModel>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.Id == 0) continue;  // 自分自身と Idle は除外
                string? title = null;
                try { title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? null : p.MainWindowTitle; }
                catch { /* タイトル取得不能 */ }
                list.Add(new DebugProcessViewModel(p.Id, p.ProcessName, title, IsManaged(p)));
            }
            catch { /* 列挙中に終了した等。スキップ */ }
            finally { p.Dispose(); }
        }
        return list
            .OrderByDescending(i => i.IsManaged)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Pid)
            .ToList();
    }

    /// <summary>プロセスに coreclr/clr がロードされているかで .NET 実行中かを推定する。
    /// アクセス権限不足・ビット不一致では例外になり判定不能（false）。netcoredbg でアタッチ可能なのも
    /// おおむね検査できるプロセスに限られるため、この best-effort で十分。</summary>
    private static bool IsManaged(Process p)
    {
        try
        {
            foreach (ProcessModule m in p.Modules)
            {
                var n = m.ModuleName;
                if (n.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("hostpolicy.dll", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* 判定不能 */ }
        return false;
    }
}
