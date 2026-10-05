using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ペグボードの1アイテム（カード）。</summary>
public sealed partial class PegboardItemVm : ObservableObject
{
    public required PegboardItemSnapshot Snapshot { get; init; }

    [ObservableProperty] private bool _pinned;

    public string Content => Snapshot.Content;
    public string Type => Snapshot.Type;

    private bool? _isDirectory;

    /// <summary>file 種別のうちフォルダーを指すもの（アイコンと見出しの出し分け用。判定は一度だけ）。</summary>
    public bool IsDirectory
    {
        get
        {
            if (_isDirectory is { } known) return known;
            bool result;
            try { result = Type == "file" && Directory.Exists(Content); }
            catch { result = false; }
            _isDirectory = result;
            return result;
        }
    }

    /// <summary>アイコンの種別（url / file / folder / text）。</summary>
    public string Kind => Type switch
    {
        "url" => "url",
        "file" => IsDirectory ? "folder" : "file",
        _ => "text",
    };

    /// <summary>カードの見出し。Title 優先、無ければ種別ごとに一番見分けのつく部分
    /// （url＝スキームを除いたアドレス、file＝ファイル名、text＝先頭行）。</summary>
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Snapshot.Title)) return Snapshot.Title!;
            return Type switch
            {
                "url" => StripScheme(Content),
                "file" => FileNameOf(Content),
                _ => FirstLine(Content),
            };
        }
    }

    /// <summary>出典のページを持つ（ブラウザの選択から残した text・§24.24）。</summary>
    public bool HasSource => Type == "text" && !string.IsNullOrWhiteSpace(Snapshot.SourceUrl);

    /// <summary>出典の呼び名（題名、無ければスキームを除いたアドレス）。出典が無ければ空。</summary>
    public string SourceLabel
        => !HasSource ? ""
         : !string.IsNullOrWhiteSpace(Snapshot.SourceTitle) ? Snapshot.SourceTitle!
         : StripScheme(Snapshot.SourceUrl!);

    /// <summary>出典へ戻る URL。引用した一節へスクロールして強調する Text Fragment 付き。</summary>
    public string? SourceLink => HasSource ? BrowserTextFragment.Build(Snapshot.SourceUrl!, Content) : null;

    /// <summary>見出しの下に薄く出す所在（url＝アドレス、file＝親フォルダー、出典付き text＝出典）。見出しと同じなら空。</summary>
    public string Subtitle
    {
        get
        {
            var subtitle = Type switch
            {
                "url" => StripScheme(Content),
                "file" => ParentOf(Content),
                _ when HasSource => $"出典: {SourceLabel}",
                _ => "",
            };
            return subtitle == DisplayTitle ? "" : subtitle;
        }
    }

    /// <summary>text の本文プレビュー。先頭行は見出しに出ているので2行目以降だけ
    /// （Title を持つ text は全文）。単一行・url・file は空にして高さを節約する。</summary>
    public string Preview
    {
        get
        {
            if (Type is "url" or "file") return "";
            if (!string.IsNullOrWhiteSpace(Snapshot.Title)) return Content == Snapshot.Title ? "" : Content;
            var text = Content.TrimStart();
            var newline = text.IndexOf('\n');
            return newline < 0 ? "" : Dedent(text[(newline + 1)..].Trim('\r', '\n'));
        }
    }

    /// <summary>作成時刻の相対表記（「3分前」「昨日」…）。<see cref="RefreshTime"/> で更新する。</summary>
    public string CreatedLabel => FormatRelative(Snapshot.CreatedUtc.ToLocalTime(), DateTime.Now);

    public string CreatedToolTip => Snapshot.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string OpenLabel => Type switch
    {
        "url" => "ブラウザで開く",
        "file" when IsDirectory => "ターミナルで開く",
        _ => "エディタで開く",
    };

    /// <summary>グループ見出し（固定 / 最近）。</summary>
    public string SectionLabel => Pinned ? "固定" : "最近";

    partial void OnPinnedChanged(bool value) => OnPropertyChanged(nameof(SectionLabel));

    public void RefreshTime() => OnPropertyChanged(nameof(CreatedLabel));

    /// <summary>相対時刻。1時間以内は分、当日は時間、前日は「昨日」、1週間以内は日数、それより前は日付。</summary>
    internal static string FormatRelative(DateTime local, DateTime now)
    {
        var elapsed = now - local;
        if (elapsed < TimeSpan.FromMinutes(1)) return "たった今";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}分前";
        var days = (now.Date - local.Date).Days;
        if (days == 0) return $"{(int)elapsed.TotalHours}時間前";
        if (days == 1) return "昨日";
        if (days < 7) return $"{days}日前";
        return local.Year == now.Year ? local.ToString("M/d") : local.ToString("yyyy/M/d");
    }

    private static string FirstLine(string content)
    {
        var text = content.AsSpan().TrimStart();
        var newline = text.IndexOfAny('\r', '\n');
        return (newline >= 0 ? text[..newline] : text).ToString();
    }

    /// <summary>行頭の共通インデントを外す（コード片の2行目以降が右へ寄って読めなくなるのを防ぐ）。</summary>
    internal static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0)
                          .Select(l => l.Length - l.TrimStart(' ', '\t').Length)
                          .DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }

    private static string StripScheme(string url)
    {
        var index = url.IndexOf("://", StringComparison.Ordinal);
        var rest = index >= 0 ? url[(index + 3)..] : url;
        return rest.TrimEnd('/');
    }

    private static string FileNameOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    private static string ParentOf(string path)
    {
        try { return Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? ""; }
        catch { return ""; }
    }

    /// <summary>絞り込み語（空白区切り AND・大小無視）が見出し・本文・出典のすべてに当たるか。</summary>
    internal bool Matches(string[] terms)
        => terms.All(t => DisplayTitle.Contains(t, StringComparison.OrdinalIgnoreCase)
                       || Content.Contains(t, StringComparison.OrdinalIgnoreCase)
                       || (HasSource && (SourceLabel.Contains(t, StringComparison.OrdinalIgnoreCase)
                                         || Snapshot.SourceUrl!.Contains(t, StringComparison.OrdinalIgnoreCase))));
}

/// <summary>
/// ペグボードペインの ViewModel（設計書 §23.3）。ターミナル出力片・スニペット・URL・
/// ファイル参照を貼っておく作業台。アイテムはワークスペーススナップショットに保存される
/// （<see cref="LoadItems"/> / <see cref="ToSnapshots"/> を ShellWindow が切替/保存時に呼ぶ）。
/// </summary>
public sealed partial class PegboardViewModel : ObservableObject
{
    public ObservableCollection<PegboardItemVm> Items { get; } = new();

    /// <summary>アイテムの増減・ピン替えで発火（ShellWindow がスナップショット保存に使う）。</summary>
    public event EventHandler? Changed;

    /// <summary>「開く」要求。url→ブラウザ / file→エディタ等の振り分けは ShellWindow が担う。</summary>
    public event EventHandler<PegboardItemVm>? OpenRequested;

    /// <summary>「出典を開く」要求（出典付き text・§24.24）。ブラウザで開くのは ShellWindow が担う。</summary>
    public event EventHandler<PegboardItemVm>? OpenSourceRequested;

    /// <summary>「ブラウザのURLを残す」要求。表示中 URL の取得は ShellWindow が担う。</summary>
    public event EventHandler? BrowserPinRequested;

    /// <summary>「エディタの選択を残す」要求。選択テキストの取得は ShellWindow が担う。</summary>
    public event EventHandler? EditorSelectionPinRequested;

    /// <summary>「ターミナルへ送る」要求（素材の流れ）。送信先の解決は ShellWindow が担う。</summary>
    public event EventHandler<PegboardItemVm>? SendToTerminalRequested;

    /// <summary>「コンポーザへ送る」要求（素材の流れ）。追記は ShellWindow が担う。</summary>
    public event EventHandler<PegboardItemVm>? SendToComposerRequested;

    [ObservableProperty] private string _emptyMessage = "";

    /// <summary>絞り込み語（空白区切り AND）。見出しと本文に当てる。</summary>
    [ObservableProperty] private string _filter = "";

    [ObservableProperty] private bool _isFilterVisible;

    /// <summary>固定と最近の両方が見えているときだけ見出しを出す（1群だけなら見出しは雑音）。</summary>
    [ObservableProperty] private bool _showSections;

    /// <summary>ヘッダーの件数（絞り込み中は「一致数 / 全体」）。</summary>
    [ObservableProperty] private string _countLabel = "";

    /// <summary>表示用ビュー（絞り込み＋固定/最近のグループ）。並びは <see cref="Items"/> のまま。</summary>
    public ICollectionView ItemsView { get; }

    private string[] _terms = [];

    public PegboardViewModel()
    {
        var view = new ListCollectionView(Items) { Filter = o => o is PegboardItemVm i && i.Matches(_terms) };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PegboardItemVm.SectionLabel)));
        ItemsView = view;
        Items.CollectionChanged += (_, _) => UpdateEmptyMessage();
        UpdateEmptyMessage();
    }

    partial void OnFilterChanged(string value)
    {
        _terms = value.Split(' ', '　', '\t').Where(t => t.Length > 0).ToArray();
        ItemsView.Refresh();
        UpdateEmptyMessage();
    }

    /// <summary>絞り込みを解除して閉じる。</summary>
    public void CloseFilter()
    {
        Filter = "";
        IsFilterVisible = false;
    }

    /// <summary>表示中の相対時刻（「3分前」）を今の時刻で引き直す。</summary>
    public void RefreshTimes()
    {
        foreach (var item in Items)
            item.RefreshTime();
    }

    /// <summary>ワークスペース切替時にアイテムを入れ替える（保存は発火しない）。</summary>
    public void LoadItems(IEnumerable<PegboardItemSnapshot> snapshots)
    {
        Items.Clear();
        foreach (var s in Sort(snapshots.Select(ToVm)))
            Items.Add(s);
    }

    public List<PegboardItemSnapshot> ToSnapshots()
        => Items.Select(i => i.Snapshot).ToList();

    /// <summary>クリップボードのテキストを1アイテムとして追加する。</summary>
    [RelayCommand]
    private void AddFromClipboard()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch { return; /* クリップボード占有中などは無視 */ }
        AddContent(text);
    }

    /// <summary>内容から種別（text/url/file）を判定して追加する。明示指定があればそれを使う。
    /// <paramref name="sourceUrl"/> はブラウザの選択から残すときの出典（text のときだけ持たせる・§24.24）。</summary>
    public void AddContent(string content, string? type = null, string? title = null,
                           string? sourceUrl = null, string? sourceTitle = null)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0) return;

        var snapshot = new PegboardItemSnapshot
        {
            Type = type ?? DetectType(trimmed),
            Content = trimmed,
            Title = title,
        };
        if (snapshot.Type == "text" && !string.IsNullOrWhiteSpace(sourceUrl))
        {
            snapshot.SourceUrl = sourceUrl;
            snapshot.SourceTitle = string.IsNullOrWhiteSpace(sourceTitle) ? null : sourceTitle;
        }
        // 新規は「ピン留め群の直後」（未ピンの先頭）に置く。
        var vm = ToVm(snapshot);
        Items.Insert(Items.Count(i => i.Pinned), vm);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>単一行の URL / 実在パスを判定する（複数行は常に text）。テスト対象。</summary>
    internal static string DetectType(string content)
    {
        if (content.Contains('\n')) return "text";
        if (content.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "url";
        try
        {
            if (Path.IsPathRooted(content) && (File.Exists(content) || Directory.Exists(content)))
                return "file";
        }
        catch { /* 不正なパス文字は text 扱い */ }
        return "text";
    }

    /// <summary>ブラウザで表示中のページを残す（URL 取得は ShellWindow 側）。</summary>
    [RelayCommand]
    private void PinBrowserUrl() => BrowserPinRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>エディタの選択テキストを残す（選択の取得は ShellWindow 側）。</summary>
    [RelayCommand]
    private void PinEditorSelection() => EditorSelectionPinRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>アイテムを可視ターミナルのプロンプトへ送る（実行はしない）。</summary>
    [RelayCommand]
    private void SendToTerminal(PegboardItemVm item) => SendToTerminalRequested?.Invoke(this, item);

    /// <summary>アイテムをコンポーザ本文の末尾へ送る。</summary>
    [RelayCommand]
    private void SendToComposer(PegboardItemVm item) => SendToComposerRequested?.Invoke(this, item);

    [RelayCommand]
    private void Copy(PegboardItemVm item)
    {
        try { Clipboard.SetText(item.Content); } catch { /* 無視 */ }
    }

    [RelayCommand]
    private void Open(PegboardItemVm item) => OpenRequested?.Invoke(this, item);

    /// <summary>出典のページを、引用した一節を強調した状態でブラウザに開く。</summary>
    [RelayCommand]
    private void OpenSource(PegboardItemVm item)
    {
        if (item.HasSource)
            OpenSourceRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private void Delete(PegboardItemVm item)
    {
        Items.Remove(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void TogglePin(PegboardItemVm item)
    {
        item.Pinned = !item.Pinned;
        item.Snapshot.Pinned = item.Pinned;

        // ピン留め群（上）⇔ 通常群（下）へ移し替える。群内は作成の新しい順を保つ。
        var ordered = Sort(Items.ToList());
        Items.Clear();
        foreach (var i in ordered)
            Items.Add(i);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static PegboardItemVm ToVm(PegboardItemSnapshot snapshot)
        => new() { Snapshot = snapshot, Pinned = snapshot.Pinned };

    private static IEnumerable<PegboardItemVm> Sort(IEnumerable<PegboardItemVm> items)
        => items.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Snapshot.CreatedUtc);

    private void UpdateEmptyMessage()
    {
        var visible = Items.Where(i => i.Matches(_terms)).ToList();
        var shown = visible.Count;
        ShowSections = visible.Any(i => i.Pinned) && visible.Any(i => !i.Pinned);
        CountLabel = Items.Count == 0 ? "" : _terms.Length == 0 ? $"{Items.Count}" : $"{shown} / {Items.Count}";
        EmptyMessage = Items.Count == 0
            ? "スニペット・URL・ファイルパスを置いておく場所です。Ctrl+V でクリップボードを、ここへのドロップでテキストやファイルを残せます。"
            : shown == 0 ? "一致するものはありません。" : "";
    }
}
