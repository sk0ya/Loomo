using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services.Settings;

namespace sk0ya.Loomo.App.ViewModels;

public enum TabEntryKind
{
    Terminal,
    Editor,
    Browser
}

public sealed partial class TabEntryViewModel : ObservableObject
{
    public TabEntryViewModel(TabEntryKind kind, string title, bool isActive = true, ImageSource? icon = null)
        : this(Guid.NewGuid(), kind, title, isActive, icon)
    {
    }

    public TabEntryViewModel(Guid id, TabEntryKind kind, string title, bool isActive = true, ImageSource? icon = null)
    {
        Id = id;
        Kind = kind;
        _title = title;
        _isActive = isActive;
        _icon = icon;
    }

    public Guid Id { get; }
    public TabEntryKind Kind { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>ファイル種別アイコン（<see cref="FileIcons"/>）の索引。テーマの明暗が変わると同じ絵を
    /// 別配色で引き直す必要があるため、描画済みの <see cref="Icon"/> とは別に覚えておく。
    /// favicon を出すブラウザタブのように種別アイコンを使わないものは -1。</summary>
    public int IconIndex { get; private set; } = -1;

    /// <summary>種別アイコンを索引で差し替える（索引と描画済みの絵を必ず同時に更新する）。</summary>
    public void SetFileIcon(int index)
    {
        IconIndex = index;
        Icon = FileIcons.ImageFor(index);
    }

    /// <summary>種別アイコンではない絵（ブラウザの favicon）を直接与える。以後この行はテーマ切替で
    /// 引き直さない。</summary>
    public void SetCustomIcon(ImageSource? icon)
    {
        IconIndex = -1;
        Icon = icon;
    }

    /// <summary>テーマの明暗が変わってアイコンの配色が入れ替わったとき、引き直させる。</summary>
    public void RefreshIcon()
    {
        if (IconIndex >= 0)
            Icon = FileIcons.ImageFor(IconIndex);
    }

    /// <summary>プレビュータブ（FolderTree の単クリックで開き、編集するまで確定しない）。タイトルを斜体で表示する。</summary>
    [ObservableProperty] private bool _isPreview;

    /// <summary>実ファイルの絶対パス（Editor タブのみ。Untitled／仮想ドキュメントは null）。
    /// 「パスをコピー」「エクスプローラーで表示」の表示可否・対象に使う。</summary>
    [ObservableProperty] private string? _filePath;
}

/// <summary>見出しの設定ビューに出す「種別」1行（エディタ／ブラウザ／ターミナル）。一覧の行と同じ
/// 「アイコン＋名前＋件数」を持ち、押すとその種別の出し入れが切り替わる。出す／隠すの正本はここ
/// ——<see cref="TabsViewModel.ShowEditorTabs"/> 等はこの行への窓口で、二重の真実を作らない。</summary>
public sealed partial class TabKindRowViewModel : ObservableObject
{
    public TabKindRowViewModel(TabEntryKind kind, string label, string automationId)
    {
        Kind = kind;
        Label = label;
        AutomationId = automationId;
    }

    public TabEntryKind Kind { get; }

    /// <summary>行に出す名前（「エディタ」）。</summary>
    public string Label { get; }

    /// <summary>実機検証が掴む識別子。</summary>
    public string AutomationId { get; }

    /// <summary>一覧の行と同じ 16x16 の線画（テーマの明暗が変わると引き直す）。</summary>
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>その種別がいま持っているタブ数。隠していても数は出す（「無くなった」に見せない）。</summary>
    [ObservableProperty] private int _count;

    /// <summary>この種別を一覧に並べるか。</summary>
    [ObservableProperty] private bool _isShown = true;
}

/// <summary>Terminal / Editor / Browser のタブ相当情報をサイドバーへ表示する。</summary>
public sealed partial class TabsViewModel : ObservableObject
{
    /// <summary>ターミナルのタブに出す絵は PowerShell（.ps1）のアイコンを流用する。</summary>
    private static readonly int TerminalIconIndex = FileIcons.IndexFor("terminal.ps1", isDirectory: false);

    private readonly TabIconService _icons;
    private readonly LoomoSettings? _settings;
    private readonly SettingsStore? _settingsStore;
    private readonly TabKindRowViewModel _editorKind = new(TabEntryKind.Editor, "エディタ", "TabsShowEditorToggle");
    private readonly TabKindRowViewModel _browserKind = new(TabEntryKind.Browser, "ブラウザ", "TabsShowBrowserToggle");
    private readonly TabKindRowViewModel _terminalKind = new(TabEntryKind.Terminal, "ターミナル", "TabsShowTerminalToggle");

    public ObservableCollection<TabEntryViewModel> TerminalTabs { get; } = new();
    public ObservableCollection<TabEntryViewModel> EditorTabs { get; } = new();
    public ObservableCollection<TabEntryViewModel> BrowserTabs { get; } = new();

    public event EventHandler<TabEntryViewModel>? TabActivated;
    public event EventHandler<TabEntryViewModel>? TabCloseRequested;
    /// <summary>「他のタブを閉じる」：同じ Kind（Terminal/Editor/Browser）内の他タブを閉じる。</summary>
    public event EventHandler<TabEntryViewModel>? TabCloseOthersRequested;
    /// <summary>「すべて閉じる」：同じ Kind 内の全タブを閉じる。</summary>
    public event EventHandler<TabEntryViewModel>? TabCloseAllRequested;
    /// <summary>「別ウィンドウで開く」：このタブをフローティングウィンドウへ切り離す（複製／スピンオフ）。</summary>
    public event EventHandler<TabEntryViewModel>? TabDetachRequested;

    /// <summary>見出しの件数表示。<b>いま一覧に出ている</b>タブだけを数える——種別を隠しているのに
    /// 隠した分まで数えると、並んでいる行数と食い違って見出しが嘘をつく。</summary>
    public int TotalCount
        => (ShowEditorTabs ? EditorTabs.Count : 0)
         + (ShowBrowserTabs ? BrowserTabs.Count : 0)
         + (ShowTerminalTabs ? TerminalTabs.Count : 0);

    /// <summary>見出しクリックで開く設定ビュー（表示する種別を選ぶ）が開いているか。
    /// 面そのものの出し入れは ActivityBar が担うので、見出しクリックはこちらに使える。</summary>
    [ObservableProperty] private bool _isSettingsOpen;

    /// <summary>設定ビューに出す種別の行（並びは一覧と同じ エディタ → ブラウザ → ターミナル）。</summary>
    public IReadOnlyList<TabKindRowViewModel> Kinds { get; }

    /// <summary>エディタのタブを一覧に出すか。隠しても閉じるわけではなく、この一覧に出さないだけ。</summary>
    public bool ShowEditorTabs
    {
        get => _editorKind.IsShown;
        set => _editorKind.IsShown = value;
    }

    /// <summary>ブラウザのタブを一覧に出すか。</summary>
    public bool ShowBrowserTabs
    {
        get => _browserKind.IsShown;
        set => _browserKind.IsShown = value;
    }

    /// <summary>ターミナルのタブを一覧に出すか。</summary>
    public bool ShowTerminalTabs
    {
        get => _terminalKind.IsShown;
        set => _terminalKind.IsShown = value;
    }

    /// <summary>いずれかの種別を隠している＝一覧に絞りが効いている。見出しの ⚙ に印を残すのに使う
    /// ——開かなくても「全部は出ていない」と分かる必要がある。</summary>
    public bool IsFiltered => !(ShowEditorTabs && ShowBrowserTabs && ShowTerminalTabs);

    /// <summary>その種別の行をいま並べるか（出す設定＋実際に1つ以上ある）。</summary>
    public bool IsEditorSectionVisible => ShowEditorTabs && EditorTabs.Count > 0;
    public bool IsBrowserSectionVisible => ShowBrowserTabs && BrowserTabs.Count > 0;
    public bool IsTerminalSectionVisible => ShowTerminalTabs && TerminalTabs.Count > 0;

    /// <summary>3種とも隠していて、一覧が空になっている。タブが無いのか自分で隠したのかを
    /// 見分けられないと「壊れた」に見えるので、そのときだけ案内を出す。</summary>
    public bool IsAllKindsHidden => !ShowEditorTabs && !ShowBrowserTabs && !ShowTerminalTabs;

    public TabsViewModel()
        : this(new TabIconService())
    {
    }

    public TabsViewModel(TabIconService icons, LoomoSettings? settings = null, SettingsStore? settingsStore = null)
    {
        _icons = icons;
        _settings = settings;
        _settingsStore = settingsStore;
        Kinds = [_editorKind, _browserKind, _terminalKind];
        // 保存済みの選択は購読前に入れる（起動しただけで書き戻さない）。
        _editorKind.IsShown = settings?.TabsPanel.ShowEditor ?? true;
        _browserKind.IsShown = settings?.TabsPanel.ShowBrowser ?? true;
        _terminalKind.IsShown = settings?.TabsPanel.ShowTerminal ?? true;
        foreach (var kind in Kinds)
            kind.PropertyChanged += OnKindRowChanged;
        RefreshKindIcons();
        TerminalTabs.CollectionChanged += OnTabCollectionChanged;
        EditorTabs.CollectionChanged += OnTabCollectionChanged;
        BrowserTabs.CollectionChanged += OnTabCollectionChanged;
        // アプリと同じ寿命なので解除は要らない（フォルダーツリーと同じ扱い）。
        FileIcons.PaletteChanged += (_, _) => RefreshIcons();
    }

    private void OnTabCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => NotifyCounts();

    private void OnKindRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabKindRowViewModel.IsShown) || sender is not TabKindRowViewModel row)
            return;

        OnPropertyChanged(row.Kind switch
        {
            TabEntryKind.Editor => nameof(ShowEditorTabs),
            TabEntryKind.Browser => nameof(ShowBrowserTabs),
            _ => nameof(ShowTerminalTabs),
        });
        NotifyCounts();
        Persist();
    }

    private void NotifyCounts()
    {
        _editorKind.Count = EditorTabs.Count;
        _browserKind.Count = BrowserTabs.Count;
        _terminalKind.Count = TerminalTabs.Count;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(IsEditorSectionVisible));
        OnPropertyChanged(nameof(IsBrowserSectionVisible));
        OnPropertyChanged(nameof(IsTerminalSectionVisible));
        OnPropertyChanged(nameof(IsAllKindsHidden));
        OnPropertyChanged(nameof(IsFiltered));
    }

    private void Persist()
    {
        if (_settings is null) return;
        _settings.TabsPanel.ShowEditor = ShowEditorTabs;
        _settings.TabsPanel.ShowBrowser = ShowBrowserTabs;
        _settings.TabsPanel.ShowTerminal = ShowTerminalTabs;
        try { _settingsStore?.Save(_settings); }
        catch { /* 永続化に失敗しても選択自体は効かせる */ }
    }

    /// <summary>テーマの明暗が入れ替わったので、種別アイコンを新しい配色で引き直す
    /// （favicon を出しているブラウザタブは対象外）。</summary>
    private void RefreshIcons()
    {
        foreach (var tab in EditorTabs) tab.RefreshIcon();
        foreach (var tab in BrowserTabs) tab.RefreshIcon();
        foreach (var tab in TerminalTabs) tab.RefreshIcon();
        RefreshKindIcons();
    }

    /// <summary>設定ビューの種別行に出す絵。一覧の行とまったく同じ引き方をするので、同じ種別は
    /// どこに出ても同じ絵になる（エディタは既定のファイル、ターミナルは .ps1、ブラウザは既定の favicon）。</summary>
    private void RefreshKindIcons()
    {
        _editorKind.Icon = FileIcons.ImageFor(FileIconData.DefaultFileIndex);
        _terminalKind.Icon = FileIcons.ImageFor(TerminalIconIndex);
        _browserKind.Icon = _icons.GetBrowserDefaultIcon();
    }

    public void AddTerminalTab(Guid id, string? title, bool isActive)
    {
        var tab = new TabEntryViewModel(id, TabEntryKind.Terminal, TerminalTitle(title), isActive);
        tab.SetFileIcon(TerminalIconIndex);
        TerminalTabs.Add(tab);
    }

    public void UpdateTerminalTab(Guid id, string? title)
    {
        var tab = TerminalTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;

        tab.Title = TerminalTitle(title);
        tab.SetFileIcon(TerminalIconIndex);
    }

    public void ActivateTerminalTab(Guid id)
    {
        foreach (var tab in TerminalTabs)
            tab.IsActive = tab.Id == id;
    }

    public void RemoveTerminalTab(Guid id)
    {
        var tab = TerminalTabs.FirstOrDefault(t => t.Id == id);
        if (tab is not null)
            TerminalTabs.Remove(tab);
    }

    public void AddEditorTab(Guid id, string? path, bool isModified, bool isActive)
    {
        var title = string.IsNullOrWhiteSpace(path)
            ? "Untitled"
            : Path.GetFileName(path);
        if (isModified)
            title += " *";

        var tab = new TabEntryViewModel(id, TabEntryKind.Editor, title, isActive)
        {
            FilePath = RealFilePath(path),
        };
        tab.SetFileIcon(FileIconIndexFor(path));

        var previewIndex = IndexOfPreviewEditorTab();
        if (previewIndex >= 0)
            EditorTabs.Insert(previewIndex, tab);
        else
            EditorTabs.Add(tab);
    }

    public void UpdateEditorTab(Guid id, string? path, bool isModified)
    {
        var tab = EditorTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;

        var title = string.IsNullOrWhiteSpace(path)
            ? "Untitled"
            : Path.GetFileName(path);
        if (isModified)
            title += " *";

        tab.Title = title;
        tab.SetFileIcon(FileIconIndexFor(path));
        tab.FilePath = RealFilePath(path);
    }

    /// <summary>エディタタブの種別アイコン。フォルダーツリー・検索結果とまったく同じ引き方をするので、
    /// 同じファイルはどこに出ても同じ絵になる。</summary>
    private static int FileIconIndexFor(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? FileIconData.DefaultFileIndex
            : FileIcons.IndexFor(path, isDirectory: false);

    /// <summary>AddEditorTab/UpdateEditorTab の path 引数は Untitled=null・仮想ドキュメント=タイトル文字列・
    /// 実ファイル=絶対パスを兼用しているため、絶対パスの場合だけ実ファイルとして扱う。</summary>
    private static string? RealFilePath(string? path)
        => path is { Length: > 0 } p && Path.IsPathRooted(p) ? p : null;

    /// <summary>エディタタブのプレビュー表示（斜体）を切り替える。</summary>
    public void SetEditorTabPreview(Guid id, bool isPreview)
    {
        var tab = EditorTabs.FirstOrDefault(t => t.Id == id);
        if (tab is not null)
        {
            tab.IsPreview = isPreview;
            if (isPreview)
                MoveEditorTabToEnd(tab);
        }
    }

    private void MoveEditorTabToEnd(TabEntryViewModel tab)
    {
        var index = EditorTabs.IndexOf(tab);
        var last = EditorTabs.Count - 1;
        if (index >= 0 && index < last)
            EditorTabs.Move(index, last);
    }

    private int IndexOfPreviewEditorTab()
    {
        for (var i = 0; i < EditorTabs.Count; i++)
        {
            if (EditorTabs[i].IsPreview)
                return i;
        }

        return -1;
    }

    public void ActivateEditorTab(Guid id)
    {
        foreach (var tab in EditorTabs)
            tab.IsActive = tab.Id == id;
    }

    public void RemoveEditorTab(Guid id)
    {
        var tab = EditorTabs.FirstOrDefault(t => t.Id == id);
        if (tab is not null)
            EditorTabs.Remove(tab);
    }

    public void AddBrowserTab(Guid id, string? title, bool isActive)
    {
        BrowserTabs.Add(new TabEntryViewModel(
            id,
            TabEntryKind.Browser,
            BrowserTitle(title),
            isActive,
            _icons.GetBrowserDefaultIcon()));
    }

    public void UpdateBrowserTab(Guid id, string? title)
    {
        var tab = BrowserTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;

        tab.Title = BrowserTitle(title);
    }

    public void ActivateBrowserTab(Guid id)
    {
        foreach (var tab in BrowserTabs)
            tab.IsActive = tab.Id == id;
    }

    public void RemoveBrowserTab(Guid id)
    {
        var tab = BrowserTabs.FirstOrDefault(t => t.Id == id);
        if (tab is not null)
            BrowserTabs.Remove(tab);
    }

    public void UpdateTabIcon(Guid id, ImageSource? icon)
    {
        var tab = TerminalTabs.FirstOrDefault(t => t.Id == id)
               ?? EditorTabs.FirstOrDefault(t => t.Id == id)
               ?? BrowserTabs.FirstOrDefault(t => t.Id == id);

        if (tab is not null)
            tab.SetCustomIcon(icon);
    }

    private static string BrowserTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? "Browser" : title.Trim();

    private static string TerminalTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? "Terminal" : title.Trim();

    [RelayCommand]
    private void ActivateTab(TabEntryViewModel? tab)
    {
        if (tab is not null)
            TabActivated?.Invoke(this, tab);
    }

    [RelayCommand]
    private void CloseTab(TabEntryViewModel? tab)
    {
        if (tab is not null)
            TabCloseRequested?.Invoke(this, tab);
    }

    [RelayCommand]
    private void CloseOtherTabs(TabEntryViewModel? tab)
    {
        if (tab is not null)
            TabCloseOthersRequested?.Invoke(this, tab);
    }

    [RelayCommand]
    private void CloseAllTabs(TabEntryViewModel? tab)
    {
        if (tab is not null)
            TabCloseAllRequested?.Invoke(this, tab);
    }

    [RelayCommand]
    private void DetachTab(TabEntryViewModel? tab)
    {
        if (tab is not null)
            TabDetachRequested?.Invoke(this, tab);
    }

    [RelayCommand]
    private void CopyPath(TabEntryViewModel? tab)
    {
        if (tab?.FilePath is { Length: > 0 } path)
            Clipboard.SetText(path);
    }

    [RelayCommand]
    private void RevealInExplorer(TabEntryViewModel? tab)
    {
        if (tab?.FilePath is { Length: > 0 } path && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
