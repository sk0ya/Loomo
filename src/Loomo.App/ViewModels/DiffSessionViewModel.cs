using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>AI変更、作業ツリー、コミット範囲の差分表示を調停する。</summary>
public sealed partial class DiffSessionViewModel : ObservableObject
{
    private readonly IFileChangeJournal _journal;
    private readonly GitService _git;
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;
    private readonly DiffFileGateway _files;
    public DiffConflictViewModel Conflict { get; }
    private readonly DiffSessionQuery _query;
    private readonly DiffSessionCommandHandler _commands;
    private bool _loaded;

    private (string? From, string To)? _commitRange;

    /// <summary>差分の出どころ（Git／AI変更／アドホック比較）。ヘッダーのラジオボタンで切り替わる。</summary>
    [ObservableProperty] private DiffSource _source = DiffSource.Git;
    // ラジオボタン用の相互排他プロキシ。true を書いたものへ切り替わり、false 書き込みは無視する
    // （RadioButton は選択が移るとき旧選択に false を書くので、ここで捨てないと二重に切り替わる）。
    public bool IsGitMode { get => Source == DiffSource.Git; set { if (value) Source = DiffSource.Git; } }
    public bool IsAiMode { get => Source == DiffSource.Ai; set { if (value) Source = DiffSource.Ai; } }
    public bool IsCompareMode { get => Source == DiffSource.Compare; set { if (value) Source = DiffSource.Compare; } }
    private bool _suppressModeChangeRefresh;
    [ObservableProperty] private bool _isSideBySide = true;
    [ObservableProperty] private string _gitTargetLabel = "";
    /// <summary>単一コミットの差分を表示中なら、そのコミットを Git 一覧で選択できる。</summary>
    public bool CanOpenCommitInGit => _commitRange is { From: null };
    [ObservableProperty] private DiffFileItem? _selectedFile;
    [ObservableProperty] private string _emptyMessage = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _canDiscardSelected;
    [ObservableProperty] private bool _canDiscardLines;

    public ObservableCollection<DiffFileItem> Files { get; } = new();
    public ObservableCollection<DiffRowVm> DiffRows { get; } = new();
    public ObservableCollection<DiffSideRowVm> SideRows { get; } = new();

    public ObservableCollection<DiffHunkVm> Hunks { get; } = new();

    public event EventHandler<string>? CommitOpenInGitRequested;

    public bool CanStageHunks => Hunks.Count > 0;

    public DiffSessionViewModel(
        IFileChangeJournal journal, GitService git, IEditorService editor, IWorkspaceService workspace,
        DiffFileGateway files, DiffSessionQuery query, DiffSessionCommandHandler commands)
    {
        _journal = journal;
        _git = git;
        _editor = editor;
        _workspace = workspace;
        _files = files;
        _query = query;
        _commands = commands;
        Conflict = new DiffConflictViewModel(files, git, ClearDiffForConflict, SetStatus);
        _journal.Changed += (_, _) => DispatchRefresh();
        _git.RepositoryChanged += (_, _) => DispatchRefresh();
    }

    private void ClearDiffForConflict()
    {
        Hunks.Clear();
        OnPropertyChanged(nameof(CanStageHunks));
        DiffRows.Clear();
        SideRows.Clear();
    }

    private IDisposable? _live;

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    public void StartLiveTracking() => _live ??= _git.TrackLiveChanges();

    public void StopLiveTracking()
    {
        _live?.Dispose();
        _live = null;
    }

    private void DispatchRefresh()
    {
        if (!_loaded) return;
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
    }

    partial void OnSourceChanged(DiffSource value)
    {
        OnPropertyChanged(nameof(IsGitMode));
        OnPropertyChanged(nameof(IsAiMode));
        OnPropertyChanged(nameof(IsCompareMode));
        // 比較を見ているかどうかで意味が変わる（帯を畳む・比較専用の操作を殺す）。
        OnPropertyChanged(nameof(HasComparison));
        OnPropertyChanged(nameof(CompareCaption));
        _changeCursor = -1;
        if (value != DiffSource.Git)
        {
            _commitRange = null;
            OnPropertyChanged(nameof(CanOpenCommitInGit));
            GitTargetLabel = "";
        }
        UpdateCanDiscard();
        if (!_suppressModeChangeRefresh)
            _ = RefreshAsync();
    }

    partial void OnSelectedFileChanged(DiffFileItem? value)
    {
        _changeCursor = -1; // ファイルが変わったら次/前ジャンプの位置をリセット
        if (_pendingJumpFile is not null && !ReferenceEquals(value, _pendingJumpFile))
        {
            _pendingJumpFile = null;
            _pendingJumpNewLine = -1;
        }
        UpdateCanDiscard();
        InvalidateWorkingTreePatch(value); // 開き直すたびに作業ツリーの最新内容を読み直す
        _ = LoadAndAutoJumpAsync(value);
    }

    private void UpdateCanDiscard()
    {
        var workingTree = IsGitMode && _commitRange is null;
        CanDiscardSelected = workingTree && SelectedFile?.Entry is not null;
        CanDiscardLines = workingTree
            && SelectedFile is { IsStaged: false, Entry: { IsUntracked: false } };
    }

    partial void OnIsSideBySideChanged(bool value)
    {
        _changeCursor = -1;
        _ = LoadAndAutoJumpAsync(SelectedFile);
    }

    /// <summary>差分を読み込み、最初の変更への自動ジャンプを要求する。</summary>
    private async Task LoadAndAutoJumpAsync(DiffFileItem? item)
    {
        await Conflict.LoadAsync(item, LoadDiffAsync);
        if (item is not null)
            AutoJumpRequested?.Invoke();
    }

    /// <summary>Git コミット範囲の差分を表示する。</summary>
    public void ShowCommitRange(string? fromHash, string toHash, string label)
    {
        _loaded = true;
        _commitRange = (fromHash, toHash);
        OnPropertyChanged(nameof(CanOpenCommitInGit));
        GitTargetLabel = label;
        UpdateCanDiscard();
        if (!IsGitMode)
            IsGitMode = true;  // OnIsGitModeChanged 経由で更新が走る
        else
            _ = RefreshAsync();
    }

    /// <summary>
    /// エディタの blame クリックから：1コミットの差分を表示し、そのファイルを選択して、
    /// コミット時点の行番号 <paramref name="lineInCommit"/>（新側・1始まり）の行へスクロールする。
    /// ファイルが一致しない（リネーム等）ときは既定の選択、行が差分に見つからないときは
    /// 通常の「最初の変更へ」の自動ジャンプにフォールバックする。
    /// ペインの表示は呼び出し側（ShellWindow）が行う。
    /// </summary>
    public async Task ShowCommitFileAsync(string hash, string label, string? filePath, int lineInCommit)
    {
        // マルチルート：filePath が現在の Git 操作対象と違うワークスペースフォルダーに属していたら、
        // そのフォルダーのリポジトリへ切り替えてからコミットを引く（さもないと hash が別リポジトリの
        // ものとして解決され、コミット範囲の変更に一致しない＝下の target が見つからない）。
        if (!string.IsNullOrEmpty(filePath))
            _git.SetActiveRootForPath(filePath);
        _loaded = true;
        _commitRange = (null, hash);
        OnPropertyChanged(nameof(CanOpenCommitInGit));
        GitTargetLabel = label;
        UpdateCanDiscard();
        if (!IsGitMode)
        {
            // 生成された setter を通すと OnIsGitModeChanged が RefreshAsync を fire-and-forget で
            // 走らせて下の await と競合するため、この変更での自動更新だけ抑止する。
            _suppressModeChangeRefresh = true;
            try
            {
                IsGitMode = true;
            }
            finally
            {
                _suppressModeChangeRefresh = false;
            }
        }
        await RefreshAsync();

        var display = _query.ToDisplayPath(filePath ?? "");
        var target = Files.FirstOrDefault(f =>
            string.Equals(f.DisplayPath, display, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        _pendingJumpNewLine = lineInCommit;
        _pendingJumpFile = target;
        if (!ReferenceEquals(SelectedFile, target))
            SelectedFile = target;              // OnSelectedFileChanged → 読込 → 自動ジャンプで消費
        else
            await LoadAndAutoJumpAsync(target); // 既に選択済みでも読み直してジャンプさせる
    }

    /// <summary>
    /// サイドバー Git パネルから：作業ツリーの特定ファイルの差分を表示する。
    /// コミット範囲を解除して作業ツリー（Git）モードへ切り替え、一覧を読み直してそのファイルを選択する。
    /// ペインの表示は呼び出し側（ShellWindow）が行う。
    /// </summary>
    public async Task ShowWorkingTreeFileAsync(GitChangeEntry entry, bool isStaged)
    {
        _loaded = true;
        _commitRange = null;
        GitTargetLabel = "";
        IsGitMode = true;
        await RefreshAsync();
        SelectedFile = Files.FirstOrDefault(f =>
            f.IsStaged == isStaged
            && string.Equals(f.DisplayPath, entry.Path, StringComparison.OrdinalIgnoreCase))
            ?? SelectedFile;
    }

    // ===== アドホック比較（クリップボード ↔ 選択範囲 など） =====

    private DiffComparison? _comparison;

    /// <summary>比較の素材が入っているか（左右入替・比較し直しが使えるか）。
    /// 素材は Git／AI変更へ切り替えても保持するが、<b>今それを見ていない間は false</b>——
    /// git 差分を見ているときに「左右を入れ替える」が押せると、押した瞬間に見ていた差分が消えるため。</summary>
    public bool HasComparison => Source == DiffSource.Compare && _comparison is not null;

    /// <summary>差分本体の上に出す「どちらが左でどちらが右か」の帯。比較を見ていなければ空
    /// （空でないと帯が出たままになり、git 差分を別の何かだと名乗ってしまう）。</summary>
    public string CompareCaption => HasComparison ? _comparison!.Caption : "";

    /// <summary>
    /// 任意のテキスト2つを比較して表示する（左＝旧・右＝新）。ペインの表示・フォーカスは呼び出し側が行う
    /// ——「素材を別のペインへ渡す」動線の受け口なので、渡す側が見せ方を決める（設計書 §23.3）。
    /// </summary>
    public void ShowComparison(DiffComparison comparison)
    {
        _comparison = comparison;
        _loaded = true;
        // Source の変更で走る自動更新は抑止して、下の force 付き1回にまとめる
        // （同じ内容の一覧に見えても本文が変わり得るので、こちらは必ず組み直す）。
        _suppressModeChangeRefresh = true;
        try
        {
            Source = DiffSource.Compare;
        }
        finally
        {
            _suppressModeChangeRefresh = false;
        }
        OnPropertyChanged(nameof(HasComparison));
        OnPropertyChanged(nameof(CompareCaption));
        SetStatus("", isError: false);
        _ = RefreshAsync(force: true);
    }

    private DiffFileList LoadComparison()
        => _comparison is { } comparison
            ? new DiffFileList(new[] { BuildCompareItem(comparison) }, "")
            : new DiffFileList(
                Array.Empty<DiffFileItem>(),
                "比較する内容がありません。エディタやターミナルで選択して右クリック →"
                + "「Diff へ送る（クリップボードと比較）」から作れます。");

    private static DiffFileItem BuildCompareItem(DiffComparison comparison)
    {
        var (added, removed) = DiffUtil.Stat(comparison.LeftText, comparison.RightText);
        return new DiffFileItem
        {
            FullPath = comparison.FilePath,
            DisplayPath = comparison.DisplayPath,
            Badge = added == 0 && removed == 0 ? "同一" : "比較",
            Stats = $"+{added} −{removed}",
            IsCompare = true,
            FileIsLeft = comparison.FileIsLeft,
            OldContent = comparison.LeftText,
            NewContent = comparison.RightText,
        };
    }

    /// <summary>
    /// 一覧で選んでいるファイルの「今のディスク上の内容」をクリップボードと比較する
    /// （Git 差分の一覧から、そのファイルとコピーしてきた案をそのまま突き合わせられる）。
    /// </summary>
    [RelayCommand]
    private async Task CompareFileWithClipboardAsync(DiffFileItem? item)
    {
        item ??= SelectedFile;
        if (item is not { FullPath.Length: > 0 })
        {
            SetStatus("比較できるファイルが選ばれていません。", isError: true);
            return;
        }
        if (ClipboardText.TryGet() is not { } clipboard)
        {
            SetStatus("クリップボードにテキストがありません。", isError: true);
            return;
        }
        string content;
        try
        {
            content = await _files.ReadTextAsync(item.FullPath);
        }
        catch (Exception ex)
        {
            SetStatus($"ファイルを読めませんでした: {ex.Message}", isError: true);
            return;
        }
        ShowComparison(new DiffComparison(
            Path.GetFileName(item.FullPath), content, "クリップボード", clipboard,
            item.FullPath, FileIsLeft: true));
    }

    /// <summary>左右を入れ替えて比較し直す（どちらを「元」と見るかは見ている人が決める）。</summary>
    [RelayCommand]
    private void SwapComparison()
    {
        if (_comparison is { } comparison)
            ShowComparison(comparison.Swapped());
    }

    /// <summary>右側だけ今のクリップボードで置き換えて比較し直す（コピーし直したときの一手）。</summary>
    [RelayCommand]
    private void RecompareWithClipboard()
    {
        if (_comparison is not { } comparison) return;
        if (ClipboardText.TryGet() is not { } text)
        {
            SetStatus("クリップボードにテキストがありません。", isError: true);
            return;
        }
        ShowComparison(comparison.WithRight("クリップボード", text));
    }

    [RelayCommand]
    private void ClearGitTarget()
    {
        if (_commitRange is null) return;
        _commitRange = null;
        OnPropertyChanged(nameof(CanOpenCommitInGit));
        GitTargetLabel = "";
        UpdateCanDiscard();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void OpenCommitInGit()
    {
        if (_commitRange is { From: null, To: var hash })
            CommitOpenInGitRequested?.Invoke(this, hash);
    }

    /// <param name="force">一覧の中身が同じでも組み直す。アドホック比較の作り直しのように、
    /// 見出しが同じまま本文だけ変わり得るときに使う。</param>
    private async Task RefreshAsync(bool force = false)
    {
        _loaded = true;
        _patchCache.Clear();
        var selectedPath = SelectedFile?.FullPath;

        var result = Source == DiffSource.Compare
            ? LoadComparison()
            : await _query.LoadAsync(IsGitMode, _commitRange);
        var items = result.Items;
        var emptyMessage = result.EmptyMessage;

        if (!force && DiffSessionQuery.SameFiles(items, Files))
        {
            EmptyMessage = Files.Count > 0 ? "" : emptyMessage;
            if (IsGitMode && _commitRange is null)
                await Conflict.LoadAsync(SelectedFile, LoadDiffAsync);
            return;
        }

        Files.Clear();
        DiffFileItem? reselect = null;
        foreach (var item in items)
        {
            Files.Add(item);
            if (selectedPath is not null
                && string.Equals(item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                reselect = item;
        }

        EmptyMessage = Files.Count > 0 ? "" : emptyMessage;

        SelectedFile = reselect ?? Files.FirstOrDefault();
        if (SelectedFile is null)
        {
            DiffRows.Clear();
            SideRows.Clear();
        }
    }

    // ===== 操作 =====

    [RelayCommand]
    private async Task OpenInEditorAsync(DiffFileItem? item)
    {
        item ??= SelectedFile;
        if (item is null) return;
        if (item.FullPath.Length == 0)
        {
            // アドホック比較で出どころのファイルが無い（クリップボード同士など）。
            SetStatus("この比較には開けるファイルがありません。", isError: true);
            return;
        }
        try { await _editor.OpenFileAsync(item.FullPath); }
        catch (Exception ex) { SetStatus($"エディタで開けませんでした: {ex.Message}", isError: true); }
    }

    /// <summary>差分本体の行に対応するファイル行をエディタで開くよう要求する（ShellWindow が処理）。
    /// 行が特定できないときは行番号 0（ファイルを開くだけ）。</summary>
    public event EventHandler<(string Path, int Line)>? EditorLineOpenRequested;

    /// <summary>
    /// 差分本体の <paramref name="rowIndex"/> 行目（表示中の形式での添字）が指すファイル行をエディタで開く。
    /// 出どころのファイルが無い比較では何もしない。
    /// </summary>
    public void RequestOpenRowInEditor(int rowIndex)
    {
        if (SelectedFile is not { FullPath.Length: > 0 } item)
        {
            SetStatus("この比較には開けるファイルがありません。", isError: true);
            return;
        }
        var line = IsSideBySide
            ? DiffRowLineMapper.LineForSideRow(SideRows, rowIndex, item.FileIsLeft)
            : DiffRowLineMapper.LineForUnifiedRow(DiffRows, rowIndex, item.FileIsLeft);
        EditorLineOpenRequested?.Invoke(this, (item.FullPath, line));
    }

    /// <summary>AI変更の巻き戻し：新規作成ならファイル削除、変更なら変更前の全文を書き戻す。</summary>
    [RelayCommand]
    private async Task RevertAsync(DiffFileItem? item)
    {
        item ??= SelectedFile;
        if (item is not { CanRevert: true }) return;

        var detail = item.IsNew ? "AI が新規作成したファイルなので削除されます。" : "AI 変更前の内容へ書き戻します。";
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.DisplayPath} の AI 変更を元に戻しますか？\n{detail}",
            "AI変更の巻き戻し", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var result = await _commands.RevertAiAsync(item);
        SetStatus(result.Message, !result.Success);
    }

    /// <summary>
    /// Git 作業ツリー（Current）の変更を破棄する：追跡済みは作業ツリーを復元、未追跡は削除。破壊的。
    /// コミット範囲・AI変更モードでは呼べない（<see cref="CanDiscardSelected"/> で抑止）。
    /// </summary>
    [RelayCommand]
    private async Task DiscardAsync(DiffFileItem? item)
    {
        item ??= SelectedFile;
        if (item?.Entry is not { } entry || _commitRange is not null) return;

        var detail = entry.IsUntracked ? "未追跡ファイルなので削除されます。" : "作業ツリーの変更が失われます。";
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.DisplayPath} の変更を破棄しますか？\n{detail}",
            "変更の破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        // 破棄が成功すると GitService が RepositoryChanged を発火し、一覧は自動で読み直される。
        var result = await _commands.DiscardAsync(item);
        SetStatus(result.Message, !result.Success);
    }

    /// <summary>
    /// 統合表示で選択した差分行（<paramref name="selectedRowIndices"/> は <see cref="DiffRows"/> の添字）の変更だけを
    /// 破棄する。選んだ <c>+</c>/<c>-</c> 行を縮約パッチにして <c>git apply --reverse --recount</c> で逆適用する。
    /// </summary>
    public async Task DiscardSelectedLinesAsync(IReadOnlySet<int> selectedRowIndices)
    {
        if (!CanDiscardLines || SelectedFile is not { Entry: not null } item) return;
        if (selectedRowIndices.Count == 0) return;

        // DiffRows は GetPatchTextAsync(item, 3) を改行分割したものと1対1。同じパッチから縮約する。
        var patch = await GetPatchTextAsync(item, 3);
        var reduced = UnifiedPatchEditor.BuildReverseDiscardPatch(patch, selectedRowIndices);
        if (reduced.IsEmpty)
        {
            SetStatus("破棄する変更行が選択されていません（追加・削除行を選んでください）。", isError: false);
            return;
        }

        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.DisplayPath} の選択した {reduced.DiscardedLineCount} 行ぶんの変更を破棄しますか？\n作業ツリーのその変更が失われます。",
            "選択行の破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        // 適用が成功すると GitService が RepositoryChanged を発火し、一覧・差分は自動で読み直される。
        var result = await _commands.ApplyReverseAsync(reduced.Patch,
            $"{item.DisplayPath} の選択した {reduced.DiscardedLineCount} 行を破棄しました。");
        SetStatus(result.Message, !result.Success);
    }

    /// <summary>
    /// 左右並び表示で、変更ブロック（範囲）の旧/新行番号を指定してその範囲の変更だけを破棄する。
    /// <paramref name="oldLines"/> は復活させる削除行の旧行番号、<paramref name="newLines"/> は取り消す追加行の新行番号。
    /// </summary>
    public async Task DiscardSideLinesAsync(IReadOnlySet<int> oldLines, IReadOnlySet<int> newLines)
    {
        if (!CanDiscardLines || SelectedFile is not { Entry: not null } item) return;
        if (oldLines.Count == 0 && newLines.Count == 0) return;

        var patch = await GetPatchTextAsync(item, 3);
        var reduced = UnifiedPatchEditor.BuildReverseDiscardPatchForLines(patch, oldLines, newLines);
        if (reduced.IsEmpty) return;

        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.DisplayPath} のこの範囲（{reduced.DiscardedLineCount} 行）の変更を破棄しますか？\n作業ツリーのその変更が失われます。",
            "範囲の破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var result = await _commands.ApplyReverseAsync(reduced.Patch,
            $"{item.DisplayPath} のこの範囲（{reduced.DiscardedLineCount} 行）を破棄しました。");
        SetStatus(result.Message, !result.Success);
    }

    /// <summary>AI変更の記録をすべて消す（ファイル自体は変更しない）。</summary>
    [RelayCommand]
    private void ClearAiChanges()
    {
        _commands.ClearAiChanges();
        SetStatus("AI変更の記録をクリアしました。", isError: false);
    }

    /// <summary>View（右クリック操作など）からペイン下部の実行結果メッセージを出す。</summary>
    public void SetStatusMessage(string message, bool isError) => SetStatus(message, isError);

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}
