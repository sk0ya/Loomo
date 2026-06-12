using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
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

    /// <summary>カードの見出し（Title 優先、無ければ本文の先頭行）。</summary>
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Snapshot.Title)) return Snapshot.Title!;
            var firstLine = Content.AsSpan().TrimStart();
            var newline = firstLine.IndexOfAny('\r', '\n');
            return (newline >= 0 ? firstLine[..newline] : firstLine).ToString();
        }
    }

    /// <summary>本文プレビュー（見出しと重複する単一行アイテムは空にして高さを節約）。</summary>
    public string Preview
        => Content.Contains('\n') ? Content : (DisplayTitle == Content ? "" : Content);

    public string TypeGlyph => Type switch { "url" => "🔗", "file" => "📄", _ => "📝" };
    public string CreatedLabel => Snapshot.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string OpenLabel => Type switch
    {
        "url" => "ブラウザで開く",
        "file" => "エディタで開く",
        _ => "エディタで開く",
    };
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

    /// <summary>「ブラウザのURLをピン」要求。表示中 URL の取得は ShellWindow が担う。</summary>
    public event EventHandler? BrowserPinRequested;

    [ObservableProperty] private string _emptyMessage = "";

    public PegboardViewModel() => UpdateEmptyMessage();

    /// <summary>ワークスペース切替時にアイテムを入れ替える（保存は発火しない）。</summary>
    public void LoadItems(IEnumerable<PegboardItemSnapshot> snapshots)
    {
        Items.Clear();
        foreach (var s in Sort(snapshots.Select(ToVm)))
            Items.Add(s);
        UpdateEmptyMessage();
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

    /// <summary>内容から種別（text/url/file）を判定して追加する。明示指定があればそれを使う。</summary>
    public void AddContent(string content, string? type = null, string? title = null)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0) return;

        var snapshot = new PegboardItemSnapshot
        {
            Type = type ?? DetectType(trimmed),
            Content = trimmed,
            Title = title,
        };
        // 新規は「ピン留め群の直後」（未ピンの先頭）に置く。
        var vm = ToVm(snapshot);
        Items.Insert(Items.Count(i => i.Pinned), vm);
        UpdateEmptyMessage();
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

    /// <summary>ブラウザで表示中のページをピンする（URL 取得は ShellWindow 側）。</summary>
    [RelayCommand]
    private void PinBrowserUrl() => BrowserPinRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Copy(PegboardItemVm item)
    {
        try { Clipboard.SetText(item.Content); } catch { /* 無視 */ }
    }

    [RelayCommand]
    private void Open(PegboardItemVm item) => OpenRequested?.Invoke(this, item);

    [RelayCommand]
    private void Delete(PegboardItemVm item)
    {
        Items.Remove(item);
        UpdateEmptyMessage();
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
        => EmptyMessage = Items.Count > 0
            ? ""
            : "まだ何もありません。「クリップボードから追加」や ブラウザの「URLをピン」で、スニペット・URL・ファイルパスを貼っておけます。";
}
