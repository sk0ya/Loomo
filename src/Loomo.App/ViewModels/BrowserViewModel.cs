using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ブックマーク一覧の1行に共通するもの（＝段の深さ）。フォルダーの行とブックマークの行を
/// <b>1つの ItemsControl に均して流す</b>ための土台。入れ子の ItemsControl にしないのは、行の中から
/// コマンド（開く・消す・畳む）へ辿る道を「一覧の DataContext」一段に保つため——入れ子にすると
/// <c>AncestorType=ItemsControl</c> が内側の一覧に当たって、行のボタンが黙って効かなくなる。</summary>
public abstract partial class BrowserRowViewModel : ObservableObject
{
    /// <summary>根から数えた段（0 が一番上）。</summary>
    public int Depth { get; init; }

    /// <summary>字下げ。行の見た目の左端だけを寄せる（幅は変えない）。</summary>
    public Thickness IndentMargin => new(Depth * 14, 0, 0, 0);
}

/// <summary>ブックマーク一覧のフォルダー行（設計書 §21.5.1）。畳んだままでも中の件数は見せる。</summary>
public sealed partial class BrowserBookmarkFolderViewModel : BrowserRowViewModel
{
    public required string Name { get; init; }

    /// <summary>展開状態を覚えておくための鍵（<see cref="BrowserBookmarkTree.Key"/>）。</summary>
    public required string Key { get; init; }

    /// <summary>この下（入れ子を含む）のブックマーク数。</summary>
    public required int Count { get; init; }

    public required bool IsExpanded { get; init; }

    public string Glyph => IsExpanded ? "▾" : "▸";
    public string FolderGlyph => IsExpanded ? "📂" : "📁";
    public string CountText => Count.ToString();
}

/// <summary>ブックマーク／履歴／候補の1行（一覧・ドロップダウンの表示単位）。</summary>
public sealed partial class BrowserLinkViewModel : BrowserRowViewModel
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

/// <summary>拡張機能1件（一覧の行）。</summary>
public sealed partial class BrowserExtensionViewModel : ObservableObject
{
    /// <param name="isEnabled">初期値はフィールドへ直接入れる——プロパティ経由で入れると、
    /// 一覧を作り直しただけで「使う側が切り替えた」通知が飛ぶ。</param>
    public BrowserExtensionViewModel(bool isEnabled) => _isEnabled = isEnabled;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? FolderPath { get; init; }

    /// <summary>ボタンを押したときに開くページ（<c>chrome-extension://&lt;ID&gt;/…</c>）。
    /// 持たない拡張機能（内容スクリプトだけのもの）もある。</summary>
    public string? PopupUrl { get; init; }
    public bool HasPopup => PopupUrl is not null;

    /// <summary>設定画面（manifest の <c>options_ui.page</c>／<c>options_page</c>）。
    /// <b>ここが唯一の入口</b>——WebView2 には <c>chrome://extensions</c> も拡張機能のツールバーも無い。</summary>
    public string? OptionsUrl { get; init; }
    public bool HasOptions => OptionsUrl is not null;

    /// <summary>一覧に出すアイコン（拡張機能フォルダーの中の画像ファイル）。
    /// WebView2 に導入済みでもフォルダーの記録が無いもの（Edge に元から入っているもの）は持たない。</summary>
    public string? IconPath { get; init; }
    public bool HasIcon => IconPath is not null;

    [ObservableProperty] private bool _isEnabled;

    /// <summary>使う側が有効/無効を切り替えた。シェルが WebView2 へ流す。</summary>
    internal Action<BrowserExtensionViewModel>? EnabledChanged;
    partial void OnIsEnabledChanged(bool value) => EnabledChanged?.Invoke(this);

    /// <summary>切り替えが WebView2 側で通らなかったときに表示を戻す。
    /// <b>通知は起こさない</b>——戻した値でもう一度切り替え要求が飛ぶと堂々巡りになるうえ、
    /// 戻したことが「使う側の操作」として記録されてしまう。</summary>
    internal void RevertEnabled(bool value)
    {
        var handler = EnabledChanged;
        EnabledChanged = null;
        try { IsEnabled = value; }
        finally { EnabledChanged = handler; }
    }

    public string SubText => string.IsNullOrEmpty(Version) ? Id : $"{Version} · {Id}";
}

/// <summary>保存済みログイン情報1件（既定では伏せて出す）。</summary>
public sealed partial class SavedPasswordViewModel : ObservableObject
{
    public required string Origin { get; init; }
    public required string Host { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }

    [ObservableProperty] private bool _isRevealed;
    partial void OnIsRevealedChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayPassword));
        OnPropertyChanged(nameof(RevealGlyph));
    }

    /// <summary>伏せ字の長さは実際の桁数を晒さない（一覧を覗かれても手掛かりにならないように）。</summary>
    public string DisplayPassword => IsRevealed ? Password : "••••••••";
    public string RevealGlyph => IsRevealed ? "🙈" : "👁";
    public string DisplayUsername => string.IsNullOrEmpty(Username) ? "(ユーザー名なし)" : Username;
}

/// <summary>
/// ブラウザペインの状態（設計書 §21）。ツールバーの活性・読み込み中・ズーム・ページ内検索、
/// ブックマークと訪問履歴（<see cref="BrowserLibraryStore"/>）、アドレス欄の候補、
/// ダウンロード一覧、拡張機能と保存済みログイン情報の一覧を持つ。
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

    /// <summary>他のブラウザからの取り込み（§21.5.4）。選ばせるところまでが VM の仕事で、
    /// 実際に読んで書くのはシェル（プロファイルと WebView2 に触るため）。</summary>
    public BrowserImportViewModel Import { get; } = new();

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
    /// <summary>ブックマーク一覧の行（フォルダーの行とブックマークの行が深さ順に並んだもの）。
    /// 木そのものではなく<b>均した行の列</b>を持つ——理由は <see cref="BrowserRowViewModel"/>。</summary>
    public ObservableCollection<BrowserRowViewModel> Bookmarks { get; } = new();
    public ObservableCollection<BrowserLinkViewModel> History { get; } = new();
    public ObservableCollection<BrowserLinkViewModel> Suggestions { get; } = new();

    [ObservableProperty] private bool _isLibraryOpen;
    [ObservableProperty] private bool _isSuggestionsOpen;

    /// <summary>いま開いているフォルダー（<see cref="BrowserBookmarkTree.Key"/> の鍵）。
    /// <b>既定は全部畳んだ状態</b>——取り込んだ直後の数百件が一覧を埋め尽くさないように
    /// （エクスプローラのツリーと同じ流儀）。覚えるのはアプリを動かしている間だけで、
    /// browser.json には書かない：どこを開いていたかは資産ではなく、いまの見え方にすぎない。</summary>
    private readonly HashSet<string> _expandedFolders = new(StringComparer.Ordinal);

    /// <summary>フォルダーの行を押した（開く／畳む）。</summary>
    [RelayCommand]
    private void ToggleBookmarkFolder(BrowserBookmarkFolderViewModel? folder)
    {
        if (folder is null)
            return;
        if (!_expandedFolders.Remove(folder.Key))
            _expandedFolders.Add(folder.Key);
        RefreshLists();
    }

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
        foreach (var row in BrowserBookmarkTree.Flatten(
                     BrowserBookmarkTree.Build(library.Bookmarks), _expandedFolders))
            Bookmarks.Add(row.Folder is { } folder
                ? new BrowserBookmarkFolderViewModel
                {
                    Depth = row.Depth,
                    Name = folder.Name,
                    Key = BrowserBookmarkTree.Key(folder.Path),
                    Count = folder.TotalCount,
                    IsExpanded = _expandedFolders.Contains(BrowserBookmarkTree.Key(folder.Path)),
                }
                : new BrowserLinkViewModel
                {
                    Depth = row.Depth,
                    Url = row.Bookmark!.Url,
                    Title = row.Bookmark.Title,
                    IsBookmark = true,
                });
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

    /// <summary>取り込んだブックマーク・履歴を、いま持っているものへ混ぜて保存する（§21.5.4）。
    /// <b>browser.json の持ち主はこの VM だけ</b>なので、取り込み側がファイルへ直接書くことはしない
    /// ——そうすると、この VM が抱えている古い実体が次の保存で取り込みぶんを消し飛ばす。</summary>
    public (int Bookmarks, int History) MergeImported(
        IReadOnlyList<BrowserBookmark> bookmarks, IReadOnlyList<BrowserHistoryEntry> history)
    {
        var library = Library;
        var (mergedBookmarks, addedBookmarks) = BrowserImportMerge.Bookmarks(library.Bookmarks, bookmarks);
        var (mergedHistory, addedHistory) = BrowserImportMerge.History(library.History, history, _store.MaxHistory);
        library.Bookmarks = mergedBookmarks;
        library.History = mergedHistory;
        Persist();
        RefreshLists();
        // 取り込んだブックマークにいま見ているページが含まれていたら、★も点く。
        IsBookmarked = CurrentUrl is not null
            && library.Bookmarks.Any(b => BrowserLibrary.SameUrl(b.Url, CurrentUrl));
        return (addedBookmarks, addedHistory);
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

    // ── 拡張機能（§21.5.2） ───────────────────────────────────────────
    public ObservableCollection<BrowserExtensionViewModel> Extensions { get; } = new();

    [ObservableProperty] private bool _isExtensionsOpen;
    [ObservableProperty] private bool _isExtensionsBusy;
    [ObservableProperty] private string _extensionStatus = "";
    [ObservableProperty] private string _extensionInput = "";

    /// <summary>一覧を開いた／導入や削除の後に、WebView2 から今の顔ぶれを取り直す要求。</summary>
    public event EventHandler? ExtensionsRefreshRequested;
    /// <summary>ストアの URL か ID から入れる要求。</summary>
    public event EventHandler<string>? ExtensionInstallRequested;
    /// <summary>展開済みフォルダーを選んで入れる要求。</summary>
    public event EventHandler? ExtensionFolderInstallRequested;
    public event EventHandler<BrowserExtensionViewModel>? ExtensionEnableChanged;
    public event EventHandler<BrowserExtensionViewModel>? ExtensionRemoveRequested;
    /// <summary>拡張機能のボタン（ポップアップ UI）を開く要求。</summary>
    public event EventHandler<BrowserExtensionViewModel>? ExtensionPopupRequested;

    partial void OnIsExtensionsOpenChanged(bool value)
    {
        if (value)
            ExtensionsRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>シェルが取り直した一覧で差し替える（有効/無効の通知線もここで結び直す）。</summary>
    public void SetExtensions(IEnumerable<BrowserExtensionViewModel> items)
    {
        Extensions.Clear();
        foreach (var item in items)
        {
            item.EnabledChanged = changed => ExtensionEnableChanged?.Invoke(this, changed);
            Extensions.Add(item);
        }
        OnPropertyChanged(nameof(HasExtensions));
    }

    public bool HasExtensions => Extensions.Count > 0;

    [RelayCommand]
    private void InstallExtension()
    {
        if (!string.IsNullOrWhiteSpace(ExtensionInput))
            ExtensionInstallRequested?.Invoke(this, ExtensionInput.Trim());
    }

    [RelayCommand]
    private void InstallExtensionFromFolder() => ExtensionFolderInstallRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>ストアの拡張機能ページを開いたときの促しバー。<b>ストアを見て入れる</b>のが本線で、
    /// URL/ID の貼り付けは手が別にあるとき用。ページ側の「Chrome に追加」を押しても同じ道を通る
    /// （<see cref="ExtensionStoreInstallRequested"/>）。</summary>
    [ObservableProperty] private bool _isExtensionPromptVisible;
    [ObservableProperty] private string _extensionPromptMessage = "";
    [ObservableProperty] private bool _isExtensionPromptInstalled;

    /// <summary>いま見ているストアページの拡張機能を入れる要求（ID はシェルが URL から取り直す）。</summary>
    public event EventHandler? ExtensionStoreInstallRequested;

    public void ShowExtensionPrompt(string name, bool alreadyInstalled)
    {
        ExtensionPromptMessage = alreadyInstalled
            ? $"「{name}」は追加済みです。"
            : $"「{name}」を Loomo のブラウザに追加できます。";
        IsExtensionPromptInstalled = alreadyInstalled;
        IsExtensionPromptVisible = true;
    }

    /// <summary>バーを引っ込める（ページが変わった・追加した）。閉じた事実は覚えない。</summary>
    public void CloseExtensionPrompt() => IsExtensionPromptVisible = false;

    /// <summary>使う側が × で閉じた。<b>こちらだけ覚える</b>——題が後から確定して再評価が走るたびに
    /// 閉じたバーが戻ってくるのを避ける。</summary>
    public event EventHandler? ExtensionPromptDismissed;

    [RelayCommand]
    private void DismissExtensionPrompt()
    {
        IsExtensionPromptVisible = false;
        ExtensionPromptDismissed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void InstallExtensionFromStore() => ExtensionStoreInstallRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>🧩 の一覧から拡張機能ストアを開く。</summary>
    [RelayCommand]
    private void OpenExtensionStore()
    {
        IsExtensionsOpen = false;
        OpenUrlRequested?.Invoke(this, (BrowserExtensionStore.StoreHomeUrl, false));
    }

    [RelayCommand]
    private void RemoveExtension(BrowserExtensionViewModel? item)
    {
        if (item is not null)
            ExtensionRemoveRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private void OpenExtensionPopup(BrowserExtensionViewModel? item)
    {
        if (item?.HasPopup != true)
            return;
        IsExtensionsOpen = false;
        ExtensionPopupRequested?.Invoke(this, item);
    }

    /// <summary>設定画面を<b>新しいタブ</b>で開く。ポップアップの器（400×560）には収まらない作りのものが多く
    /// （uBlock Origin のダッシュボードなど）、設定は腰を据えて触るものなので、部屋のタブとして開く。</summary>
    [RelayCommand]
    private void OpenExtensionOptions(BrowserExtensionViewModel? item)
    {
        if (item?.OptionsUrl is not { Length: > 0 } url)
            return;
        IsExtensionsOpen = false;
        OpenUrlRequested?.Invoke(this, (url, true));
    }

    // ── 保存済みログイン情報（§21.5.2） ───────────────────────────────
    private IReadOnlyList<SavedPasswordViewModel> _allPasswords = Array.Empty<SavedPasswordViewModel>();

    public ObservableCollection<SavedPasswordViewModel> Passwords { get; } = new();

    [ObservableProperty] private bool _isPasswordsOpen;
    [ObservableProperty] private string _passwordStatus = "";
    [ObservableProperty] private string _passwordFilter = "";

    /// <summary>一覧を開いたときに読み込む要求（<b>開くまで復号しない</b>——
    /// 使いもしない平文をずっと抱えない）。</summary>
    public event EventHandler? PasswordsRefreshRequested;
    /// <summary>保存済みログイン情報を全部消す要求（個別削除はプロファイルへの書き込みになるので持たない）。</summary>
    public event EventHandler? PasswordsClearRequested;

    partial void OnIsPasswordsOpenChanged(bool value)
    {
        if (value)
        {
            PasswordsRefreshRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        // 閉じたら平文を手放し、次に開いたときは伏せた状態から始める。
        _allPasswords = Array.Empty<SavedPasswordViewModel>();
        Passwords.Clear();
        PasswordFilter = "";
        OnPropertyChanged(nameof(HasPasswords));
    }

    partial void OnPasswordFilterChanged(string value) => ApplyPasswordFilter();

    public void SetPasswords(IReadOnlyList<SavedPasswordViewModel> items, string? error)
    {
        _allPasswords = items;
        PasswordStatus = error
            ?? (items.Count == 0 ? "保存されたログイン情報はまだありません。" : $"{items.Count} 件");
        ApplyPasswordFilter();
    }

    private void ApplyPasswordFilter()
    {
        var query = PasswordFilter.Trim();
        Passwords.Clear();
        foreach (var item in _allPasswords)
            if (query.Length == 0
                || item.Host.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Username.Contains(query, StringComparison.OrdinalIgnoreCase))
                Passwords.Add(item);
        OnPropertyChanged(nameof(HasPasswords));
    }

    public bool HasPasswords => Passwords.Count > 0;

    [RelayCommand]
    private void TogglePasswordReveal(SavedPasswordViewModel? item)
    {
        if (item is not null)
            item.IsRevealed = !item.IsRevealed;
    }

    [RelayCommand]
    private void CopyPassword(SavedPasswordViewModel? item) => CopyToClipboard(item?.Password, "パスワード");

    [RelayCommand]
    private void CopyUsername(SavedPasswordViewModel? item) => CopyToClipboard(item?.Username, "ユーザー名");

    [RelayCommand]
    private void OpenPasswordSite(SavedPasswordViewModel? item)
    {
        if (item is null)
            return;
        IsPasswordsOpen = false;
        OpenUrlRequested?.Invoke(this, (item.Origin, false));
    }

    [RelayCommand]
    private void ClearPasswords() => PasswordsClearRequested?.Invoke(this, EventArgs.Empty);

    private static void CopyToClipboard(string? text, string label)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            Clipboard.SetText(text);
            ToastService.Success($"{label}をコピーしました。");
        }
        catch
        {
            // クリップボードを他プロセスが握っている間は失敗する。
            ToastService.Error($"{label}をコピーできませんでした。");
        }
    }
}
