using sk0ya.Loomo.App.Services.Infrastructure;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ構成名の追加・変更・削除をダイアログとプロファイルVMへ接続する。</summary>
internal sealed class DebugProfileEditorController(
    Func<DebugProfilesViewModel?> resolveProfiles,
    Func<Window?> resolveOwner)
{
    public void AddProfile()
    {
        var profiles = resolveProfiles();
        if (profiles is null)
            return;
        var name = Views.InputDialog.Prompt(resolveOwner(), "新しい構成", "構成名を入力してください:");
        if (!string.IsNullOrWhiteSpace(name))
            profiles.AddProfile(name);
    }

    public void RenameProfile()
    {
        var profiles = resolveProfiles();
        if (profiles?.SelectedProfile is not { } selected)
            return;
        var name = Views.InputDialog.Prompt(resolveOwner(), "構成名の変更", "構成名を入力してください:", selected.Name);
        if (!string.IsNullOrWhiteSpace(name))
            profiles.RenameSelectedProfile(name);
    }

    public void DeleteProfile() => resolveProfiles()?.DeleteSelectedProfile();
}
