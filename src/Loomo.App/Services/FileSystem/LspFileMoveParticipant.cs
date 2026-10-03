using System.Windows;
using Editor.Core.Lsp;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ファイル／フォルダーの移動・改名に割り込む口。<see cref="FolderTreeCommandHandler"/> が
/// 実際に動かす直前と直後に呼ぶ。ツリー・ファイル一覧ペインの名前変更・切り取り貼り付け・D&amp;D は
/// すべてこのハンドラーへ落ちるので、ここ 1 か所で全経路を拾える。
/// <para>どちらも UI スレッドから同期的に呼ばれる。<see cref="BeforeMove"/> が例外を投げると移動は行われない。</para>
/// </summary>
public interface IFileMoveParticipant
{
    void BeforeMove(string source, string destination, bool isDirectory);
    void AfterMove(string source, string destination, bool isDirectory);
}

/// <summary>
/// 移動・改名に合わせて import 等の参照を言語サーバーに直させる（VS Code の
/// 「ファイル移動時に import を更新」相当）。
///
/// <para>流れ: 動かす前に <c>workspace/willRenameFiles</c> → 返った編集を（設定に応じて確認してから）
/// <c>ShellWindow.ApplyLspWorkspaceEdit</c> と同じトランザクション経路で適用 → 実際に動かす →
/// <c>workspace/didRenameFiles</c>。編集は<b>旧パスのまま</b>当てる——tsserver は移動するファイル自身の
/// 相対 import も旧 URI で返すので、動かした後では当て先が無くなる。開いているタブはバッファへ当たり
/// （未保存のまま）、そのあと既存の改名追従（<c>RebaseEditorTabPath</c>）でパスごと付いていく。</para>
///
/// <para><b>同期で待つ理由とその上限:</b> 移動の経路（名前変更・貼り付け・D&amp;D・競合ダイアログ）は全部
/// 同期で組まれていて、移動の前に答えが要る。サーバーの応答は背景スレッドで読まれ UI スレッドを
/// 必要としないので、<see cref="Task.Run(System.Func{Task})"/> に逃がして <see cref="ResponseTimeout"/> だけ待つ。
/// 間に合わなければ参照は更新せずに移動する（移動そのものを止めない）。</para>
/// </summary>
public sealed class LspFileMoveParticipant : IFileMoveParticipant
{
    /// <summary>willRenameFiles の応答を待つ上限。tsserver の getEditsForFileRename はプロジェクト読込済みなら
    /// 数百 ms で返る。これを超えるのは読込中か応答しないサーバーで、UI を止め続けるより移動を優先する。</summary>
    internal static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    private readonly LspWorkspaceService _lsp;
    private readonly LspSettings _settings;
    private readonly Action<LoomoSettings>? _save;
    private readonly LoomoSettings? _rootSettings;
    private readonly Action<string> _reportError;
    private Func<LspWorkspaceEdit, string?>? _apply;
    private Func<FileMoveReferencePrompt, FileMoveReferenceAnswer> _confirm;

    public LspFileMoveParticipant(LspWorkspaceService lsp, LoomoSettings settings, Action<LoomoSettings>? save = null)
        : this(lsp, settings.Lsp, ToastService.Warning, save, settings) { }

    internal LspFileMoveParticipant(
        LspWorkspaceService lsp,
        LspSettings settings,
        Action<string> reportError,
        Action<LoomoSettings>? save = null,
        LoomoSettings? rootSettings = null)
    {
        _lsp = lsp;
        _settings = settings;
        _reportError = reportError;
        _save = save;
        _rootSettings = rootSettings;
        _confirm = ConfirmWithMessageBox;
    }

    /// <summary>
    /// 編集の適用先を結ぶ（ShellWindow が起動時に1回）。<paramref name="apply"/> は失敗理由を返す（成功は null）。
    /// 結ばれるまでは何もしない＝移動は従来どおり。
    /// </summary>
    public void Attach(
        Func<LspWorkspaceEdit, string?> apply,
        Func<FileMoveReferencePrompt, FileMoveReferenceAnswer>? confirm = null)
    {
        _apply = apply;
        if (confirm is not null) _confirm = confirm;
    }

    public void BeforeMove(string source, string destination, bool isDirectory)
    {
        if (_apply is null || _settings.UpdateReferencesOnFileMove == FileMoveReferenceUpdate.Never) return;

        var rename = new LspFileRename(Path.GetFullPath(source), Path.GetFullPath(destination), isDirectory);
        LspFileRenameEditResult result;
        using (var cts = new CancellationTokenSource())
        {
            var pending = Task.Run(() => _lsp.WillRenameFilesAsync([rename], cts.Token));
            try
            {
                if (!pending.Wait(ResponseTimeout))
                {
                    cts.Cancel();
                    _reportError("言語サーバーの応答が間に合わなかったため、参照は更新せずに移動しました。");
                    return;
                }
                result = pending.Result;
            }
            catch (Exception ex)
            {
                _reportError($"参照の更新を問い合わせられませんでした: {(ex as AggregateException)?.InnerException?.Message ?? ex.Message}");
                return;
            }
        }

        if (result.Error is { } error)
        {
            _reportError($"参照を更新できませんでした（{error}）。参照は更新せずに移動します。");
            return;
        }
        if (result.Edit is not { } edit) return;

        var fileCount = CountFiles(edit);
        if (fileCount == 0) return;

        if (_settings.UpdateReferencesOnFileMove == FileMoveReferenceUpdate.Prompt)
        {
            var answer = _confirm(new FileMoveReferencePrompt(
                Path.GetFileName(rename.OldPath.TrimEnd(Path.DirectorySeparatorChar)), fileCount));
            switch (answer)
            {
                case FileMoveReferenceAnswer.Skip:
                    return;
                case FileMoveReferenceAnswer.Never:
                    Remember(FileMoveReferenceUpdate.Never);
                    return;
                case FileMoveReferenceAnswer.Always:
                    Remember(FileMoveReferenceUpdate.Always);
                    break;
            }
        }

        if (_apply(edit) is { } failure)
            _reportError($"参照を更新できませんでした: {failure}");
    }

    public void AfterMove(string source, string destination, bool isDirectory)
    {
        if (_apply is null) return;
        try
        {
            _lsp.DidRenameFiles([new LspFileRename(Path.GetFullPath(source), Path.GetFullPath(destination), isDirectory)]);
        }
        catch
        {
            // 通知の失敗で移動を失敗扱いにしない。
        }
    }

    /// <summary>編集が及ぶファイル数（本文の編集＋ファイル操作の対象。重複は数えない）。</summary>
    internal static int CountFiles(LspWorkspaceEdit edit)
    {
        var uris = new HashSet<string>(LspUri.Comparer);
        foreach (var (uri, edits) in edit.Changes)
            if (edits.Count > 0) uris.Add(LspUri.Normalize(uri));
        foreach (var operation in edit.FileOperations ?? [])
            uris.Add(LspUri.Normalize(operation.Uri));
        return uris.Count;
    }

    private void Remember(FileMoveReferenceUpdate mode)
    {
        _settings.UpdateReferencesOnFileMove = mode;
        if (_save is not null && _rootSettings is not null)
        {
            try { _save(_rootSettings); } catch { /* 保存できなくても今回の選択は効かせる */ }
        }
    }

    private static FileMoveReferenceAnswer ConfirmWithMessageBox(FileMoveReferencePrompt prompt)
    {
        var owner = Application.Current?.MainWindow;
        var message =
            $"「{prompt.Name}」の移動に合わせて、{prompt.FileCount} ファイルの参照（import など）を更新します。\n\n" +
            "はい: 更新して移動する\nいいえ: 参照は更新せずに移動する\n\n" +
            "毎回確認するかどうかは 設定 › 言語サーバー (LSP) で変えられます。";
        var answer = owner is null
            ? MessageBox.Show(message, "参照の更新", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, message, "参照の更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return answer == MessageBoxResult.Yes ? FileMoveReferenceAnswer.Update : FileMoveReferenceAnswer.Skip;
    }
}

/// <summary>確認ダイアログへ渡す内容。</summary>
public sealed record FileMoveReferencePrompt(string Name, int FileCount);

/// <summary>確認への答え。Always/Never は設定へ書き戻す。</summary>
public enum FileMoveReferenceAnswer
{
    Update,
    Skip,
    Always,
    Never,
}
