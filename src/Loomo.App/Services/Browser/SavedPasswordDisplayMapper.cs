using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>保存済みログイン情報をブラウザペインの表示モデルへ変換する。</summary>
internal static class SavedPasswordDisplayMapper
{
    internal static IReadOnlyList<SavedPasswordViewModel> Map(IEnumerable<SavedPassword> items)
        => items.Select(item => new SavedPasswordViewModel
        {
            Origin = item.Origin,
            Host = item.Host,
            Username = item.Username,
            Password = item.Password,
        }).ToArray();
}
