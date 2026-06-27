using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// Windows の一部の推論バックエンドは、ネイティブ層で非 ASCII のモデルパスを開けない。
/// 対象ディレクトリを空きドライブ文字へ一時的に割り当て、ネイティブ層には ASCII パスだけを渡す。
/// </summary>
internal sealed class NativeModelPath : IDisposable
{
    private const uint DddRawTargetPath = 0x00000001;
    private const uint DddRemoveDefinition = 0x00000002;
    private const uint DddExactMatchOnRemove = 0x00000004;
    private const uint DddNoBroadcastSystem = 0x00000008;

    private static readonly object DriveLock = new();
    private static readonly HashSet<char> AllocatedDrives = new();

    private readonly string? _deviceName;
    private readonly string? _targetPath;
    private readonly string? _temporaryHardLink;
    private bool _disposed;

    private NativeModelPath(string path, string? deviceName = null, string? targetPath = null,
        string? temporaryHardLink = null)
    {
        Path = path;
        _deviceName = deviceName;
        _targetPath = targetPath;
        _temporaryHardLink = temporaryHardLink;
    }

    public string Path { get; }

    public static NativeModelPath Create(string modelPath)
    {
        var fullPath = System.IO.Path.GetFullPath(modelPath);
        if (!OperatingSystem.IsWindows() || IsAscii(fullPath))
            return new NativeModelPath(fullPath);

        var isFile = File.Exists(fullPath);
        var targetDirectory = isFile ? System.IO.Path.GetDirectoryName(fullPath)! : fullPath;
        string? hardLink = null;

        try
        {
            var leafName = isFile ? System.IO.Path.GetFileName(fullPath) : null;
            if (leafName is not null && !IsAscii(leafName))
            {
                hardLink = System.IO.Path.Combine(targetDirectory,
                    $".loomo-model-{Guid.NewGuid():N}{System.IO.Path.GetExtension(fullPath)}");
                CreateHardLink(hardLink, fullPath);
                leafName = System.IO.Path.GetFileName(hardLink);
            }

            var targetPath = ToNtPath(targetDirectory);
            var deviceName = AllocateDrive(targetPath);
            var nativePath = leafName is null ? $"{deviceName}\\" : $"{deviceName}\\{leafName}";
            return new NativeModelPath(nativePath, deviceName, targetPath, hardLink);
        }
        catch
        {
            if (hardLink is not null)
                TryDelete(hardLink);
            throw;
        }
    }

    private static string AllocateDrive(string targetPath)
    {
        lock (DriveLock)
        {
            for (var letter = 'Z'; letter >= 'D'; letter--)
            {
                if (AllocatedDrives.Contains(letter) || IsDeviceDefined($"{letter}:"))
                    continue;

                var deviceName = $"{letter}:";
                if (!DefineDosDevice(DddRawTargetPath | DddNoBroadcastSystem, deviceName, targetPath))
                    continue;

                AllocatedDrives.Add(letter);
                return deviceName;
            }
        }

        throw new IOException("日本語を含むモデルパス用の空きドライブ文字を確保できませんでした。");
    }

    private static bool IsDeviceDefined(string deviceName)
    {
        var buffer = new char[512];
        return QueryDosDevice(deviceName, buffer, buffer.Length) != 0;
    }

    private static string ToNtPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\??\UNC\" + path[2..]
        : @"\??\" + path;

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
            if (c > 0x7f)
                return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_deviceName is not null && _targetPath is not null)
        {
            DefineDosDevice(
                DddRawTargetPath | DddRemoveDefinition | DddExactMatchOnRemove | DddNoBroadcastSystem,
                _deviceName, _targetPath);
            lock (DriveLock)
                AllocatedDrives.Remove(_deviceName[0]);
        }

        if (_temporaryHardLink is not null)
            TryDelete(_temporaryHardLink);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CreateHardLink(string linkPath, string existingPath)
    {
        if (!CreateHardLinkNative(linkPath, existingPath, IntPtr.Zero))
            throw new IOException(
                $"日本語を含む GGUF モデルパスの互換リンクを作成できませんでした: {existingPath}",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string? targetPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string? deviceName, [Out] char[] targetPath, int maxLength);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkNative(string fileName, string existingFileName,
        IntPtr securityAttributes);
}
