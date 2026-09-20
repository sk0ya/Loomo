namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: エクスプローラ内のセクション（フォルダーツリー／タブ一覧）の高さ配分。
/// 上下の境目は <c>SidebarTabsSplitter</c> でドラッグでき、ダブルクリックで既定値へ戻す。
/// タブセクションを折りたたむと行を Auto（見出しのみ）へ落とし、展開時に直前の高さへ戻す。</summary>
public partial class ShellWindow {
    /// <summary>タブセクションの既定高さ（px）。ダブルクリックでのリセット値でもある。</summary>
    private const double DefaultTabsSectionHeight = 200;
    /// <summary>折りたたむ直前の高さ。展開したときにここへ戻す。</summary>
    private double _savedTabsSectionHeight = DefaultTabsSectionHeight;

    private void InitializeSidebarSections() {
        SidebarTabsSplitter.Cursor = Cursors.SizeNS;
        SidebarTabsSplitter.MouseEnter += (_, _) => SidebarTabsSplitter.Background = (Brush)FindResource("Accent");
        SidebarTabsSplitter.MouseLeave += (_, _) => SidebarTabsSplitter.Background = (Brush)FindResource("Border");
        SidebarTabsSplitter.MouseDoubleClick += (_, _) =>
            SidebarTabsRow.Height = new GridLength(DefaultTabsSectionHeight);
        _vm.Tabs.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(ViewModels.TabsViewModel.IsSectionExpanded))
                ApplyTabsSectionState();
        };
        ApplyTabsSectionState();
    }

    private void ApplyTabsSectionState() {
        if (_vm.Tabs.IsSectionExpanded) {
            SidebarTabsRow.MinHeight = 26;
            SidebarTabsRow.Height = new GridLength(_savedTabsSectionHeight);
            SidebarTabsSplitter.Visibility = Visibility.Visible;
            return;
        }
        // 実測値を覚える：ドラッグ後の行は px とは限らない（* へ変わることがある）ため、
        // Height ではなく ActualHeight を見る。見出しだけの高さは記憶しない。
        if (SidebarTabsRow.ActualHeight > 40)
            _savedTabsSectionHeight = SidebarTabsRow.ActualHeight;
        SidebarTabsRow.MinHeight = 0;
        SidebarTabsRow.Height = GridLength.Auto;
        SidebarTabsSplitter.Visibility = Visibility.Collapsed;
    }
}
