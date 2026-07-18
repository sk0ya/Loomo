using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ワークスペース（フォルダ・選択状態・ファイルシステム）を抽象化。</summary>
public interface IWorkspaceService
{
    string? RootPath { get; }
    string? SelectedPath { get; set; }
    void OpenFolder(string rootPath);
    Task<IReadOnlyList<FileNode>> ListAsync(string path);
    Task<string> ReadFileAsync(string path);
    /// <summary>パスをワークスペースルート基準の絶対パスへ解決する。</summary>
    string ResolvePath(string path);
    event EventHandler<string?>? SelectionChanged;
    event EventHandler<string?>? RootChanged;
}
