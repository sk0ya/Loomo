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

/// <summary>
/// ブラウザサポートペイン（<see cref="PaneKind.BrowserSupport"/>、§41）。前に出ているブラウザのタブのページに合った
/// 道具を出す。どのページにどの道具かは <see cref="BrowserSupportCatalog"/>、手元のクローンを探すのと押したときの中身は
/// ShellWindow（部屋のペインを動かすので）。
/// </summary>
public sealed partial class BrowserSupportViewModel : ObservableObject
{
    /// <summary>いま見せているページ（知らないページなら null）。</summary>
    public BrowserSupportPage? Page { get; private set; }

    /// <summary>手元のクローン（無い・まだ探している間は null）。</summary>
    public string? RepositoryRoot { get; private set; }

    [ObservableProperty] private bool _hasPage;
    [ObservableProperty] private string _site = "";
    [ObservableProperty] private string _heading = "";
    [ObservableProperty] private string _detail = "";
    /// <summary>手元のクローンの在りか（または無いこと）・押した道具の失敗。</summary>
    [ObservableProperty] private string _status = "";
    /// <summary>知らないページのときの案内。</summary>
    [ObservableProperty] private string _emptyText = "";

    public ObservableCollection<BrowserSupportActionRow> Actions { get; } = [];

    private string? _key;

    public event Action<BrowserSupportAction>? ActionRequested;

    /// <summary>ページが替わった。手元のクローンはまだ分からないので、要らない道具だけ先に出す。
    /// 同じページ（同じ URL の形）なら何もしない——SPA の同一ページ内の遷移やタイトルの確定で並びを作り直さない。</summary>
    public bool Show(BrowserSupportPage? page, string? url)
    {
        // 道具の並びは毎回作り直した別の列なので、ページの同一は URL から読んだ中身（値の record）で見る。
        var key = page is null ? $"none:{string.IsNullOrWhiteSpace(url)}" : $"{page.Site}|{page.Heading}|{page.AzureDevOps}";
        if (key == _key)
            return false;
        _key = key;
        Page = page;
        RepositoryRoot = null;
        HasPage = page is not null;
        Site = page?.Site ?? "";
        Heading = page?.Heading ?? "";
        Detail = page?.Detail ?? "";
        Status = page?.AzureDevOps is not null ? "手元のクローンを探しています…" : "";
        EmptyText = page is null
            ? (string.IsNullOrWhiteSpace(url) ? "ブラウザのタブがありません。" : "このページに合う道具はありません。") +
              "\n\n対応しているページ:\n・Azure DevOps（Azure Repos）の PR・ファイル・コミット"
            : "";
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
