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
    /// <summary>ブックマーク一覧の行か。履歴・候補にも同じ行テンプレートを使うための識別子。</summary>
    public bool IsBookmarkRow { get; init; }

    /// <summary>この行を作った VM。<b>ブックマークバーのフォルダーの中身</b>だけが使う——
    /// そこは Popup の中の一覧で、行のテンプレートから <c>AncestorType=ItemsControl</c> を辿ると
    /// バーの項目（フォルダー）に当たってコマンドが見つからず、押しても黙って何も起きない。</summary>
    public BrowserViewModel? Owner { get; init; }

    [ObservableProperty] private bool _isSelected;
    internal event EventHandler? SelectionChanged;
    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title!;
    public string Glyph => IsBookmark ? "★" : "🕘";

    /// <summary>見出しが URL と同じ行は下段を出さない（高密度・予約幅なしの流儀）。</summary>
    public string SubText => DisplayTitle == Url ? "" : Url;
}

/// <summary>ブックマークバー（アドレス欄の下の帯）の1項目。根の直下にあるブックマークか
/// フォルダーのどちらかで、フォルダーは押すと中身を落とす。
///
/// <para>バーに出すのは<b>根の直下だけ</b>。ブックマーク一覧（ポップアップ）は資産の全体を畳んで
/// 見せる場所で、こちらは「いつも行く数件へ1クリックで届く」ための帯——同じ木を2つの目的で
/// 見せるので、深さの扱いが違う（バーは1段、フォルダーの中身は平らに落とす）。</para></summary>
public sealed partial class BrowserBookmarkBarItemViewModel : ObservableObject
{
    public required BrowserViewModel Owner { get; init; }
    public required string Title { get; init; }

    /// <summary>ブックマークなら行き先、フォルダーなら null。</summary>
    public string? Url { get; init; }

    public bool IsFolder => Url is null;

    /// <summary>フォルダーの中身（入れ子ぶんも含めて平らに落としたもの）。
    /// バーから落ちる一枚に入れ子の開閉まで持ち込まない——数クリック先へ届くのが帯の役目。</summary>
    public IReadOnlyList<BrowserLinkViewModel> Children { get; init; } = Array.Empty<BrowserLinkViewModel>();

    /// <summary>フォルダーの中身を出しているか（帯から落ちる一枚）。</summary>
    [ObservableProperty] private bool _isOpen;

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
            Owner.CloseOtherBookmarkBarPopups(this);
    }

    public string ToolTipText => IsFolder ? $"{Title}（{Children.Count} 件）" : Url!;
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
    private readonly LoomoSettings? _settings;
    private readonly SettingsStore? _settingsStore;
    private BrowserLibrarySnapshot? _library;

    public BrowserViewModel() : this(new BrowserLibraryStore()) { }

    /// <param name="settings">表示状態（ブックマークバーの表示ON/OFF）の保存先。テストでは省く。</param>
    public BrowserViewModel(BrowserLibraryStore store,
        LoomoSettings? settings = null, SettingsStore? settingsStore = null)
    {
        _store = store;
        _settings = settings;
        _settingsStore = settingsStore;
        // 保存された表示状態を初期反映する（field 直接代入なので永続化・再構築は走らない）。
        _isBookmarkBarVisible = settings?.BrowserBookmarkBarVisible ?? true;
    }

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
    public bool IsBookmarkable => BrowserLibrary.IsRecordable(CurrentUrl);
    public string BookmarkTip => !IsBookmarkable
        ? "このページはブックマークに追加できません"
        : IsBookmarked ? "ブックマークを外す（Ctrl+D）" : "ブックマークに追加（Ctrl+D）";
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
    [ObservableProperty] private bool _isHistoryOpen;
    [ObservableProperty] private bool _isSuggestionsOpen;
    [ObservableProperty] private bool _isBookmarkSelectionMode;

    public string SelectionModeButtonText => IsBookmarkSelectionMode ? "選択を終了" : "選択";
    public string SelectedBookmarkCountText => $"選択中 {SelectedBookmarkCount} 件";
    public string DeleteSelectedBookmarksText => $"選択した {SelectedBookmarkCount} 件を削除";

    partial void OnIsBookmarkSelectionModeChanged(bool value)
    {
        if (!value)
            ClearBookmarkSelection();
        OnPropertyChanged(nameof(SelectionModeButtonText));
        OnPropertyChanged(nameof(HasSelectedBookmarks));
        OnPropertyChanged(nameof(SelectedBookmarkCountText));
        OnPropertyChanged(nameof(DeleteSelectedBookmarksText));
    }

    /// <summary>いま開いているフォルダー（<see cref="BrowserBookmarkTree.Key"/> の鍵）。
    /// <b>既定は全部畳んだ状態</b>——取り込んだ直後の数百件が一覧を埋め尽くさないように
    /// （エクスプローラのツリーと同じ流儀）。覚えるのはアプリを動かしている間だけで、
    /// browser.json には書かない：どこを開いていたかは資産ではなく、いまの見え方にすぎない。</summary>
    private readonly HashSet<string> _expandedFolders = new(StringComparer.Ordinal);

    /// <summary>現在の一覧にフォルダーが含まれるか。取り込み後の「全展開」を必要なときだけ出す。</summary>
    public bool HasBookmarkFolders => _library?.Bookmarks.Any(b =>
        BrowserBookmarkTree.NormalizePath(b.Folder).Count > 0) == true;

    // ── ブックマークバー（アドレス欄の下の帯・§21.5.1） ─────────────────
    /// <summary>バーに並ぶ項目（根の直下のフォルダー→ブックマークの順）。</summary>
    public ObservableCollection<BrowserBookmarkBarItemViewModel> BookmarkBarItems { get; } = new();

    public bool HasBookmarkBarItems => BookmarkBarItems.Count > 0;

    /// <summary>ブックマークバーを出すか（設定へ永続化する＝次回起動でも同じ見え方）。
    /// 帯は資産の量に関係なく1行ぶんの高さを取るので、要らない人が畳めることまで含めて機能になる
    /// ——切り替えの入口はバーの右クリック・ブックマーク一覧の「バーを表示／隠す」・Ctrl+Shift+B・
    /// ページの右クリックメニュー（Loomo）。</summary>
    [ObservableProperty] private bool _isBookmarkBarVisible = true;

    partial void OnIsBookmarkBarVisibleChanged(bool value)
    {
        if (value)
            _ = Library;      // 出すと決めた時点で読む（起動時には読まない流儀のまま）
        RefreshBookmarkBar();
        OnPropertyChanged(nameof(BookmarkBarToggleText));
        OnPropertyChanged(nameof(BookmarkBarMenuText));
        if (_settings is null)
            return;
        _settings.BrowserBookmarkBarVisible = value;
        try { _settingsStore?.Save(_settings); }
        catch { /* 永続化に失敗しても表示切替自体は効かせる */ }
    }

    /// <summary>ブックマーク一覧の中に置く短い方の見出し（周りが「選択」「畳む」等と並ぶ場所）。</summary>
    public string BookmarkBarToggleText => IsBookmarkBarVisible ? "バーを隠す" : "バーを表示";

    /// <summary>右クリックメニューに出す方の見出し（何のバーかが文だけで分かる長さ）。</summary>
    public string BookmarkBarMenuText => IsBookmarkBarVisible ? "ブックマークバーを隠す" : "ブックマークバーを表示";

    [RelayCommand]
    public void ToggleBookmarkBar() => IsBookmarkBarVisible = !IsBookmarkBarVisible;

    /// <summary>ブラウザペインを実際に使い始めたときに呼ぶ。バーを出しているならここで
    /// browser.json を読む——「起動時には読まない」流儀を保ったまま、帯の中身を埋めるための一点。</summary>
    public void PrepareBookmarkBar()
    {
        if (IsBookmarkBarVisible)
            _ = Library;
    }

    /// <summary>バーのブックマークを開く（フォルダーの項目は帯から落ちる一枚を開くだけで、ここへは来ない）。</summary>
    [RelayCommand]
    private void OpenBookmarkBarItem(BrowserBookmarkBarItemViewModel? item)
    {
        if (item?.Url is not { Length: > 0 } url)
            return;
        CloseBookmarkBarPopups();
        OpenUrlRequested?.Invoke(this, (url, false));
    }

    /// <summary>帯から落ちる一枚は同時に1つだけにする（隣を開いたら前のは畳む）。</summary>
    internal void CloseOtherBookmarkBarPopups(BrowserBookmarkBarItemViewModel opened)
    {
        foreach (var item in BookmarkBarItems)
        {
            if (!ReferenceEquals(item, opened))
                item.IsOpen = false;
        }
    }

    private void CloseBookmarkBarPopups()
    {
        foreach (var item in BookmarkBarItems)
            item.IsOpen = false;
    }

    /// <summary>バーの項目を組み直す。<b>根の直下だけ</b>を並べ、フォルダーは中身（入れ子ぶん含む）を
    /// 平らに持たせる。読み込み前（<c>_library</c> が null）は空のまま——読んだ時点で
    /// <see cref="RefreshLists"/> からここへ戻ってくる。</summary>
    private void RefreshBookmarkBar()
    {
        BookmarkBarItems.Clear();
        if (IsBookmarkBarVisible && _library is not null)
        {
            var root = BrowserBookmarkTree.Build(_library.Bookmarks);
            foreach (var folder in root.Folders)
            {
                BookmarkBarItems.Add(new BrowserBookmarkBarItemViewModel
                {
                    Owner = this,
                    Title = folder.Name,
                    Children = BrowserBookmarkTree.Descendants(folder)
                        .Select(b => new BrowserLinkViewModel
                        {
                            Owner = this,
                            Url = b.Url,
                            Title = b.Title,
                            IsBookmark = true,
                        })
                        .ToList(),
                });
            }
            foreach (var bookmark in root.Bookmarks)
            {
                BookmarkBarItems.Add(new BrowserBookmarkBarItemViewModel
                {
                    Owner = this,
                    Title = string.IsNullOrWhiteSpace(bookmark.Title) ? bookmark.Url : bookmark.Title!,
                    Url = bookmark.Url,
                });
            }
        }
        OnPropertyChanged(nameof(HasBookmarkBarItems));
    }

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

    /// <summary>取り込んだ直後に中身を確認しやすいよう、フォルダーを一度に開く。</summary>
    [RelayCommand]
    private void ExpandAllBookmarkFolders()
    {
        _ = Library;
        foreach (var folder in BrowserBookmarkTree.Folders(BrowserBookmarkTree.Build(Library.Bookmarks)))
            _expandedFolders.Add(BrowserBookmarkTree.Key(folder.Path));
        RefreshLists();
    }

    /// <summary>一覧をコンパクトに戻す。</summary>
    [RelayCommand]
    private void CollapseAllBookmarkFolders()
    {
        _expandedFolders.Clear();
        RefreshLists();
    }

    /// <summary>ブックマークの複数選択モードを開閉する。</summary>
    [RelayCommand]
    private void ToggleBookmarkSelectionMode()
        => IsBookmarkSelectionMode = !IsBookmarkSelectionMode;

    [RelayCommand]
    private void SelectAllBookmarks()
    {
        _ = Library;
        IsBookmarkSelectionMode = true;
        // 折りたたまれたフォルダーの中も「全選択」に含める。
        foreach (var folder in BrowserBookmarkTree.Folders(BrowserBookmarkTree.Build(Library.Bookmarks)))
            _expandedFolders.Add(BrowserBookmarkTree.Key(folder.Path));
        RefreshLists();
        foreach (var item in Bookmarks.OfType<BrowserLinkViewModel>())
            item.IsSelected = true;
        NotifyBookmarkSelectionChanged();
    }

    [RelayCommand]
    private void ClearBookmarkSelection()
    {
        foreach (var item in Bookmarks.OfType<BrowserLinkViewModel>())
            item.IsSelected = false;
        NotifyBookmarkSelectionChanged();
    }

    [RelayCommand]
    private void RemoveSelectedBookmarks()
    {
        var selected = Bookmarks.OfType<BrowserLinkViewModel>()
            .Where(item => item.IsSelected)
            .Select(item => item.Url)
            .ToList();
        if (selected.Count == 0)
            return;

        var keys = selected.Select(BrowserLibrary.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Library.Bookmarks.RemoveAll(bookmark =>
            keys.Contains(BrowserLibrary.Normalize(bookmark.Url)));
        IsBookmarkSelectionMode = false;
        RefreshLists();
        Persist();
        ToastService.Info($"ブックマーク {selected.Count} 件を削除しました。");
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
        OnPropertyChanged(nameof(IsBookmarkable));
        OnPropertyChanged(nameof(BookmarkTip));
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
        ToastService.Success(existing is null ? "ブックマークに追加しました。" : "ブックマークを外しました。");
    }

    [RelayCommand]
    private void OpenLink(BrowserLinkViewModel? item)
    {
        if (item is null)
            return;
        IsLibraryOpen = false;
        IsHistoryOpen = false;
        IsSuggestionsOpen = false;
        CloseBookmarkBarPopups();
        OpenUrlRequested?.Invoke(this, (item.Url, false));
    }

    [RelayCommand]
    private void OpenLinkInNewTab(BrowserLinkViewModel? item)
    {
        if (item is null)
            return;
        IsLibraryOpen = false;
        IsHistoryOpen = false;
        CloseBookmarkBarPopups();
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
        ToastService.Info($"{(item.IsBookmark ? "ブックマーク" : "履歴")}を削除しました。");
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
        {
            if (row.Folder is { } folder)
            {
                Bookmarks.Add(new BrowserBookmarkFolderViewModel
                {
                    Depth = row.Depth,
                    Name = folder.Name,
                    Key = BrowserBookmarkTree.Key(folder.Path),
                    Count = folder.TotalCount,
                    IsExpanded = _expandedFolders.Contains(BrowserBookmarkTree.Key(folder.Path)),
                });
            }
            else
            {
                var bookmark = new BrowserLinkViewModel
                {
                    Depth = row.Depth,
                    Url = row.Bookmark!.Url,
                    Title = row.Bookmark.Title,
                    IsBookmark = true,
                    IsBookmarkRow = true,
                };
                bookmark.SelectionChanged += OnBookmarkSelectionChanged;
                Bookmarks.Add(bookmark);
            }
        }
        History.Clear();
        foreach (var h in library.History.Take(MaxHistoryShown))
            History.Add(new BrowserLinkViewModel { Url = h.Url, Title = h.Title, IsBookmark = false });
        RefreshBookmarkBar();
        OnPropertyChanged(nameof(HasBookmarks));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasBookmarkFolders));
        OnPropertyChanged(nameof(BookmarkCount));
        OnPropertyChanged(nameof(HistoryCount));
        OnPropertyChanged(nameof(BookmarkCountText));
        OnPropertyChanged(nameof(HistoryCountText));
    }

    public bool HasBookmarks => Bookmarks.Count > 0;
    public bool HasHistory => History.Count > 0;
    public int BookmarkCount => _library?.Bookmarks.Count ?? 0;
    public int HistoryCount => _library?.History.Count ?? 0;
    public string BookmarkCountText => $"ブックマーク（{BookmarkCount}）";
    public string HistoryCountText => $"履歴（{HistoryCount}）";

    public int SelectedBookmarkCount
        => Bookmarks.OfType<BrowserLinkViewModel>().Count(item => item.IsSelected);

    public bool HasSelectedBookmarks => SelectedBookmarkCount > 0;

    private void NotifyBookmarkSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedBookmarkCount));
        OnPropertyChanged(nameof(HasSelectedBookmarks));
        OnPropertyChanged(nameof(SelectedBookmarkCountText));
        OnPropertyChanged(nameof(DeleteSelectedBookmarksText));
    }

    private void OnBookmarkSelectionChanged(object? sender, EventArgs e)
        => NotifyBookmarkSelectionChanged();

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
