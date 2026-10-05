namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 軌跡のしおり（§27.13）。軌跡は自動で積まれる通過記録で、人が意味を付けた点が無かった——
/// 「ここまで調べた」「この状態で再現した」と一言添えて残し、日をまたいだ一覧からそこへ戻る。
/// しおりはメモ1つを地点（SQLite の行）に足すだけで、戻り方は普通のドットのクリックと同じ（§27.6）。</summary>
public partial class ShellWindow {
    /// <summary>右クリックされたドット（ドットの外で右クリックしたら null＝現在地が対象）。</summary>
    private TrailEntryViewModel? _trailContextEntry;
    /// <summary>開いているメニューが対象にしている地点。</summary>
    private TrailEntryViewModel? _trailMenuTarget;

    private void InitializeTrailBookmarks() {
        TrailDots.PreviewMouseRightButtonDown += (_, e) =>
            _trailContextEntry = (e.OriginalSource as FrameworkElement)?.DataContext as TrailEntryViewModel;
    }

    /// <summary>バーの右クリックメニューを開いた：対象（右クリックしたドット、無ければ現在地）に合わせて
    /// 「付ける／編集する」の見出しと可否を決める。</summary>
    private void OnTrailContextMenuOpened(object sender, RoutedEventArgs e) {
        _trailMenuTarget = _trailContextEntry ?? _vm.Trail.CurrentEntry;
        _trailContextEntry = null;
        var target = _trailMenuTarget;
        TrailBookmarkMenuItem.IsEnabled = CanBookmark(target);
        TrailBookmarkMenuItem.Header = target is { HasNote: true }
            ? "この地点のしおりを編集…"
            : _trailMenuTarget is not null && ReferenceEquals(target, _vm.Trail.CurrentEntry)
                ? "現在地にしおりを付ける…"
                : "この地点にしおりを付ける…";
    }

    private void OnTrailBookmarkEntry(object sender, RoutedEventArgs e) => BookmarkTrailEntry(_trailMenuTarget);

    private void OnTrailShowBookmarks(object sender, RoutedEventArgs e) => OpenTrailBookmarks();

    /// <summary>Git の地点はログ専用で戻り先が無い（§27.6）ので、しおりを付けても戻れない。</summary>
    private static bool CanBookmark(TrailEntryViewModel? entry)
        => entry is not null && entry.Kind != TrailEntryKind.Git;

    /// <summary>地点にしおりを付ける（既にあれば編集・空にすれば外す）。</summary>
    private void BookmarkTrailEntry(TrailEntryViewModel? entry) {
        if (!CanBookmark(entry)) {
            ToastService.Info(entry is null
                ? "しおりを付ける地点がまだありません（ファイルやページを開くと軌跡に点が積まれます）。"
                : "Git の操作記録には戻り先が無いため、しおりを付けられません。");
            return;
        }
        var target = entry!;
        var answer = InputDialog.Prompt(this, "しおり",
            $"「{target.Label}」（{target.Timestamp:M/d HH:mm}）に残す一言。空にするとしおりを外します。",
            target.Note ?? "", allowEmpty: true);
        if (answer is null)
            return;   // キャンセル
        _vm.Trail.SetNote(target, answer);
    }

    private const string TrailBookmarkCategory = "しおり";

    /// <summary>しおり一覧（新しい順・日をまたぐ）をパレットで開く。選ぶとその日の軌跡を出してその地点へ戻る。</summary>
    private void OpenTrailBookmarks() {
        var bookmarks = _vm.Trail.ListBookmarks();
        var items = bookmarks.Select(bookmark => new PaletteCommand(TrailBookmarkCategory, bookmark.Note,
            () => {
                if (!_vm.Trail.JumpToBookmark(bookmark))
                    ToastService.Info("そのしおりの地点が見つかりませんでした。");
            }) {
            Badge = bookmark.Timestamp.ToString("M/d HH:mm"),
            Detail = TrailBookmarkDetail(bookmark),
        }).ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand(TrailBookmarkCategory, "しおりはまだありません", () => { }) {
                Detail = "軌跡バーのドットを右クリック →「この地点にしおりを付ける…」、またはパレットの"
                         + "「いまの地点にしおりを付ける」で、通った地点に一言添えて残せます。",
            });
        OpenCommandPalette(PaletteMode.Command, items);
    }

    private static string TrailBookmarkDetail(TrailNoteRecord bookmark) {
        var kind = (TrailEntryKind)bookmark.Kind;
        var where = kind is TrailEntryKind.File or TrailEntryKind.Edit or TrailEntryKind.Preview or TrailEntryKind.Browser
            ? bookmark.Target
            : bookmark.Label;
        return $"{bookmark.Note}{Environment.NewLine}{Environment.NewLine}"
               + $"{bookmark.Label}{Environment.NewLine}{where}{Environment.NewLine}"
               + $"{bookmark.Timestamp:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{Environment.NewLine}"
               + "Enter でその日の軌跡を出し、この地点へ戻る";
    }
}
