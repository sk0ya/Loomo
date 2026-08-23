using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel
{
    /// <summary>選択集合のうち、Explorerへピン留め可能なフォルダーがあるか。混在選択では
    /// 対象になるフォルダーだけを一括操作し、ファイルや仮想Shell項目は無視する。</summary>
    public bool CanPinToQuickAccess(IEnumerable<FileNodeViewModel> nodes)
        => QuickAccess.IsAvailable && nodes.Any(node =>
            node.IsDirectory && !node.IsShellItem && QuickAccess.CanPin(node.FullPath));

    public bool CanUnpinFromQuickAccess(IEnumerable<FileNodeViewModel> nodes)
        => QuickAccess.IsAvailable && nodes.Any(node =>
            node.IsDirectory && !node.IsShellItem && QuickAccess.IsPinned(node.FullPath));

    public QuickAccessBatchResult PinToQuickAccess(IEnumerable<FileNodeViewModel> nodes)
        => QuickAccess.PinMany(nodes
            .Where(node => node.IsDirectory && !node.IsShellItem)
            .Select(node => node.FullPath)
            .Where(QuickAccess.CanPin));

    public QuickAccessBatchResult UnpinFromQuickAccess(IEnumerable<FileNodeViewModel> nodes)
        => QuickAccess.UnpinMany(nodes
            .Where(node => node.IsDirectory && !node.IsShellItem)
            .Select(node => node.FullPath)
            .Where(QuickAccess.IsPinned));
}
