using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ワークスペース（フォルダ・選択状態・ファイルシステム）を抽象化。</summary>
public interface IWorkspaceService
{
    /// <summary>開いているワークスペースフォルダー（順序あり）。[0] がプライマリ
    /// （ツールの相対パス基準・ターミナル既定cwd・Git/検索/デバッグの既定対象、旧 RootPath と同じ役割）。</summary>
    IReadOnlyList<string> Folders { get; }
    /// <summary>プライマリフォルダー（<see cref="Folders"/>[0]）。未オープン時は null。</summary>
    string? RootPath { get; }
    string? SelectedPath { get; set; }
    /// <summary>ワークスペース全体をこの1フォルダーへリセットする（既存の追加フォルダーは失われる）。</summary>
    void OpenFolder(string rootPath);
    /// <summary>ワークスペースへフォルダーを追加する（マルチルート）。既存フォルダーと同一・祖先/子孫関係
    /// なら何もしない。</summary>
    void AddFolder(string path);
    /// <summary>ワークスペースからフォルダーを取り除く。プライマリ（<see cref="Folders"/>[0]）は
    /// 取り除けない（ワークスペースを切り替えるには <see cref="OpenFolder"/> を使う）。</summary>
    void RemoveFolder(string path);
    Task<IReadOnlyList<FileNode>> ListAsync(string path, CancellationToken ct = default);
    Task<string> ReadFileAsync(string path, CancellationToken ct = default);
    /// <summary>パスをワークスペースルート基準の絶対パスへ解決する。</summary>
    string ResolvePath(string path);
    event EventHandler<string?>? SelectionChanged;
    /// <summary>プライマリフォルダーが変わったとき（<see cref="OpenFolder"/>）。</summary>
    event EventHandler<string?>? RootChanged;
    /// <summary>フォルダー集合が変わったとき（<see cref="AddFolder"/>/<see cref="RemoveFolder"/>/
    /// <see cref="OpenFolder"/> のいずれでも発火）。</summary>
    event EventHandler? FoldersChanged;
}
