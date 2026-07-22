using System;
using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// FolderTree のツリー走査で共有するヘルパー（ExpandAll の再帰展開が使う）。
/// </summary>
public static class FolderTreeFilter
{
    // シンボリックリンク/ジャンクションの循環で無限再帰しないための保険。実在のソースツリーは
    // この深さに達しないので、超えたら打ち切る。
    public const int MaxDepth = 64;

    public static bool IsReparsePoint(string directory)
    {
        try
        {
            return (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
