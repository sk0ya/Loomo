using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>FolderTree で扱う Windows Shell 名前空間の既知の入口。</summary>
public sealed record ShellNamespaceDescriptor(string Id, string Label, string Path);

/// <summary>Shell 名前空間の入口と、アドレス欄で使うパスの正規化を一箇所に集める。
/// 実ファイルシステムのパスと混ぜないことが重要で、ここで返す仮想パスはファイル操作や
/// ワークスペースのパス制限へ誤って流れない。</summary>
public static class FolderTreeShellNamespaces
{
    public const string RecycleBinId = "645FF040-5081-101B-9F08-00AA002F954E";
    public const string NetworkId = "F02C1A0D-BE21-4350-88B0-7367FC96EF3C";
    public const string LibrariesId = "031E4825-7B94-4DC3-B131-E946B44C8DD5";

    public static IReadOnlyList<ShellNamespaceDescriptor> Known { get; } = new[]
    {
        new ShellNamespaceDescriptor(RecycleBinId, "ごみ箱", Root(RecycleBinId)),
        new ShellNamespaceDescriptor(NetworkId, "ネットワーク", Root(NetworkId)),
        new ShellNamespaceDescriptor(LibrariesId, "ライブラリ", Root(LibrariesId)),
    };

    public static bool IsShellPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && (path.StartsWith("shell:::{", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(":::{", StringComparison.OrdinalIgnoreCase));

    public static bool TryNormalize(string? input, string? basePath, out string normalized)
    {
        normalized = string.Empty;
        var text = input?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();

        var alias = Known.FirstOrDefault(x =>
            string.Equals(x.Label, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.Id, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.Path, text, StringComparison.OrdinalIgnoreCase)
            || (x.Id == RecycleBinId && string.Equals(text, "Recycle Bin", StringComparison.OrdinalIgnoreCase))
            || (x.Id == NetworkId && string.Equals(text, "Network", StringComparison.OrdinalIgnoreCase))
            || (x.Id == LibrariesId && string.Equals(text, "Libraries", StringComparison.OrdinalIgnoreCase)));
        if (alias is not null)
        {
            normalized = alias.Path;
            return true;
        }

        if (IsShellPath(text))
        {
            normalized = Canonicalize(text);
            return true;
        }

        // 仮想フォルダー内での相対入力（例: shell:::{Libraries}\Music.library-ms\…）。
        if (IsShellPath(basePath))
        {
            var baseNormalized = Canonicalize(basePath!);
            if (text == ".")
            {
                normalized = baseNormalized;
                return true;
            }

            if (text == "..")
            {
                normalized = Parent(baseNormalized) ?? baseNormalized;
                return true;
            }

            if (!text.Contains(':') && !Path.IsPathRooted(text))
            {
                normalized = Canonicalize(baseNormalized.TrimEnd('\\') + "\\" + text);
                return true;
            }
        }

        try
        {
            var physicalBase = IsShellPath(basePath) ? Environment.CurrentDirectory : basePath;
            normalized = Path.GetFullPath(text, physicalBase ?? Environment.CurrentDirectory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string Canonicalize(string path)
    {
        var value = path.Trim().Replace('/', '\\');
        if (value.StartsWith(":::{", StringComparison.OrdinalIgnoreCase))
            value = "shell:" + value;
        return value.TrimEnd('\\');
    }

    public static string? Parent(string path)
    {
        if (!IsShellPath(path)) return null;
        var normalized = Canonicalize(path);
        var slash = normalized.LastIndexOf('\\');
        var rootEnd = normalized.IndexOf('}');
        if (slash <= rootEnd)
            return null;
        return normalized[..slash];
    }

    public static string Name(string path)
    {
        var normalized = Canonicalize(path);
        var descriptor = Known.FirstOrDefault(x => string.Equals(x.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (descriptor is not null) return descriptor.Label;
        var slash = normalized.LastIndexOf('\\');
        return slash >= 0 && slash + 1 < normalized.Length ? normalized[(slash + 1)..] : normalized;
    }

    private static string Root(string id) => "shell:::" + "{" + id + "}";
}

/// <summary>Shell.Application を遅延バインドして FolderTree 用の子項目へ変換する。
/// COM が無い・権限が無い・非 Windows の場合は空を返し、通常のファイルツリーを壊さない。</summary>
public class WindowsFolderTreeShellNamespaceProvider
{
    public virtual bool IsAvailable => OperatingSystem.IsWindows() && CreateShell() is not null;

    public virtual bool Exists(string path)
    {
        if (!OperatingSystem.IsWindows() || !FolderTreeShellNamespaces.IsShellPath(path)) return false;
        try { return OpenFolder(path) is not null; }
        catch { return false; }
    }

    public virtual FolderTreeEntries Enumerate(string path)
    {
        if (!OperatingSystem.IsWindows() || !FolderTreeShellNamespaces.IsShellPath(path))
            return new FolderTreeEntries(Array.Empty<string>(), Array.Empty<string>());

        try
        {
            dynamic? folder = OpenFolder(path);
            if (folder is null)
                return new FolderTreeEntries(Array.Empty<string>(), Array.Empty<string>());

            var directories = new List<string>();
            var files = new List<string>();
            foreach (dynamic item in folder.Items())
            {
                var name = item.Name as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var isFolder = item.IsFolder is true;
                // Shell.Application が返す物理パスをそのまま FullPath にしない。
                // そうすると、ごみ箱の内部ファイル等が通常の削除・編集対象に見えてしまう。
                var child = FolderTreeShellNamespaces.Canonicalize(
                    path.TrimEnd('\\') + "\\" + name);
                (isFolder ? directories : files).Add(child);
            }

            directories.Sort(StringComparer.OrdinalIgnoreCase);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return new FolderTreeEntries(directories, files);
        }
        catch
        {
            return new FolderTreeEntries(Array.Empty<string>(), Array.Empty<string>());
        }
    }

    private static object? CreateShell()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            return type is null ? null : Activator.CreateInstance(type);
        }
        catch { return null; }
    }

    private static object? OpenFolder(string path)
    {
        var shell = CreateShell();
        if (shell is null) return null;
        dynamic instance = shell;
        return instance.NameSpace(FolderTreeShellNamespaces.Canonicalize(path));
    }
}
