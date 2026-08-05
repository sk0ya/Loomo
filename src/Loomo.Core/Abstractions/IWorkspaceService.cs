using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ワークスペース（フォルダ・選択状態・ファイルシステム）を抽象化。</summary>
public interface IWorkspaceService
{
    /// <summary>開いているワークスペースフォルダー（順序あり）。[0] がプライマリ
    /// （ツールの相対パス基準・ターミナル既定cwd・Git/検索/デバッグの既定対象＝<see cref="PrimaryFolder"/>）。</summary>
    IReadOnlyList<string> Folders { get; }
    /// <summary>プライマリフォルダー（<see cref="Folders"/>[0]）。未オープン時は null。
    ///
    /// <para><b>「ワークスペース」の代表値として使わないこと。</b>これは「プライマリでなければ
    /// ならない用途」——相対パスの基準・ターミナル既定 cwd・スナップショットの同一性・Git の既定対象——
    /// のためにある。「このパスはワークスペース内か」「どのフォルダーの担当か」は
    /// <see cref="Contains"/> / <see cref="FolderFor"/> を使う。ここと前方一致させると、
    /// あとから追加したフォルダーのファイルが黙って対象外になる。</para>
    ///
    /// <para><b>名前について。</b>旧名は <c>RootPath</c> だった。短く無害に見えるせいで
    /// 「ワークスペースのルート」と読まれ、追加フォルダーを取りこぼす実装が繰り返し生まれた
    /// （設計書 §32.10）。名前の側で「これはプライマリ<b>だけ</b>だ」と言わせるための改名なので、
    /// 元に戻さないこと。永続化スナップショット（<c>WorkspaceSnapshot.RootPath</c>）とは別物で、
    /// あちらは JSON の項目名なので据え置いている——見分けがつくこと自体が狙い。</para></summary>
    string? PrimaryFolder { get; }
    string? SelectedPath { get; set; }
    /// <summary>ワークスペース全体をこの1フォルダーへリセットする（既存の追加フォルダーは失われる）。</summary>
    void OpenFolder(string rootPath);
    /// <summary>ワークスペースへフォルダーを追加する（マルチルート）。既存フォルダーと同一・祖先/子孫関係
    /// なら何もしない。</summary>
    void AddFolder(string path);
    /// <summary>ワークスペースからフォルダーを取り除く。プライマリ（<see cref="Folders"/>[0]）は
    /// 取り除けない（ワークスペースを切り替えるには <see cref="OpenFolder"/> を使う）。</summary>
    void RemoveFolder(string path);
    // ── ワークスペースへの問い（実装は WorkspacePaths ただ1つ）─────────────────
    // Folders とプライマリだけを公開していた頃は、各機能が「どちらを使うか」と
    // 「包含判定の書き方」を毎回自分で決めていて、19 のファイルが個別に同じ学習をしていた。
    // 追加フォルダーを取りこぼす／C:\work\app2 を C:\work\app 配下と誤認する、の2つが常習の間違い。
    // 既定実装で共通化してあるので、テスト用の実装も含めて振る舞いは常に一致する。

    /// <summary>このパスがワークスペース（全フォルダー）の中にあるか。
    /// <b>「書き込んでよいか」「対象に含めるか」はこれで判定する</b>——
    /// <see cref="PrimaryFolder"/> との前方一致で代用してはいけない。</summary>
    bool Contains(string path) => Files.WorkspacePaths.Contains(Folders, path);

    /// <summary>このパスを担当するワークスペースフォルダー（属さなければ null）。
    /// 言語サーバーのルート・フォルダー単位の状態・表示上の基準はこれ。</summary>
    string? FolderFor(string path) => Files.WorkspacePaths.FolderFor(Folders, path);

    /// <summary>そのパスの<b>解決基準にするフォルダー</b>。担当フォルダーがあればそれ、
    /// 無ければ（ワークスペース外・パス不明）プライマリへ倒す。
    ///
    /// <para>相対リンクの解決先やプレビューの base href のように「基準が要る／無いなら諦めるより
    /// プライマリで出した方がまし」という場所のためにある。<see cref="PrimaryFolder"/> を直に渡すと、
    /// 追加フォルダーのファイルが常にプライマリ基準で解決され、
    /// <c>../assets/img.png</c> のような上への相対参照が黙って壊れる。</para></summary>
    string? FolderForOrPrimary(string? path)
        => (path is null ? null : FolderFor(path)) ?? PrimaryFolder;

    /// <summary>一覧表示用の短い表記。フォルダーが2つ以上あるときだけフォルダー名を前置する。</summary>
    string ToDisplayPath(string path) => Files.WorkspacePaths.ToDisplayPath(Folders, path);

    Task<IReadOnlyList<FileNode>> ListAsync(string path, CancellationToken ct = default);
    Task<string> ReadFileAsync(string path, CancellationToken ct = default);
    /// <summary>パスを絶対パスへ解決する（相対は<see cref="PrimaryFolder"/>基準）。</summary>
    string ResolvePath(string path);
    event EventHandler<string?>? SelectionChanged;
    /// <summary>プライマリフォルダーが変わったとき（<see cref="OpenFolder"/>）。</summary>
    event EventHandler<string?>? RootChanged;
    /// <summary>フォルダー集合が変わったとき（<see cref="AddFolder"/>/<see cref="RemoveFolder"/>/
    /// <see cref="OpenFolder"/> のいずれでも発火）。</summary>
    event EventHandler? FoldersChanged;
}
