namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 消えたファイルを探して戻す（§24.94）。ツリーにもタブにも無いものへの入口。
/// パレットに消したファイルを新しい順に並べ（名前で絞るのはパレットの検索）、選ぶと消す直前の版でできることを並べる。
/// </summary>
public partial class ShellWindow
{
    private const string DeletedFilesCategory = "消えたファイル";

    /// <summary>パレット「Git: 消えたファイルを探す…」。</summary>
    private async void OpenDeletedFiles()
    {
        if (_git.RootPath is not { Length: > 0 } root)
        {
            ToastService.Info("Git のリポジトリを開いていません。");
            return;
        }
        IReadOnlyList<PaletteCommand> loading = [new PaletteCommand(DeletedFilesCategory, "消したファイルを履歴から探しています…", () => { })];
        OpenCommandPalette(PaletteMode.Command, loading);
        IReadOnlyList<GitDeletedFile> deleted;
        try
        {
            deleted = await _git.GetDeletedFilesAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            deleted = [];
        }
        // 消したあとで作り直したもの（今ある）は「消えたファイル」ではない。
        var now = DateTimeOffset.Now;
        var items = deleted
            .Where(file => !File.Exists(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar))))
            .Select(file => new PaletteCommand(DeletedFilesCategory, file.Path, () => OpenDeletedFileActions(root, file))
            {
                Detail = DeletedFileDetail(file, now),
            })
            .ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand(DeletedFilesCategory, "消したファイルは見つかりませんでした（直近 300 回の削除まで見ています）", () => { }));
        if (IsPaletteOpen && ReferenceEquals(_paletteCommands, loading))
            OpenCommandPalette(PaletteMode.Command, items);
    }

    private static string DeletedFileDetail(GitDeletedFile file, DateTimeOffset now)
        => $"{file.ShortHash} で消した{(file.Date is { } at ? $"（{RelativeAge(now - at)}・{at.LocalDateTime:yyyy-MM-dd}）" : "")}" +
           $"{Environment.NewLine}{file.Author}  {file.Subject}";

    private void OpenDeletedFileActions(string root, GitDeletedFile file)
    {
        var detail = DeletedFileDetail(file, DateTimeOffset.Now);
        PaletteCommand Act(string label, Action run, string? note = null)
            => new(DeletedFilesCategory, label, run) { Detail = note is null ? $"{file.Path}{Environment.NewLine}{detail}" : $"{file.Path}{Environment.NewLine}{note}" };
        OpenCommandPalette(PaletteMode.Command, [
            Act("消す直前の版を開く（読み取り専用）", () => _ = OpenDeletedFileRevisionAsync(file)),
            Act("作業ツリーへ戻す", () => _ = RestoreDeletedFileAsync(root, file),
                $"消す直前（{file.ShortHash}^）の内容で作り直し、ステージする（git checkout {file.ShortHash}^ -- {file.Path}）"),
            Act("消したコミットを Git ペインで見る", () => _ = RevealDeletingCommitAsync(file)),
        ]);
    }

    private async Task OpenDeletedFileRevisionAsync(GitDeletedFile file)
    {
        var result = await _git.GetFileAtRevisionAsync(file.RevisionBefore, file.Path);
        if (!result.Success)
        {
            ToastService.Error($"消す直前の版を読めませんでした: {result.Message}");
            return;
        }
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await _editor.OpenDocumentAsync(new EditorDocument
        {
            FileName = $"{Path.GetFileNameWithoutExtension(file.Path)}@{file.ShortHash}^{Path.GetExtension(file.Path)}",
            Content = result.Output,
            OnSaved = _ => { },   // 過去の版なので保存先は無い。戻すなら「作業ツリーへ戻す」
        });
    }

    private async Task RestoreDeletedFileAsync(string root, GitDeletedFile file)
    {
        var fullPath = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
        {
            ToastService.Warning($"{file.Path} は今あるので戻しません（上書きしない）。");
            return;
        }
        var result = await _git.RestoreFileAtRevisionAsync(file.RevisionBefore, file.Path);
        if (!result.Success)
        {
            ToastService.Error($"戻せませんでした: {result.Message}");
            return;
        }
        ToastService.Info($"{file.Path} を {file.ShortHash}^ の内容で戻しました（ステージ済み）。");
        await OpenPathInEditorAsync(fullPath, line: 0, column: 0);
    }

    private async Task RevealDeletingCommitAsync(GitDeletedFile file)
    {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
        await _vm.GitSession.SelectCommitAsync(file.Hash);
        FocusPane(PaneKind.Git);
    }
}
