using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ターミナル（sk0ya.Terminal）への操作を抽象化。</summary>
public interface ITerminalService
{
    /// <summary>コマンドを実行し結果を待つ。</summary>
    Task<CommandResult> RunCommandAsync(string command, CancellationToken ct);

    /// <summary>作業ディレクトリを設定。</summary>
    void SetWorkingDirectory(string path);

    string CurrentDirectory { get; }
    bool IsExecuting { get; }

    /// <summary>実行された全コマンド結果（人間・AI問わず）の通知。</summary>
    event EventHandler<CommandResult>? CommandExecuted;
}

/// <summary>エディタ（sk0ya.Editor）への操作を抽象化。</summary>
public interface IEditorService
{
    Task OpenFileAsync(string path);
    Task<string> GetActiveContentAsync();
    Task<string> GetSelectedTextAsync();

    /// <summary>差分を提示する（適用は ApplyEditAsync）。戻り値は差分の概要。</summary>
    Task<string> ShowDiffAsync(string path, string proposedContent);

    /// <summary>編集を適用して保存。</summary>
    Task<bool> ApplyEditAsync(string path, string newContent);

    /// <summary>編集可能な「仮想ドキュメント」をエディタペインで開く。ユーザーが保存（:w）すると
    /// <see cref="EditorDocument.OnSaved"/> が最新内容で呼ばれる（永続化はコールバック側の責務）。
    /// 設定の長文項目をエディタ領域で編集するために使う。</summary>
    Task OpenDocumentAsync(EditorDocument document);

    string? ActiveFilePath { get; }
}

/// <summary>ワークスペース（フォルダ・選択状態・ファイルシステム）を抽象化。</summary>
public interface IWorkspaceService
{
    string? RootPath { get; }
    string? SelectedPath { get; set; }

    void OpenFolder(string rootPath);
    Task<IReadOnlyList<FileNode>> ListAsync(string path);
    Task<string> ReadFileAsync(string path);

    /// <summary>パスをワークスペースルート基準の絶対パスへ解決する。
    /// ルート外限定が有効でルート外を指す場合は <see cref="System.UnauthorizedAccessException"/>。</summary>
    string ResolvePath(string path);

    event EventHandler<string?>? SelectionChanged;
    event EventHandler<string?>? RootChanged;
}

/// <summary>危険操作（コマンド実行・書込）のユーザー承認。</summary>
public interface IApprovalService
{
    Task<bool> RequestApprovalAsync(string toolName, string summary, CancellationToken ct);
}

/// <summary>
/// ブラウザ（可視 WebView2 ペインのアクティブタブ）への操作を抽象化。
/// <see cref="ITerminalService"/> が可視ターミナルへ操作を一本化するのと同様に、
/// AIのブラウザ操作も人間が見ているブラウザペインへ一本化する（別ウィンドウは起動しない）。
/// 実装は WebView2 に依存するため App 層に置く（Core は UI 非依存を保つ）。
/// </summary>
public interface IBrowserService
{
    /// <summary>操作可能なアクティブタブ（初期化済み WebView2）が存在するか。</summary>
    bool IsAvailable { get; }

    /// <summary>指定URLへ遷移し、読み込み完了を待つ。遷移後のURL・タイトルを返す。</summary>
    Task<BrowserPageInfo> NavigateAsync(string url, CancellationToken ct);

    /// <summary>現在表示中ページのURL・タイトルを返す。</summary>
    Task<BrowserPageInfo> GetPageInfoAsync(CancellationToken ct);

    /// <summary>現在のページでクリック/入力できる要素（リンク・ボタン・入力欄）を、表示文言と
    /// そのまま <see cref="ClickAsync"/>/<see cref="TypeAsync"/> へ渡せる CSS セレクタ付きで列挙する。
    /// セレクタを盲目で推測させず、可視要素から選ばせるための一括取得。</summary>
    Task<IReadOnlyList<BrowserClickable>> ListClickablesAsync(CancellationToken ct);

    /// <summary>現在のページの可視テキスト（document.body.innerText）を抽出する。</summary>
    Task<string> GetVisibleTextAsync(CancellationToken ct);

    /// <summary>CSSセレクタに一致する最初の要素をクリックする。一致が無ければ例外。</summary>
    Task ClickAsync(string selector, CancellationToken ct);

    /// <summary>CSSセレクタに一致する入力要素へ値を設定し input/change を発火する。一致が無ければ例外。</summary>
    Task TypeAsync(string selector, string text, CancellationToken ct);

    /// <summary>現在のページのスクリーンショット(PNG)を撮りバイト列を返す。</summary>
    Task<byte[]> CaptureScreenshotAsync(CancellationToken ct);
}

/// <summary>ブラウザの現在ページ情報。</summary>
public sealed record BrowserPageInfo(string Url, string Title);

/// <summary>ページ内のクリック/入力可能な要素。<see cref="Selector"/> は querySelector で再選択できる。</summary>
public sealed record BrowserClickable(string Tag, string Text, string Selector);
