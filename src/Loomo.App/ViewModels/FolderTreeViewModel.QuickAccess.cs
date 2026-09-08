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

    /// <summary>ピン留め／解除は Explorer の照会と反映待ちで秒単位かかるので、UI スレッドの外で行う。
    /// 対象の絞り込み（フォルダーのみ・仮想 Shell 項目を除く）だけをここで済ませ、ピン済みかどうかの
    /// 判定はサービス側の最新照会に任せる——手元のキャッシュで先に弾くと、期限切れや未照会のときに
    /// 押しても黙って何も起きない。</summary>
    public Task<QuickAccessBatchResult> PinToQuickAccessAsync(IEnumerable<FileNodeViewModel> nodes)
        => QuickAccess.PinManyAsync(QuickAccessTargets(nodes));

    public Task<QuickAccessBatchResult> UnpinFromQuickAccessAsync(IEnumerable<FileNodeViewModel> nodes)
        => QuickAccess.UnpinManyAsync(QuickAccessTargets(nodes));

    private static IReadOnlyList<string> QuickAccessTargets(IEnumerable<FileNodeViewModel> nodes)
        => nodes
            .Where(node => node.IsDirectory && !node.IsShellItem)
            .Select(node => node.FullPath)
            .ToList();

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
