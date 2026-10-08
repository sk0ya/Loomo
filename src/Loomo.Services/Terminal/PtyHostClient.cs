using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Pty;
using Terminal.Sessions;

namespace sk0ya.Loomo.Services.Terminal;

/// <summary>
/// 端末の常駐ホスト（<c>sk0ya.Loomo.Pty.Host</c>）の窓口（設計 §34）。必要になったときにホストを起こし、
/// セッションの作成・再接続・一覧・削除をパイプ越しに頼む。
///
/// <para>ホストは本体の <c>bin\ptyhost\</c> から直接は起こさない。常駐して DLL を掴み続けるので、
/// 以後のビルドが全部落ちる。<c>%LOCALAPPDATA%\Loomo\ptyhost\&lt;内容ハッシュ&gt;\</c> へ写してから起こす
/// （§34.5）。版の違うホストがセッションを抱えて生きていても、パイプ名の版が同じならそちらに繋ぐ。</para>
/// </summary>
public sealed class PtyHostClient
{
    private const string HostExecutableName = "sk0ya.Loomo.Pty.Host.exe";
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private readonly string _pipeName;
    private readonly string _bundledDirectory;
    private readonly string _installRoot;
    private readonly object _launchGate = new();

    public PtyHostClient()
        : this(
            PtyProtocol.PipeName(),
            Path.Combine(AppContext.BaseDirectory, "ptyhost"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "ptyhost"))
    {
    }

    internal PtyHostClient(string pipeName, string bundledDirectory, string installRoot)
    {
        _pipeName = pipeName;
        _bundledDirectory = bundledDirectory;
        _installRoot = installRoot;
    }

    /// <summary>ホスト一式が同梱されているか（無ければ常駐はせず、ビューは自前の ConPTY を使う）。</summary>
    public bool IsAvailable => File.Exists(Path.Combine(_bundledDirectory, HostExecutableName));

    /// <summary>
    /// <paramref name="sessionId"/> のシェルに繋ぐ。ホストに生きていればそれに、無ければ作って繋ぐ。
    /// 端末ビューのバックグラウンドスレッドから呼ばれる（<c>TerminalTabView.SessionFactory</c>）。
    /// </summary>
    public RemotePtySession OpenSession(Guid sessionId, Guid? workspaceId, TerminalSessionRequest request)
    {
        var pipe = Connect(launchIfMissing: true, OpenTimeout)
            ?? throw new InvalidOperationException("端末の常駐ホストに接続できませんでした。");
        try
        {
            var environment = request.EnvironmentVariables.ToDictionary(
                pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            PtyProtocol.WriteJson(pipe, PtyProtocol.FrameType.Request, new PtyProtocol.Request(
                PtyProtocol.Operation.Open, sessionId, workspaceId, request.LaunchCommandLine,
                request.WorkingDirectory, request.Columns, request.Rows, request.ScrollbackLimit, environment));
            var response = ReadResponse(pipe);
            if (response.InUse)
                throw new PtySessionInUseException(sessionId);
            return new RemotePtySession(pipe, response.Created);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>ホストが抱えているセッション。ホストが起きていなければ空（一覧のためだけに起こさない）。</summary>
    public Task<IReadOnlyList<PtyProtocol.SessionInfo>> ListAsync() => Task.Run<IReadOnlyList<PtyProtocol.SessionInfo>>(() =>
        Ask(new PtyProtocol.Request(PtyProtocol.Operation.List))?.Sessions ?? new List<PtyProtocol.SessionInfo>());

    public Task KillAsync(Guid sessionId) =>
        Task.Run(() => Ask(new PtyProtocol.Request(PtyProtocol.Operation.Kill, sessionId)));

    /// <summary>ワークスペースを消したら、そこのシェルも残さない。</summary>
    public Task KillWorkspaceAsync(Guid workspaceId) =>
        Task.Run(() => Ask(new PtyProtocol.Request(PtyProtocol.Operation.KillWorkspace, WorkspaceId: workspaceId)));

    private PtyProtocol.Response? Ask(PtyProtocol.Request request)
    {
        try
        {
            using var pipe = Connect(launchIfMissing: false, TimeSpan.FromMilliseconds(500));
            if (pipe is null)
                return null;
            PtyProtocol.WriteJson(pipe, PtyProtocol.FrameType.Request, request);
            return ReadResponse(pipe);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TimeoutException)
        {
            return null;
        }
    }

    private static PtyProtocol.Response ReadResponse(Stream pipe)
    {
        var frame = PtyProtocol.ReadFrame(pipe)
            ?? throw new IOException("端末の常駐ホストが応答せずに切断しました。");
        return frame.Type switch
        {
            PtyProtocol.FrameType.Response => PtyProtocol.ReadJson<PtyProtocol.Response>(frame.Payload),
            PtyProtocol.FrameType.Error => throw new InvalidOperationException(Encoding.UTF8.GetString(frame.Payload)),
            _ => throw new InvalidDataException($"想定外の応答です: {frame.Type}"),
        };
    }

    private NamedPipeClientStream? Connect(bool launchIfMissing, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        if (TryConnect(TimeSpan.FromMilliseconds(300)) is { } first)
            return first;
        if (!launchIfMissing)
            return null;

        lock (_launchGate)
        {
            // 待っている間に別のタブが起こしているかもしれない。
            if (TryConnect(TimeSpan.FromMilliseconds(100)) is { } raced)
                return raced;
            LaunchHost();
        }

        while (DateTime.UtcNow < deadline)
        {
            if (TryConnect(TimeSpan.FromMilliseconds(250)) is { } pipe)
                return pipe;
            Thread.Sleep(50);
        }

        return null;
    }

    private NamedPipeClientStream? TryConnect(TimeSpan timeout)
    {
        // Asynchronous（overlapped）は必須：同期ハンドルは読みと書きを直列にするので、読み取りスレッドが
        // 待っている間は入力もリサイズも送れなくなる（ホスト側と同じ理由）。
        var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect((int)timeout.TotalMilliseconds);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            return null;
        }
    }

    private void LaunchHost()
    {
        var installed = Install();
        StartDetached(Path.Combine(installed, HostExecutableName), _installRoot);
        RemoveStaleInstalls(installed);
    }

    /// <summary>同梱のホスト一式を内容ハッシュのフォルダーへ写す。既にあれば写さない。</summary>
    internal string Install()
    {
        var files = Directory.GetFiles(_bundledDirectory)
            .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file).ToLowerInvariant()));
            hash.AppendData(File.ReadAllBytes(file));
        }

        var target = Path.Combine(_installRoot, Convert.ToHexString(hash.GetHashAndReset(), 0, 8).ToLowerInvariant());
        if (File.Exists(Path.Combine(target, HostExecutableName)))
            return target;

        // 途中で落ちても半端なフォルダーを「入っている」と見なさないよう、別名で揃えてから名前を変える。
        var staging = target + ".tmp-" + Environment.ProcessId;
        Directory.CreateDirectory(staging);
        foreach (var file in files)
            File.Copy(file, Path.Combine(staging, Path.GetFileName(file)), overwrite: true);
        try
        {
            Directory.Move(staging, target);
        }
        catch (IOException) when (File.Exists(Path.Combine(target, HostExecutableName)))
        {
            // 別の Loomo が先に入れた。
            Directory.Delete(staging, recursive: true);
        }

        return target;
    }

    /// <summary>古い版の一式を片付ける。動いているホストが掴んでいるものは消せないので、そのまま残る。</summary>
    private void RemoveStaleInstalls(string current)
    {
        try
        {
            foreach (var directory in Directory.GetDirectories(_installRoot))
            {
                if (string.Equals(directory, current, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Loomo と縁を切って起こす：ハンドルを継承させず（継承すると Loomo の子プロセスのパイプを掴み続ける）、
    /// Loomo がジョブの中にいても抜ける（抜けられなければそのまま起こす。その場合はジョブと一緒に消える）。
    /// </summary>
    private static void StartDetached(string executable, string workingDirectory)
    {
        const uint CreateBreakawayFromJob = 0x01000000;
        const uint CreateNewProcessGroup = 0x00000200;
        const uint CreateNoWindow = 0x08000000;
        const int ErrorAccessDenied = 5;

        Directory.CreateDirectory(workingDirectory);
        if (TryCreateProcess(executable, workingDirectory, CreateBreakawayFromJob | CreateNewProcessGroup | CreateNoWindow, out int error))
            return;
        if (error == ErrorAccessDenied &&
            TryCreateProcess(executable, workingDirectory, CreateNewProcessGroup | CreateNoWindow, out error))
            return;
        throw new Win32Exception(error, $"端末の常駐ホストを起動できませんでした: {executable}");
    }

    private static bool TryCreateProcess(string executable, string workingDirectory, uint flags, out int error)
    {
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder($"\"{executable}\"");
        if (CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                workingDirectory, ref startup, out var info))
        {
            CloseHandle(info.hProcess);
            CloseHandle(info.hThread);
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref StartupInfo lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// 開こうとしたシェルは別の Loomo が繋いでいる。同じシェルは先勝ちで、後から来た方には渡さない（§34.5）。
/// </summary>
public sealed class PtySessionInUseException(Guid sessionId)
    : InvalidOperationException($"端末セッション {sessionId:N} は別の接続が使用中です。")
{
    public Guid SessionId { get; } = sessionId;
}
