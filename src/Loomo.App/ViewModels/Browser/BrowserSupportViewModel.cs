using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ブラウザサポートの道具の1行（§41）。押すと <see cref="BrowserSupportViewModel.ActionRequested"/>。</summary>
public sealed class BrowserSupportActionRow(BrowserSupportAction action, Action<BrowserSupportAction> run)
{
    public BrowserSupportAction Action { get; } = action;
    public string Label => Action.Label;
    public string Detail => Action.Detail;
    public IRelayCommand RunCommand { get; } = new RelayCommand(() => run(action));
}

/// <summary>節の行に付くボタン1つ。</summary>
public sealed class BrowserSupportButton(string label, Action run, string? tip = null)
{
    public string Label { get; } = label;
    public string? Tip { get; } = tip;
    public IRelayCommand Command { get; } = new RelayCommand(run);
}

/// <summary>節の行の見え方。</summary>
public enum BrowserSupportRowLook
{
    /// <summary>ふつうの文（プロセス・段の名前）。</summary>
    Text,
    /// <summary>ファイル・プロジェクトの場所（等幅の1行）。</summary>
    Path,
    /// <summary>ページのコード（枠の中に等幅で。<see cref="BrowserSupportRow.Detail"/> は枠の見出し＝言語・行数）。</summary>
    Code,
    /// <summary>ページから取った抜き書き（引用の縦線付き）。</summary>
    Quote,
}

/// <summary>節の1行（CI のエラー・パッケージを使っている場所・ページのコード・抜き書き…）。</summary>
public sealed record BrowserSupportRow(string Title, string Detail, IReadOnlyList<BrowserSupportButton> Buttons,
    BrowserSupportRowLook Look = BrowserSupportRowLook.Text)
{
    public bool HasDetail => Detail.Length > 0;
    /// <summary>ボタンが1つなら行の右に並べる（「開く」「戻る」だけのために1段使わない）。コードの行は枠の下に並べる。</summary>
    public bool ButtonInline => Buttons.Count == 1 && Look != BrowserSupportRowLook.Code;
    public bool ButtonsBelow => Buttons.Count > 0 && !ButtonInline;
    public BrowserSupportButton? InlineButton => ButtonInline ? Buttons[0] : null;
}

/// <summary>ページが無いときの案内の1行（どのページで何が出るか）。</summary>
public sealed record BrowserSupportGuideItem(string Site, string Text);

/// <summary>
/// ページから読み出したものを並べる節（§41）。道具（<see cref="BrowserSupportViewModel.Actions"/>）が「ページ1つにつき決まった
/// ボタン」なのに対し、節は<b>読んでみるまで数の分からない行</b>（失敗したログのエラー・使っている場所・コードブロック）。
/// </summary>
public sealed partial class BrowserSupportSection(string key, string title, int order, bool emphasis = false) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public int Order { get; } = order;
    /// <summary>目立たせる節（調べ始めた場所：このタブが何のために開かれたか）。</summary>
    public bool Emphasis { get; } = emphasis;
    /// <summary>見出しの横の件数（空なら出さない）。</summary>
    [ObservableProperty] private string _count = "";
    public bool HasCount => Count.Length > 0;
    partial void OnCountChanged(string value) => OnPropertyChanged(nameof(HasCount));
    /// <summary>読み込み中・0 件・失敗の一言（行の上に出す）。</summary>
    [ObservableProperty] private string _note = "";
    public ObservableCollection<BrowserSupportRow> Rows { get; } = [];
    /// <summary>節の見出しの右のボタン（読み直す 等）。</summary>
    public ObservableCollection<BrowserSupportButton> Buttons { get; } = [];
    public bool HasNote => Note.Length > 0;
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
}

/// <summary>
/// ブラウザサポートペイン（<see cref="PaneKind.BrowserSupport"/>、§41）。前に出ているブラウザのタブのページに合った
/// 道具を出す。どのページにどの道具かは <see cref="BrowserSupportCatalog"/>、手元のクローンを探すのと押したときの中身・
/// 節の中身を読むのは ShellWindow（部屋のペインを動かし、ネットワーク・ディスク・ページへ問い合わせるので）。
/// </summary>
public sealed partial class BrowserSupportViewModel : ObservableObject
{
    /// <summary>いま見せているページ（http(s) でないページなら null）。</summary>
    public BrowserSupportPage? Page { get; private set; }

    /// <summary>手元のクローン（無い・まだ探している間は null）。</summary>
    public string? RepositoryRoot { get; private set; }

    [ObservableProperty] private bool _hasPage;
    [ObservableProperty] private string _site = "";
    [ObservableProperty] private string _heading = "";
    [ObservableProperty] private string _detail = "";
    /// <summary>手元のクローンの在りか（または無いこと）・押した道具の結果。</summary>
    [ObservableProperty] private string _status = "";
    /// <summary>ページが無いときの一言（タブが無い・このページに合う道具が無い）。</summary>
    [ObservableProperty] private string _emptyText = "";

    /// <summary>ページが無いときに並べる、ページごとの道具の案内。</summary>
    public IReadOnlyList<BrowserSupportGuideItem> Guide { get; } = [
        new("Azure DevOps", "PR・ファイル・コミット・パイプラインの実行"),
        new("GitHub", "PR・Issue・ファイル・コミット・Actions の実行"),
        new("npm／NuGet", "このワークスペースでの使われ方と追加"),
        new("localhost", "そのポートを動かしているターミナル"),
        new("記事・ドキュメント", "ページのコードの一覧"),
        new("どのページでも", "そのページからペグボードへ取った抜き書き"),
        new("「ブラウザへ送る」で来たタブ", "調べ始めた場所へ戻る・ページのコードをそこへ貼る"),
    ];

    public ObservableCollection<BrowserSupportActionRow> Actions { get; } = [];
    public ObservableCollection<BrowserSupportSection> Sections { get; } = [];

    public bool HasStatus => Status.Length > 0;
    public bool HasHeading => Heading.Length > 0;
    public bool HasDetail => Detail.Length > 0;
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnHeadingChanged(string value) => OnPropertyChanged(nameof(HasHeading));
    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));

    private string? _key;

    public event Action<BrowserSupportAction>? ActionRequested;

    /// <summary>ページが替わった。手元のクローンはまだ分からないので、要らない道具だけ先に出す。節は空にする。
    /// 同じページ（同じ URL の形）なら何もしない——SPA の同一ページ内の遷移やタイトルの確定で並びを作り直さない。</summary>
    public bool Show(BrowserSupportPage? page, string? url)
    {
        // 道具の並びは毎回作り直した別の列なので、ページの同一は URL から読んだ中身（値の record）で見る。
        var key = page?.Key ?? $"none:{string.IsNullOrWhiteSpace(url)}";
        if (key == _key)
            return false;
        _key = key;
        Page = page;
        RepositoryRoot = null;
        HasPage = page is not null;
        Site = page?.Site ?? "";
        Heading = page?.Heading ?? "";
        Detail = page?.Detail ?? "";
        Status = page?.HasRepository == true ? "手元のクローンを探しています…" : "";
        EmptyText = page is null
            ? (string.IsNullOrWhiteSpace(url) ? "ブラウザのタブがありません" : "このページに合う道具はありません")
            : "";
        Sections.Clear();
        Rebuild();
        return true;
    }

    /// <summary>手元のクローンが分かった（<paramref name="root"/> が null なら無かった）。</summary>
    public void SetRepository(BrowserSupportPage page, string? root, string status)
    {
        if (!ReferenceEquals(page, Page))
            return;   // 探している間に別のページへ移った
        RepositoryRoot = root;
        Status = status;
        Rebuild();
    }

    /// <summary>節を出す・中身を差し替える（<paramref name="page"/> が今のページでなければ捨てる）。並びは <c>order</c> の順。
    /// <paramref name="count"/> は見出しの横の件数（行の数と違うとき——一部だけ並べた——もあるので呼ぶ側が渡す）。
    /// 返すのは節（見出しのボタンを足すため）。</summary>
    public BrowserSupportSection? SetSection(BrowserSupportPage page, string key, string title, int order,
        IReadOnlyList<BrowserSupportRow> rows, string note = "", int? count = null, bool emphasis = false)
    {
        if (!ReferenceEquals(page, Page))
            return null;
        var section = Sections.FirstOrDefault(s => s.Key == key);
        if (section is not null && section.Title != title)
        {
            Sections.Remove(section);   // 見出しが替わった（ポート番号など）：作り直す
            section = null;
        }
        if (section is null)
        {
            section = new BrowserSupportSection(key, title, order, emphasis);
            var index = 0;
            while (index < Sections.Count && Sections[index].Order <= order)
                index++;
            Sections.Insert(index, section);
        }
        section.Note = note;
        section.Count = count is { } n ? n.ToString() : "";
        section.Rows.Clear();
        foreach (var row in rows)
            section.Rows.Add(row);
        return section;
    }

    /// <summary>節を消す（0 件のときに見出しごと出さない節：抜き書き）。</summary>
    public void RemoveSection(BrowserSupportPage page, string key)
    {
        if (ReferenceEquals(page, Page) && Sections.FirstOrDefault(s => s.Key == key) is { } section)
            Sections.Remove(section);
    }

    /// <summary>押した道具の結果・失敗を、手元のクローンの行の場所に出す。</summary>
    public void ReportStatus(string message) => Status = message;

    /// <summary>見せているページを忘れる：次の <see cref="Show"/> は同じページでも作り直す（フォルダーの出し入れでクローンを探し直すとき）。</summary>
    public void Forget() => _key = null;

    private void Rebuild()
    {
        Actions.Clear();
        if (Page is null)
            return;
        foreach (var action in Page.Actions)
            if (!action.NeedsLocalRepository || RepositoryRoot is not null)
                Actions.Add(new BrowserSupportActionRow(action, a => ActionRequested?.Invoke(a)));
    }
}
