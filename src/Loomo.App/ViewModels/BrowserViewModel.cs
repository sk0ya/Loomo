using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ブックマーク／履歴／候補の1行（一覧・ドロップダウンの表示単位）。</summary>
public sealed partial class BrowserLinkViewModel : ObservableObject
{
    public required string Url { get; init; }
    public string? Title { get; init; }
    public bool IsBookmark { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title!;
    public string Glyph => IsBookmark ? "★" : "🕘";

    /// <summary>見出しが URL と同じ行は下段を出さない（高密度・予約幅なしの流儀）。</summary>
    public string SubText => DisplayTitle == Url ? "" : Url;
}

/// <summary>ダウンロード1件。WebView2 の <see cref="CoreWebView2DownloadOperation"/> は
/// UI スレッド専用なので、通知は必ず UI スレッド上で受けてこの VM を更新する。</summary>
public sealed partial class BrowserDownloadViewModel : ObservableObject
{
    public required string Url { get; init; }

    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;          // 0..100
    [ObservableProperty] private bool _isIndeterminate;     // 総バイト数が不明なとき
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private string? _filePath;

    /// <summary>完了後の「開く」等が使えるか（実体が残っている完了ぶんだけ）。</summary>
    public bool CanOpenFile => IsCompleted && !string.IsNullOrEmpty(FilePath);

    partial void OnIsCompletedChanged(bool value) => OnPropertyChanged(nameof(CanOpenFile));
    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(CanOpenFile));

    internal CoreWebView2DownloadOperation? Operation { get; set; }
}

/// <summary>
/// ブラウザペインの状態（設計書 §21）。ツールバーの活性・読み込み中・ズーム・ページ内検索、
/// ブックマークと訪問履歴（<see cref="BrowserLibraryStore"/>）、アドレス欄の候補、
/// ダウンロード一覧を持つ。
///
/// <para>WebView2 そのものは ShellWindow が持つ（タブの実体・遅延実体化・可視ペインとの結びつきは
/// シェルの責務）。この VM は<b>見えている状態</b>と<b>覚えておく資産</b>だけを持ち、実行は
/// イベントでシェルへ委ねる——ペグボードと同じ分担。</para>
/// </summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private readonly BrowserLibraryStore _store;
    private BrowserLibrarySnapshot? _library;

    public BrowserViewModel() : this(new BrowserLibraryStore()) { }

    public BrowserViewModel(BrowserLibraryStore store) => _store = store;

    // ── ツールバーの状態 ───────────────────────────────────────────────
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBookmarked;
    [ObservableProperty] private int _zoomPercent = 100;

    /// <summary>等倍のときはズーム表示を出さない（予約幅を作らない）。</summary>
    public bool ShowZoom => ZoomPercent != 100;
    partial void OnZoomPercentChanged(int value) => OnPropertyChanged(nameof(ShowZoom));

    public string BookmarkGlyph => IsBookmarked ? "★" : "☆";
    public string BookmarkTip => IsBookmarked ? "ブックマークを外す（Ctrl+D）" : "ブックマークに追加（Ctrl+D）";
    partial void OnIsBookmarkedChanged(bool value)
    {
        OnPropertyChanged(nameof(BookmarkGlyph));
        OnPropertyChanged(nameof(BookmarkTip));
    }

    // ── ブックマーク・履歴 ─────────────────────────────────────────────
    public ObservableCollection<BrowserLinkViewModel> Bookmarks { get; } = new();
    public ObservableCollection<BrowserLinkViewModel> History { get; } = new();
    public ObservableCollection<BrowserLinkViewModel> Suggestions { get; } = new();

    [ObservableProperty] private bool _isLibraryOpen;
    [ObservableProperty] private bool _isSuggestionsOpen;

    /// <summary>一覧を開いた時点で（まだなら）browser.json を読み、閉じている間の変化を反映する。</summary>
    partial void OnIsLibraryOpenChanged(bool value)
    {
        if (!value)
            return;
        _ = Library;
        RefreshLists();
    }

    /// <summary>ドロップダウンの一覧に出す最大件数（履歴は新しい順に切る）。</summary>
    public const int MaxHistoryShown = 40;

    /// <summary>一覧・候補の行を開く要求（bool は「新しいタブで開くか」）。</summary>
    public event EventHandler<(string Url, bool NewTab)>? OpenUrlRequested;

    /// <summary>ダウンロード済みファイルをエディタで開く要求。</summary>
    public event EventHandler<string>? OpenFileInEditorRequested;

    /// <summary>ページ内検索の状態変化（語の変更・次/前・閉じる）。ShellWindow が WebView2 へ流す。</summary>
    public event EventHandler? FindChanged;
    public event EventHandler<int>? FindStepRequested;   // +1 = 次, -1 = 前

    /// <summary>初回アクセスで browser.json を読む（起動時には読まない＝起動を遅くしない）。
    /// 読み込みは「使うとき」——最初のナビゲーション完了・アドレス欄への入力・一覧を開いたとき。</summary>
    private BrowserLibrarySnapshot Library
    {
        get
        {
            if (_library is null)
            {
                _library = _store.Load();
                RefreshLists();
                // 読む前に表示していたページの★は分からないままだったので、ここで揃える。
                IsBookmarked = CurrentUrl is not null
                    && _library.Bookmarks.Any(b => BrowserLibrary.SameUrl(b.Url, CurrentUrl));
            }
            return _library;
        }
    }

    /// <summary>いま見ているページを差し替える（★の状態もここで揃う）。履歴には触らない——
    /// タブを切り替えただけ・タイトルが後から確定しただけで訪問回数が増えてはいけない。
    /// 復元中のタブ切替でも呼ばれるので、<b>ここでは browser.json を読み込まない</b>
    /// （未読込なら★は消えたまま。読んだ時点で <see cref="Library"/> が揃え直す）。</summary>
    public void SetCurrentPage(string? url, string? title)
    {
        CurrentUrl = url;
        CurrentTitle = title;
        IsBookmarked = _library is not null && url is not null
            && _library.Bookmarks.Any(b => BrowserLibrary.SameUrl(b.Url, url));
    }

    /// <summary>ページを訪れた（ナビゲーション成功）ことを記録する。</summary>
    public void RecordVisit(string? url, string? title)
    {
        _ = Library;                     // ★の判定に要るので、ここで読み込む
        SetCurrentPage(url, title);
        if (!BrowserLibrary.IsRecordable(url))
            return;
        Library.History = BrowserLibrary.RecordVisit(Library.History, url!, title, DateTime.UtcNow);
        RefreshLists();
        Persist();
    }

    /// <summary>タイトルが後から確定したときに履歴の見出しだけ揃える（訪問回数は増やさない）。
    /// <c>DocumentTitleChanged</c> はナビゲーション完了より後に、しかも複数回来る。
    ///
    /// <para><b>書き込むのは「まだ見出しが無い履歴に最初の題が付いたとき」だけ</b>。未読件数や時計を
    /// タイトルに出すページ（メール・チャット・ダッシュボード）は題を延々と打ち替えるので、
    /// 変わるたびに保存すると <c>browser.json</c> 全体の直列化＋書き出しを UI スレッドで
    /// 秒間何度も回すことになる。表示（メモリ上）は常に最新へ、ファイルは最初の題で足りる。</para></summary>
    public void UpdateCurrentTitle(string? url, string? title)
    {
        _ = Library;
        SetCurrentPage(url, title);
        if (string.IsNullOrWhiteSpace(title) || !BrowserLibrary.IsRecordable(url))
            return;
        var entry = Library.History.FirstOrDefault(h => BrowserLibrary.SameUrl(h.Url, url));
        if (entry is null || entry.Title == title)
            return;
        var wasUntitled = string.IsNullOrWhiteSpace(entry.Title);
        entry.Title = title;
        // 一覧を開いていないときは作り直さない（閉じている間の題の変化は、開くときにまとめて反映する）。
        if (IsLibraryOpen)
            RefreshLists();
        if (wasUntitled)
            Persist();
    }

    /// <summary>いま表示しているページ（★の対象）。</summary>
    public string? CurrentUrl { get; private set; }
    public string? CurrentTitle { get; private set; }

    /// <summary>表示中ページのブックマークを付け外しする（Ctrl+D・☆ボタン）。</summary>
    [RelayCommand]
    public void ToggleBookmark()
    {
        if (!BrowserLibrary.IsRecordable(CurrentUrl))
            return;
        _ = Library;
        var existing = Library.Bookmarks.FirstOrDefault(b => BrowserLibrary.SameUrl(b.Url, CurrentUrl));
        if (existing is not null)
            Library.Bookmarks.Remove(existing);
        else
            Library.Bookmarks.Insert(0, new BrowserBookmark
            {
                Url = CurrentUrl!,
                Title = string.IsNullOrWhiteSpace(CurrentTitle) ? null : CurrentTitle,
            });
        IsBookmarked = existing is null;
        RefreshLists();
        Persist();
    }

    [RelayCommand]
    private void OpenLink(BrowserLinkViewModel? item)
    {
        if (item is null)
            return;
        IsLibraryOpen = false;
        IsSuggestionsOpen = false;
        OpenUrlRequested?.Invoke(this, (item.Url, false));
    }

    [RelayCommand]
    private void OpenLinkInNewTab(BrowserLinkViewModel? item)
    {
        if (item is null)
            return;
        IsLibraryOpen = false;
        OpenUrlRequested?.Invoke(this, (item.Url, true));
    }

    [RelayCommand]
    private void RemoveLink(BrowserLinkViewModel? item)
    {
        if (item is null)
            return;
        if (item.IsBookmark)
            Library.Bookmarks.RemoveAll(b => BrowserLibrary.SameUrl(b.Url, item.Url));
        else
            Library.History.RemoveAll(h => BrowserLibrary.SameUrl(h.Url, item.Url));
        if (BrowserLibrary.SameUrl(item.Url, CurrentUrl) && item.IsBookmark)
            IsBookmarked = false;
        RefreshLists();
        Persist();
    }

    [RelayCommand]
    private void CopyLink(BrowserLinkViewModel? item)
    {
        if (item is null)
            return;
        try { Clipboard.SetText(item.Url); } catch { /* クリップボード占有中などは無視 */ }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        Library.History.Clear();
        RefreshLists();
        Persist();
    }

    /// <summary>アドレス欄に打った文字から候補を組み直す。</summary>
    public void UpdateSuggestions(string? query)
    {
        var suggestions = BrowserLibrary.Suggest(Library.Bookmarks, Library.History, query);
        Suggestions.Clear();
        foreach (var s in suggestions)
            Suggestions.Add(new BrowserLinkViewModel { Url = s.Url, Title = s.Title, IsBookmark = s.IsBookmark });
        IsSuggestionsOpen = Suggestions.Count > 0;
    }

    private void RefreshLists()
    {
        var library = _library ?? new BrowserLibrarySnapshot();
        Bookmarks.Clear();
        foreach (var b in library.Bookmarks)
            Bookmarks.Add(new BrowserLinkViewModel { Url = b.Url, Title = b.Title, IsBookmark = true });
        History.Clear();
        foreach (var h in library.History.Take(MaxHistoryShown))
            History.Add(new BrowserLinkViewModel { Url = h.Url, Title = h.Title, IsBookmark = false });
        OnPropertyChanged(nameof(HasBookmarks));
        OnPropertyChanged(nameof(HasHistory));
    }

    public bool HasBookmarks => Bookmarks.Count > 0;
    public bool HasHistory => History.Count > 0;

    private void Persist()
    {
        if (_library is not null)
            _store.Save(_library);
    }

    // ── ページ内検索（Ctrl+F） ─────────────────────────────────────────
    [ObservableProperty] private bool _isFindOpen;
    [ObservableProperty] private string _findTerm = "";
    [ObservableProperty] private string _findLabel = "";

    partial void OnFindTermChanged(string value) => FindChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void FindNext() => FindStepRequested?.Invoke(this, 1);

    [RelayCommand]
    private void FindPrevious() => FindStepRequested?.Invoke(this, -1);

    [RelayCommand]
    public void CloseFind()
    {
        IsFindOpen = false;
        FindTerm = "";
        FindLabel = "";
    }

    /// <summary>検索の当たり具合を表示に反映する（0 件のときは「一致なし」）。</summary>
    public void SetFindMatches(int activeIndex, int matchCount)
        => FindLabel = string.IsNullOrEmpty(FindTerm) ? ""
            : matchCount <= 0 ? "一致なし"
            : $"{activeIndex}/{matchCount}";

    // ── ダウンロード ───────────────────────────────────────────────────
    public ObservableCollection<BrowserDownloadViewModel> Downloads { get; } = new();

    [ObservableProperty] private bool _isDownloadsOpen;

    /// <summary>受信中の件数（ツールバーのバッジ）。0 のときボタン自体を出さない。</summary>
    public int ActiveDownloadCount => Downloads.Count(d => d.IsActive);
    public bool HasDownloads => Downloads.Count > 0;
    public string DownloadBadge => ActiveDownloadCount > 0 ? ActiveDownloadCount.ToString() : "";

    /// <summary>ダウンロードの増減・状態変化を表示へ反映する（ShellWindow が UI スレッドから呼ぶ）。</summary>
    public void NotifyDownloadsChanged()
    {
        OnPropertyChanged(nameof(ActiveDownloadCount));
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(DownloadBadge));
    }

    [RelayCommand]
    private void CancelDownload(BrowserDownloadViewModel? item)
    {
        if (item?.Operation is not { } operation)
            return;
        try { operation.Cancel(); } catch { /* 既に完了・中断済み */ }
    }

    [RelayCommand]
    private void OpenDownload(BrowserDownloadViewModel? item)
    {
        if (item?.FilePath is not { Length: > 0 } path || !File.Exists(path))
            return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* 関連付けが無い等でも落とさない */ }
    }

    [RelayCommand]
    private void ShowDownloadInFolder(BrowserDownloadViewModel? item)
    {
        if (item?.FilePath is not { Length: > 0 } path || !File.Exists(path))
            return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* 無視 */ }
    }

    [RelayCommand]
    private void OpenDownloadInEditor(BrowserDownloadViewModel? item)
    {
        if (item?.FilePath is not { Length: > 0 } path || !File.Exists(path))
            return;
        IsDownloadsOpen = false;
        OpenFileInEditorRequested?.Invoke(this, path);
    }

    [RelayCommand]
    private void ClearFinishedDownloads()
    {
        foreach (var done in Downloads.Where(d => !d.IsActive).ToList())
            Downloads.Remove(done);
        NotifyDownloadsChanged();
    }
}
