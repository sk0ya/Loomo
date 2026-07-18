namespace sk0ya.Loomo.Core.Models;

/// <summary>FolderTree 上のファイルまたはフォルダノード。</summary>
public sealed record FileNode(string Name, string FullPath, bool IsDirectory);
