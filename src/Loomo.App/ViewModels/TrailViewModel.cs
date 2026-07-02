using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>軌跡（操作ログ）エントリの種別。</summary>
public enum TrailEntryKind
{
    /// <summary>エディタで開いた（アクティブにした）ファイル。</summary>
    File,
    /// <summary>ブラウザで表示したページ。</summary>
    Browser
}

/// <summary>軌跡の1エントリ＝一度通過した地点。チップとして表示し、クリックでその地点へ戻る。</summary>
public sealed partial class TrailEntryViewModel : ObservableObject
{
    public TrailEntryViewModel(TrailEntryKind kind, string target, string label)
    {
        Kind = kind;
        Target = target;
        _label = label;
        Timestamp = DateTime.Now;
    }

    public TrailEntryKind Kind { get; }

    /// <summary>戻り先の実体（ファイルはフルパス、ブラウザは URL）。</summary>
    public string Target { get; }

    /// <summary>チップに出す短い名前（ファイル名／ページタイトル）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private string _label;

    /// <summary>記録時のカーソル行（0始まり。位置情報が無ければ -1）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private int _line = -1;

    /// <summary>記録時のカーソル桁（0始まり。位置情報が無ければ -1）。</summary>
    [ObservableProperty] private int _column = -1;

    /// <summary>最後にこの地点を通過した時刻。</summary>
    public DateTime Timestamp { get; private set; }

    /// <summary>同じ地点を再通過したとき、時刻だけ現在へ更新する。</summary>
    public void Touch()
    {
        Timestamp = DateTime.Now;
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>チップ先頭の種別アイコン。</summary>
    public string Glyph => Kind == TrailEntryKind.Browser ? "🌐" : "📄";

    /// <summary>チップの表示文字列。ファイルは「名前:行」（行は1始まりで表示）、ブラウザはタイトル。</summary>
    public string Display => Kind == TrailEntryKind.File && Line >= 0 ? $"{Label}:{Line + 1}" : Label;

    public string Tooltip
    {
        get
        {
            var location = Kind == TrailEntryKind.File && Line >= 0
                ? $"{Target}:{Line + 1}"
                : Target;
            return $"{location}\n{Timestamp:HH:mm:ss} に通過。クリックでこの地点へ戻る";
        }
    }
}

/// <summary>ウィンドウ最下部の「軌跡」バー（操作ログ）。エディタで開いたファイルとブラウザの遷移を
/// 時系列に記録し、チップのクリックでその地点へ戻る。アイデア.md「Semantic Depth」構想の
/// 最初の一歩（Thread Rail の種＝出自付きジャンプ履歴）の MVP で、記録はセッション限り（永続化しない）。
/// 実際の遷移（タブ活性化・NavigateTo・ブラウザナビゲート）は <see cref="JumpRequested"/> を受けた
/// ShellWindow（ShellWindow.Trail.cs）が行う。</summary>
public sealed partial class TrailViewModel : ObservableObject
{
    /// <summary>保持する最大エントリ数。超えたら古い順に捨てる。</summary>
    public const int MaxEntries = 60;

    public ObservableCollection<TrailEntryViewModel> Entries { get; } = new();

    /// <summary>チップがクリックされ、その地点へ戻りたい。</summary>
    public event EventHandler<TrailEntryViewModel>? JumpRequested;

    /// <summary>バー自体の表示切替（1件も無ければバーごと隠して高さを取らない）。</summary>
    [ObservableProperty] private bool _hasEntries;

    public TrailEntryViewModel? LatestEntry => Entries.Count > 0 ? Entries[^1] : null;

    /// <summary>ファイル地点を記録する。直前と同じファイルなら追記せず位置・時刻だけ更新する
    /// （タブ切替の往復やフォーカス移動で同じチップが増殖しないように）。</summary>
    public void RecordFile(string path, int line = -1, int column = -1)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (LatestEntry is { Kind: TrailEntryKind.File } last
            && string.Equals(last.Target, path, StringComparison.OrdinalIgnoreCase))
        {
            if (line >= 0)
            {
                last.Line = line;
                last.Column = column;
            }
            last.Touch();
            return;
        }

        Append(new TrailEntryViewModel(TrailEntryKind.File, path, Path.GetFileName(path))
        {
            Line = line,
            Column = column
        });
    }

    /// <summary>ブラウザ地点を記録する。直前と同じ URL ならタイトル・時刻だけ更新する
    /// （NavigationCompleted 時点ではタイトルが未確定のことがあるため、後追いで整う）。</summary>
    public void RecordBrowser(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        var label = string.IsNullOrWhiteSpace(title) ? HostOf(url) : title.Trim();
        if (LatestEntry is { Kind: TrailEntryKind.Browser } last
            && string.Equals(last.Target, url, StringComparison.Ordinal))
        {
            last.Label = label;
            last.Touch();
            return;
        }

        Append(new TrailEntryViewModel(TrailEntryKind.Browser, url, label));
    }

    /// <summary>エントリのカーソル位置を上書きする。新しい地点を記録する直前に、離れるファイルの
    /// 現在カーソルで最新エントリを更新するのに使う（「戻る」を到着時でなく離脱時の場所にする）。</summary>
    public void UpdateFilePosition(TrailEntryViewModel entry, int line, int column)
    {
        if (entry.Kind != TrailEntryKind.File || line < 0)
            return;
        entry.Line = line;
        entry.Column = column;
    }

    private void Append(TrailEntryViewModel entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(0);
        HasEntries = true;
    }

    private static string HostOf(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : url;
    }

    [RelayCommand]
    private void Jump(TrailEntryViewModel? entry)
    {
        if (entry is not null)
            JumpRequested?.Invoke(this, entry);
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        HasEntries = false;
    }
}
