using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ファイル一覧（エクスプローラ）ペイン＝<see cref="PaneKind.Files"/> の容れ物。
/// <see cref="FilesColumnViewModel"/> を<b>常に4つ</b>持ち、1／2／4カラムで見せる。
///
/// <para>カラムを毎回作り直さないのは、4→1→4 と往復したときに右側のカラムが開いていた場所を
/// 失わないため（見えていない間も状態は生きている＝「場所としての信頼」）。イベントの購読も
/// 起動時に1回で済む。</para>
///
/// <para>サイドバーのツリーを置き換えるものではない。ツリーは<b>階層を把握する</b>道具、この面は
/// <b>集合を処理する</b>道具——2カラムはその延長で、「左のフォルダーから右のフォルダーへ移す」を
/// 1画面で完結させるためにある。</para></summary>
public sealed partial class FilesPaneViewModel : ObservableObject, IDisposable
{
    /// <summary>用意するカラムの最大数（＝4カラム表示のときの数）。</summary>
    public const int MaxColumns = 4;

    // 復元中はカラムの初期地を推測しない（このあと保存された現在地で上書きするので無駄になる）。
    private bool _restoring;

    public FilesPaneViewModel(
        IWorkspaceService workspace,
        FolderTreeCommandHandler commands,
        IFolderPinStore pins,
        IFilePlacesProvider places)
    {
        for (var i = 0; i < MaxColumns; i++)
        {
            var column = new FilesColumnViewModel(workspace, commands, pins, places);
            // カラムのイベントはペインで束ねて中継する（ShellWindow は列の数を知らなくてよい）。
            column.FileActivated += (_, path) => FileActivated?.Invoke(this, path);
            column.FilePreviewRequested += (_, path) => FilePreviewRequested?.Invoke(this, path);
            column.OpenInBrowserRequested += (_, path) => OpenInBrowserRequested?.Invoke(this, path);
            column.EntryRenamed += (_, e) => EntryRenamed?.Invoke(this, e);
            column.EntryDeleted += (_, path) => EntryDeleted?.Invoke(this, path);
            column.SetInTerminalRequested += (_, request) => SetInTerminalRequested?.Invoke(this, request);
            column.CompareRequested += (_, request) => CompareRequested?.Invoke(this, request);
            column.SearchInFolderRequested += (_, path) => SearchInFolderRequested?.Invoke(this, path);
            column.StateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
            column.Activated += (sender, _) => SetActiveColumn((FilesColumnViewModel)sender!);
            AllColumns.Add(column);
        }
        AllColumns[0].IsActive = true;
        UpdateVisibleColumns();
    }

    /// <summary>常に4つある実体。表示するのは先頭から <see cref="ColumnCount"/> 個。</summary>
    public ObservableCollection<FilesColumnViewModel> AllColumns { get; } = new();

    /// <summary>いま画面に出ているカラム（View はこれを並べる）。</summary>
    public ObservableCollection<FilesColumnViewModel> Columns { get; } = new();

    /// <summary>表示カラム数（1／2／4）。2カラムは左右、4カラムは2×2。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOneColumn))]
    [NotifyPropertyChangedFor(nameof(IsTwoColumns))]
    [NotifyPropertyChangedFor(nameof(IsFourColumns))]
    private int _columnCount = 1;

    /// <summary>操作対象のカラム（キーボード・ツリーからの「ファイル一覧で表示」の行き先）。</summary>
    [ObservableProperty] private FilesColumnViewModel? _activeColumn;

    // ヘッダーのセグメント（1／2／4）用。RadioButton は bool しか見ないので3つに割る。
    public bool IsOneColumn
    {
        get => ColumnCount == 1;
        set { if (value) ColumnCount = 1; }
    }

    public bool IsTwoColumns
    {
        get => ColumnCount == 2;
        set { if (value) ColumnCount = 2; }
    }

    public bool IsFourColumns
    {
        get => ColumnCount == 4;
        set { if (value) ColumnCount = 4; }
    }

    // ShellWindow へ中継するカラム由来のイベント（受け口はツリーと同じ・§26.10）。
    public event EventHandler<string>? FileActivated;
    public event EventHandler<string>? FilePreviewRequested;
    public event EventHandler<string>? OpenInBrowserRequested;
    public event EventHandler<EntryRenamedEventArgs>? EntryRenamed;
    public event EventHandler<string>? EntryDeleted;
    public event EventHandler<TerminalSetRequest>? SetInTerminalRequested;
    public event EventHandler<FileCompareRequest>? CompareRequested;
    public event EventHandler<string>? SearchInFolderRequested;
    public event EventHandler? StateChanged;

    partial void OnColumnCountChanged(int value)
    {
        UpdateVisibleColumns();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SetColumnCount(string? count)
    {
        if (int.TryParse(count, out var value) && value is 1 or 2 or 4)
            ColumnCount = value;
    }

    private void UpdateVisibleColumns()
    {
        var visible = Math.Clamp(ColumnCount, 1, MaxColumns);
        var wasVisible = Columns.Count;
        while (Columns.Count > visible)
            Columns.RemoveAt(Columns.Count - 1);
        while (Columns.Count < visible)
            Columns.Add(AllColumns[Columns.Count]);

        // 増やしたぶんは、まだ出ていないサブフォルダーを開く（下記）。
        if (!_restoring)
            for (var i = wasVisible; i < Columns.Count; i++)
                OpenUnshownSubfolder(Columns[i]);

        // 隠れたカラムが操作対象のままだと、キー操作の行き先が見えない場所になる。
        if (ActiveColumn is null || !Columns.Contains(ActiveColumn))
            SetActiveColumn(Columns[0]);
    }

    /// <summary>カラムを増やしたときの初期地。<b>まだどのカラムにも出ていないサブフォルダー</b>を
    /// 先頭から選ぶ——増やした直後に同じフォルダーが2枚並ぶのでは、並べた意味がない。
    /// 候補は基準カラムの<b>表示順そのまま</b>（絞り込み・並べ替えを反映した `Entries`）から採るので、
    /// 「今見えている上の方のフォルダー」から順に開く。
    ///
    /// <para>すでに場所を覚えているカラム（前回 4 カラムで開いていた等）はそのままにする——
    /// 4→1→4 で戻ってきたら同じ場所、という約束を上書きしない。重複しているときだけ動かす。</para></summary>
    private void OpenUnshownSubfolder(FilesColumnViewModel column)
    {
        var others = Columns.Where(c => !ReferenceEquals(c, column)).ToList();
        var shown = others
            .Select(c => c.CurrentFolder)
            .Where(folder => folder.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 覚えている場所が他と重なっていないなら、それを尊重する。
        if (column.CurrentFolder.Length > 0 && !shown.Contains(column.CurrentFolder))
            return;

        var baseColumn = others.FirstOrDefault(c => c.CurrentFolder.Length > 0);
        if (baseColumn is null)
            return;

        var candidate = baseColumn.Entries
            .Where(entry => entry.IsDirectory && !shown.Contains(entry.FullPath))
            .Select(entry => entry.FullPath)
            .FirstOrDefault();
        if (candidate is not null)
            column.Navigate(candidate);
        else if (column.CurrentFolder.Length == 0)
            column.Navigate(baseColumn.CurrentFolder);   // 開けるサブフォルダーが無ければ基準と同じ場所
    }

    /// <summary>操作対象のカラムを切り替える（クリック・フォーカスで View から呼ばれる）。</summary>
    public void SetActiveColumn(FilesColumnViewModel column)
    {
        if (!AllColumns.Contains(column))
            return;
        ActiveColumn = column;
        foreach (var candidate in AllColumns)
            candidate.IsActive = ReferenceEquals(candidate, column);
    }

    /// <summary>ツリーの「ファイル一覧で表示」などの行き先。操作対象のカラムで開く。</summary>
    public void Reveal(string fullPath) => (ActiveColumn ?? Columns[0]).Reveal(fullPath);

    /// <summary>ワークスペース切替・復元。カラムごとに現在地を戻し、無ければプライマリを開く。</summary>
    public void Restore(FilesPaneSnapshot? snapshot, string? fallbackFolder)
    {
        _restoring = true;
        try
        {
            ColumnCount = snapshot?.ColumnCount is 1 or 2 or 4 ? snapshot.ColumnCount : 1;
            var columns = snapshot?.Columns;
            for (var i = 0; i < AllColumns.Count; i++)
            {
                // 見えていないカラムには既定の場所（プライマリ）を与えない。与えてしまうと、あとで
                // 出したときに「ユーザーが開いていた場所」と見分けが付かず、サブフォルダーの
                // 提案（OpenUnshownSubfolder）が働かなくなる。
                var restored = columns is not null && i < columns.Count ? columns[i] : null;
                AllColumns[i].Restore(restored, i < ColumnCount ? fallbackFolder : null);
            }
        }
        finally
        {
            _restoring = false;
        }

        // 保存が無い（初めて開くワークスペース）カラムは全部プライマリへ倒れるので、
        // ここでも重なりをほどく。覚えている場所は上書きしない。
        for (var i = 1; i < Columns.Count; i++)
            OpenUnshownSubfolder(Columns[i]);

        var active = snapshot?.ActiveColumn ?? 0;
        SetActiveColumn(AllColumns[Math.Clamp(active, 0, Columns.Count - 1)]);
    }

    public FilesPaneSnapshot Capture() => new()
    {
        ColumnCount = ColumnCount,
        ActiveColumn = ActiveColumn is null ? 0 : AllColumns.IndexOf(ActiveColumn),
        Columns = AllColumns.Select(column => column.Capture()).ToList(),
    };

    public void Dispose()
    {
        foreach (var column in AllColumns)
            column.Dispose();
    }
}
