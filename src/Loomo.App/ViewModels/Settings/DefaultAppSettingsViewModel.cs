using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Targets = sk0ya.Loomo.App.Services.DefaultAppRegistration.Targets;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>設定オーバーレイの「既定のアプリ」セクション。対象ごとのスイッチを切り替えると、その場で
/// Windows への登録に反映する（<see cref="DefaultAppRegistration"/>）。表示はいつもレジストリから読み直す——
/// 設定ファイルには控えないので、Windows 側で消されても食い違わない。
/// 最後の一手（既定にする）は Windows の設定画面でしかできないので、そこへ渡すボタンを置く。</summary>
[SupportedOSPlatform("windows")]
public sealed partial class DefaultAppSettingsViewModel : ObservableObject
{
    private readonly Func<DefaultAppRegistration> _registration;
    private bool _refreshing;

    [ObservableProperty] private bool _text;
    [ObservableProperty] private bool _preview;
    [ObservableProperty] private bool _web;
    [ObservableProperty] private bool _folder;

    /// <summary>登録先が別の場所の Loomo のときの説明。空なら問題なし。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathMismatch))]
    [NotifyCanExecuteChangedFor(nameof(RetargetCommand))]
    private string _pathMismatch = "";

    [ObservableProperty] private string _status = "";

    public DefaultAppSettingsViewModel() : this(DefaultAppRegistration.ForCurrentUser) { }

    internal DefaultAppSettingsViewModel(Func<DefaultAppRegistration> registration)
    {
        _registration = registration;
    }

    public bool HasPathMismatch => PathMismatch.Length > 0;

    public string TextExtensions { get; } = string.Join(" ", DefaultAppRegistration.TextExtensions);
    public string PreviewExtensions { get; } = string.Join(" ", DefaultAppRegistration.PreviewExtensions);
    public string WebTargets { get; } =
        string.Join(" ", DefaultAppRegistration.UrlSchemes.Select(s => s + "://").Concat(DefaultAppRegistration.HtmlExtensions));

    private Targets Selected =>
        (Text ? Targets.Text : Targets.None)
        | (Preview ? Targets.Preview : Targets.None)
        | (Web ? Targets.Web : Targets.None)
        | (Folder ? Targets.Folder : Targets.None);

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            var registration = _registration();
            var (targets, path) = registration.Inspect();
            Text = targets.HasFlag(Targets.Text);
            Preview = targets.HasFlag(Targets.Preview);
            Web = targets.HasFlag(Targets.Web);
            Folder = targets.HasFlag(Targets.Folder);
            PathMismatch = targets != Targets.None && !registration.IsCurrentExecutable(path)
                ? $"別の場所の Loomo が登録されています（{path}）。"
                : "";
        }
        catch (Exception ex)
        {
            Status = $"登録状態を読めませんでした: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnTextChanged(bool value) => ApplySelection();
    partial void OnPreviewChanged(bool value) => ApplySelection();
    partial void OnWebChanged(bool value) => ApplySelection();
    partial void OnFolderChanged(bool value) => ApplySelection();

    private void ApplySelection()
    {
        if (_refreshing)
            return;
        Apply(Selected, Selected == Targets.None
            ? "Windows の選択肢から Loomo を外しました。"
            : "選択肢に加えました（既定はまだ変わっていません）。既定にするには「Windows の設定を開く」から選んでください。");
    }

    /// <summary>登録先を今の Loomo に向け直す（選んでいる対象はそのまま）。</summary>
    [RelayCommand(CanExecute = nameof(HasPathMismatch))]
    private void Retarget() => Apply(Selected, "この Loomo に向け直しました。");

    private void Apply(Targets targets, string done)
    {
        try
        {
            _registration().Apply(targets);
            Status = done;
        }
        catch (Exception ex)
        {
            Status = $"反映できませんでした: {ex.Message}";
        }
        Refresh();
    }

    [RelayCommand]
    private void OpenWindowsSettings()
    {
        try { DefaultAppRegistration.OpenWindowsDefaultAppsSettings(); }
        catch (Exception ex) { Status = $"Windows の設定を開けませんでした: {ex.Message}"; }
    }
}
