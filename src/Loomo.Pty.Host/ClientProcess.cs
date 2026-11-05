using System.ComponentModel;
using System.Diagnostics;

namespace sk0ya.Loomo.Pty.Host;

/// <summary>
/// ホストに話しかけた Loomo のプロセス。「まだ生きているか」を PID の数字ではなく、そのプロセスそのものについて答える。
/// <para>
/// <see cref="Process.GetProcessById(int)"/> はハンドルを開かず PID を覚えるだけで、<see cref="Process.HasExited"/> を
/// 聞かれたときに初めてその PID で開く。その間に Loomo が Leave を言わずに消え（落ちた・kill された）、PID が別の
/// プロセスへ再利用されると、別人を開いて「生きている」と答える。実際に Windows Terminal の OpenConsole.exe が
/// 落ちた Loomo の PID を引き継ぎ、どの Loomo が正常終了しても「ほかの Loomo が生きている」ことになって、
/// ホストが4日間入れ替わらなかった（2026-11-06）。
/// </para>
/// <para>
/// なので覚えた時点でハンドルを開く。ハンドルを握っている間は、Windows はその PID を再利用しない。開けないとき
/// （権限の違う Loomo など）は起動時刻を控え、問われたときに同じ PID のプロセスの起動時刻と照らす。
/// どちらも分からないときは生きている側に倒す（間違えて殺すほうが、ホストが残るより悪い）。
/// </para>
/// </summary>
internal sealed class ClientProcess : IDisposable
{
    private readonly Process? _process;
    private readonly DateTime? _startTime;

    private ClientProcess(int id, Process? process, DateTime? startTime)
    {
        Id = id;
        _process = process;
        _startTime = startTime;
    }

    public int Id { get; }

    /// <summary>PID のプロセスを掴む。もういなければ null。</summary>
    public static ClientProcess? TryOpen(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null; // もういない。
        }

        try
        {
            _ = process.SafeHandle; // ここで開く。以後 HasExited はこのハンドルに聞く。
            return new ClientProcess(processId, process, startTime: null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            var startTime = StartTimeOf(process);
            process.Dispose();
            return new ClientProcess(processId, process: null, startTime);
        }
    }

    /// <summary>ハンドルを持たず、PID と起動時刻だけで見張る（開けなかったときの道・テスト用）。</summary>
    internal static ClientProcess FromIdentity(int processId, DateTime? startTime) => new(processId, null, startTime);

    public bool HasExited
    {
        get
        {
            if (_process is not null)
            {
                try { return _process.HasExited; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return true; }
            }

            Process current;
            try
            {
                current = Process.GetProcessById(Id);
            }
            catch (ArgumentException)
            {
                return true;
            }

            using (current)
            {
                // 起動時刻が違えば、同じ PID の別のプロセス（再利用）。読めなければ生きている側に倒す。
                return _startTime is { } expected && StartTimeOf(current) is { } actual && actual != expected;
            }
        }
    }

    private static DateTime? StartTimeOf(Process process)
    {
        try { return process.StartTime; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }

    public void Dispose() => _process?.Dispose();
}
