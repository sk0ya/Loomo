using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

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
/// COM が無い・権限が無い・非 Windows の場合は空を返し、通常のファイルツリーを壊さない。
///
/// <para><b>掴んだ COM は必ずその場で解放する。</b>ここは <see cref="IsAvailable"/>／
/// <see cref="Exists"/>／<see cref="Enumerate"/> がツリーの再読込（＝ファイル監視の発火）ごとに
/// 呼ばれる経路で、Shell.Application を作りっぱなしにすると 1 回の再読込で 3 個ずつ RCW が積み上がり、
/// 解放が GC 任せになる。名前空間フォルダー・項目コレクション・各項目も同じ理由でその場で手放す。</para></summary>
public class WindowsFolderTreeShellNamespaceProvider
{
    private static readonly FolderTreeEntries Empty =
        new(Array.Empty<string>(), Array.Empty<string>());

    public virtual bool IsAvailable => OperatingSystem.IsWindows() && WithShell(_ => true, false);

    public virtual bool Exists(string path)
    {
        if (!OperatingSystem.IsWindows() || !FolderTreeShellNamespaces.IsShellPath(path)) return false;
        return WithFolder(path, _ => true, false);
    }

    public virtual FolderTreeEntries Enumerate(string path)
    {
        if (!OperatingSystem.IsWindows() || !FolderTreeShellNamespaces.IsShellPath(path))
            return Empty;

        return WithFolder(path, folder =>
        {
            var directories = new List<string>();
            var files = new List<string>();
            object? items = folder.Items();
            if (items is null)
                return Empty;

            try
            {
                foreach (dynamic item in (dynamic)items)
                {
                    try
                    {
                        string? name = item.Name as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        bool isFolder = item.IsFolder is true;
                        // Shell.Application が返す物理パスをそのまま FullPath にしない。
                        // そうすると、ごみ箱の内部ファイル等が通常の削除・編集対象に見えてしまう。
                        var child = FolderTreeShellNamespaces.Canonicalize(
                            path.TrimEnd('\\') + "\\" + name);
                        (isFolder ? directories : files).Add(child);
                    }
                    finally
                    {
                        Release((object)item);
                    }
                }
            }
            finally
            {
                Release(items);
            }

            directories.Sort(StringComparer.OrdinalIgnoreCase);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return new FolderTreeEntries(directories, files);
        }, Empty);
    }

    private static TResult WithShell<TResult>(Func<dynamic, TResult> action, TResult fallback)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            shell = type is null ? null : Activator.CreateInstance(type);
            return shell is null ? fallback! : action((dynamic)shell);
        }
        catch { return fallback; }
        finally { Release(shell, final: true); }
    }

    private static TResult WithFolder<TResult>(string path, Func<dynamic, TResult> action, TResult fallback)
        // 型引数を明示する（省略すると戻り値が dynamic に推論され、以降の型検査が効かなくなる）。
        => WithShell<TResult>(shell =>
        {
            object? folder = shell.NameSpace(FolderTreeShellNamespaces.Canonicalize(path));
            if (folder is null) return fallback!;
            try { return action((dynamic)folder); }
            finally { Release(folder); }
        }, fallback);

    // 自分で取り出したぶんだけ手放す（Shell.Application 本体は他所と共有しないので Final でよい）。
    private static void Release(object? com, bool final = false)
    {
        try
        {
            if (com is null || !Marshal.IsComObject(com)) return;
            if (final) Marshal.FinalReleaseComObject(com);
            else Marshal.ReleaseComObject(com);
        }
        catch { /* 解放できなくてもツリーの表示は続ける */ }
    }
}
