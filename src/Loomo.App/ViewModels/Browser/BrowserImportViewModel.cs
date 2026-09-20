using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>取り込み元の候補1つ（ブラウザ×プロファイル）。</summary>
public sealed partial class BrowserImportSourceViewModel : ObservableObject
{
    public required ChromiumProfileRef Profile { get; init; }

    public string Label => Profile.Label;

    /// <summary>その相手固有の但し書き（「Cookie を取り込むには終了が必要」「パスワードは
    /// アプリ束縛暗号で読めない」）。<b>選ぶ前に見えている</b>ことが大事——押してから
    /// 「できませんでした」と言われるのが一番腹立たしい。</summary>
    [ObservableProperty] private string _note = "";

    /// <summary>選択前に分かるブックマーク件数。取り込んでから空振りに気付かないようにする。</summary>
    public int BookmarkCount { get; init; }
    public string BookmarkCountText => $"ブックマーク {BookmarkCount} 件";

    /// <summary>暗号鍵・ファイル状態から今すぐ読める種類。利用不可のチェックを押させない。</summary>
    public bool CanImportPasswords { get; init; } = true;
    public bool CanImportCookies { get; init; } = true;

    public bool HasNote => Note.Length > 0;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    /// <summary>一覧の見た目は <c>DisplayMemberPath</c> が作るが、支援技術（と UI 自動化）が読むのは
    /// こちら——既定のままだと型名が読み上げられる。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// 他のブラウザからの取り込み（設計書 §21.5.4）の画面状態。
///
/// <para>この VM は<b>選ばせるだけ</b>で、読み書きはしない。取り込みは
/// プロファイル（＝WebView2 の実体）と <c>browser.json</c> の両方に触るので、実行はシェルの仕事——
/// 拡張機能・保存済みログイン情報と同じ分担にしてある。</para>
/// </summary>
public sealed partial class BrowserImportViewModel : ObservableObject
{
    public ObservableCollection<BrowserImportSourceViewModel> Sources { get; } = new();

    [ObservableProperty] private BrowserImportSourceViewModel? _selectedSource;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    // 既定は全部入り。「引っ越してきた」と感じられるのが目的で、
    // 個別に外したい人だけが外せばよい。
    [ObservableProperty] private bool _importBookmarks = true;
    [ObservableProperty] private bool _importHistory = true;
    [ObservableProperty] private bool _importPasswords = true;
    [ObservableProperty] private bool _importCookies = true;

    /// <summary>一覧を作り直す要求（開いたときと、ブラウザを終了してもらった後の押し直し）。</summary>
    public event EventHandler? SourcesRefreshRequested;

    /// <summary>取り込みの実行要求。</summary>
    public event EventHandler<(ChromiumProfileRef Profile, BrowserImportSelection Selection)>? ImportRequested;

    /// <summary>ブラウザが書き出した CSV からパスワードだけ取り込む要求
    /// （アプリ束縛暗号の Chrome から移す唯一の道）。</summary>
    public event EventHandler? CsvImportRequested;

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
            return;
        Status = "";
        SourcesRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedSourceChanged(BrowserImportSourceViewModel? value)
    {
        ImportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedSource));
    }
    partial void OnIsBusyChanged(bool value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnImportBookmarksChanged(bool value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnImportHistoryChanged(bool value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnImportPasswordsChanged(bool value) => ImportCommand.NotifyCanExecuteChanged();
    partial void OnImportCookiesChanged(bool value) => ImportCommand.NotifyCanExecuteChanged();

    public BrowserImportSelection Selection
        => new(ImportBookmarks, ImportHistory,
            ImportPasswords && (SelectedSource?.CanImportPasswords ?? false),
            ImportCookies && (SelectedSource?.CanImportCookies ?? false));

    /// <summary>候補を差し替える。<b>選択は URL ではなくプロファイルのパスで復元する</b>——
    /// 押し直しのたびに選び直させると、「ブラウザを閉じてから再確認」がひどく煩わしい。</summary>
    public void SetSources(IReadOnlyList<BrowserImportSourceViewModel> sources, string? status)
    {
        var previous = SelectedSource?.Profile.Path;
        Sources.Clear();
        foreach (var source in sources)
            Sources.Add(source);
        SelectedSource = Sources.FirstOrDefault(s =>
                string.Equals(s.Profile.Path, previous, StringComparison.OrdinalIgnoreCase))
            ?? Sources.FirstOrDefault();
        if (status is not null)
            Status = status;
        OnPropertyChanged(nameof(HasSources));
    }

    public bool HasSources => Sources.Count > 0;
    public bool HasSelectedSource => SelectedSource is not null;

    private bool CanImport() => !IsBusy && SelectedSource is not null && !Selection.IsEmpty;

    [RelayCommand]
    private void SelectBookmarksOnly()
    {
        ImportBookmarks = true;
        ImportHistory = false;
        ImportPasswords = false;
        ImportCookies = false;
    }

    [RelayCommand]
    private void SelectAll()
    {
        ImportBookmarks = true;
        ImportHistory = true;
        ImportPasswords = true;
        ImportCookies = true;
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        if (SelectedSource is { } source)
            ImportRequested?.Invoke(this, (source.Profile, Selection));
    }

    [RelayCommand]
    private void Refresh() => SourcesRefreshRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ImportCsv() => CsvImportRequested?.Invoke(this, EventArgs.Empty);
}
