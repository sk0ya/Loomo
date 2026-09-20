using System.Windows.Data;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>選択中の <see cref="FilesGroupBy"/> でファイル行をグループ化する WPF アダプター。</summary>
public sealed class FilesGroupDescription(FilesColumnViewModel owner) : GroupDescription
{
    public override object GroupNameFromItem(object item, int level, System.Globalization.CultureInfo culture)
        => item is FileEntryViewModel entry ? entry.GroupValue(owner.GroupBy) : new FilesGroupValue("", "", 0);

    public override bool NamesMatch(object groupName, object itemName)
        => groupName is FilesGroupValue left && itemName is FilesGroupValue right
            ? string.Equals(left.Key, right.Key, StringComparison.Ordinal)
            : base.NamesMatch(groupName, itemName);
}
