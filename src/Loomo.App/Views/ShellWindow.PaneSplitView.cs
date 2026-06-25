using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン内分割のビューポート木（<see cref="ShellWindow.PaneSplitView"/>）と
/// そのノード型。1つのペイン（Editor / Terminal）の ContentHost 内を複数ビューポートへ分割管理する。
/// 分割操作の入口（Ctrl+W v/s/q）は <c>ShellWindow.ViewportSplit.cs</c>。</summary>
public partial class ShellWindow
{
    // ===== ペイン内分割（vim 風 Ctrl+W v/s）=====

    /// <summary>ビューポート木の1ノード。</summary>
    private abstract class ViewNode
    {
        /// <summary>親スプリット内での star 比率。</summary>
        public double Weight { get; set; } = 1;
        /// <summary>直近の描画で割り当てられた Grid トラック番号（未描画は -1）。サイズ取り込み用。</summary>
        public int TrackIndex { get; set; } = -1;
    }

    /// <summary>リーフ＝1ビューポート。<see cref="TabId"/> のコントロールを <see cref="Container"/> に映す。</summary>
    private sealed class ViewLeaf : ViewNode
    {
        /// <summary>ビューポートの安定ID（フォーカス追跡・ナビ用。タブIDとは別）。</summary>
        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>このビューポートが表示しているタブ。</summary>
        public Guid TabId { get; set; }
        /// <summary>コントロールを内包する枠（フォーカス時にアクセント枠を出す）。再構築で再利用する。</summary>
        public Border Container { get; } = new() { BorderThickness = new Thickness(0), Focusable = false };
    }

    /// <summary>スプリット＝入れ子の行（上下）または列（左右）。</summary>
    private sealed class ViewSplit : ViewNode
    {
        public SplitKind Orientation { get; init; }
        public List<ViewNode> Children { get; } = new();
        public Grid? Host { get; set; }
    }

    /// <summary>
    /// 1つのペイン（Editor / Terminal）の <c>ContentHost</c>（Grid）内を複数ビューポートへ分割管理する。
    /// 各ビューポートは既存タブの1つを表示する（コントロールは1タブ＝1インスタンスのため）。表示していない
    /// コントロールは <see cref="_parking"/>（非表示）へ退避し破棄しない。トップレベルのペイン木とは独立。
    /// </summary>
    private sealed partial class PaneSplitView
    {
        private readonly Grid _host;
        private readonly Func<Guid, FrameworkElement?> _resolve;        // タブID → コントロール
        private readonly Func<IEnumerable<FrameworkElement>> _allControls;
        private readonly Func<Brush> _border;
        private readonly Func<Brush> _accent;
        private readonly Action<FrameworkElement> _focusControl;
        private readonly Action _onChanged;
        private readonly Grid _parking = new() { Visibility = Visibility.Collapsed };

        private ViewNode? _root;
        private ViewLeaf? _focused;

        public PaneSplitView(
            Grid host,
            Func<Guid, FrameworkElement?> resolve,
            Func<IEnumerable<FrameworkElement>> allControls,
            Func<Brush> border,
            Func<Brush> accent,
            Action<FrameworkElement> focusControl,
            Action onChanged)
        {
            _host = host;
            _resolve = resolve;
            _allControls = allControls;
            _border = border;
            _accent = accent;
            _focusControl = focusControl;
            _onChanged = onChanged;
        }

        public int LeafCount => Leaves().Count();
        public Guid? FocusedTabId => _focused?.TabId;
        public Guid? FocusedViewportId => _focused?.Id;
        public bool IsShown(Guid tabId) => Leaves().Any(l => l.TabId == tabId);

        /// <summary>木を捨ててコンテンツホストを空にする（ワークスペース切替時）。コントロールは破棄しない。</summary>
        public void Reset()
        {
            _root = null;
            _focused = null;
            _host.Children.Clear();
            _host.RowDefinitions.Clear();
            _host.ColumnDefinitions.Clear();
        }

        /// <summary>指定タブを表示する。既に表示中ならそのビューポートへフォーカス、無ければフォーカス中ビューポートへ割り当てる。</summary>
        public void Activate(Guid tabId)
        {
            if (_root is null)
            {
                var leaf = new ViewLeaf { TabId = tabId };
                _root = leaf;
                _focused = leaf;
            }
            else if (FindLeafByTab(tabId) is { } shown)
            {
                _focused = shown;
            }
            else
            {
                _focused ??= Leaves().FirstOrDefault();
                if (_focused is null)
                {
                    var leaf = new ViewLeaf { TabId = tabId };
                    _root = leaf;
                    _focused = leaf;
                }
                else
                {
                    _focused.TabId = tabId;
                }
            }
            Rebuild();
            FocusFocused();
        }

        /// <summary>フォーカス中ビューポートの隣へ新しいビューポート（newTabId 表示）を挿入し、そこへフォーカスする。</summary>
        public void SplitFocused(SplitKind orientation, Guid newTabId)
        {
            CaptureSizes();
            var target = _focused ?? Leaves().FirstOrDefault();
            var leaf = new ViewLeaf { TabId = newTabId };
            if (target is null || _root is null)
            {
                _root = leaf;
            }
            else
            {
                Insert(target, leaf, orientation);
                _root = Normalize(_root);
            }
            _focused = leaf;
            Rebuild();
            FocusFocused();
        }

        /// <summary>フォーカス中ビューポートを畳む（2枚以上のときのみ）。タブ自体は閉じない。</summary>
        public bool CloseFocused()
        {
            if (LeafCount <= 1 || _focused is null)
                return false;
            CaptureSizes();
            Remove(_focused);
            _root = Normalize(_root);
            _focused = Leaves().FirstOrDefault();
            Rebuild();
            FocusFocused();
            return true;
        }

        /// <summary>タブが閉じられたとき：それを表示していたビューポートを畳む（最後の1枚なら残す）。</summary>
        public void RemoveTab(Guid tabId)
        {
            var leaf = FindLeafByTab(tabId);
            if (leaf is null)
                return;
            if (LeafCount > 1)
            {
                Remove(leaf);
                _root = Normalize(_root);
                if (_focused == leaf)
                    _focused = Leaves().FirstOrDefault();
            }
        }

        /// <summary>表示中タブが有効IDの集合に無いビューポートを、未使用の有効タブへ振り直す。</summary>
        public void RepairTabs(IEnumerable<Guid> validTabIds)
        {
            var valid = validTabIds.ToHashSet();
            var used = new HashSet<Guid>();
            foreach (var leaf in Leaves())
            {
                if (!valid.Contains(leaf.TabId))
                {
                    var replacement = valid.FirstOrDefault(v => !used.Contains(v));
                    if (replacement != default || valid.Contains(default))
                        leaf.TabId = replacement;
                }
                used.Add(leaf.TabId);
            }
        }

        /// <summary>指定ビューポートをフォーカスして中身のコントロールへキーボードフォーカスを移す。</summary>
        public void FocusViewport(Guid viewportId)
        {
            if (FindLeafById(viewportId) is not { } leaf)
                return;
            _focused = leaf;
            UpdateFocusBorders();
            if (_resolve(leaf.TabId) is { } control)
                _focusControl(control);
        }

        /// <summary>キーボードフォーカスを得た要素から、それを内包するビューポートをフォーカス扱いにする（再フォーカスはしない）。</summary>
        public Guid? SetFocusedFromElement(DependencyObject element)
        {
            foreach (var leaf in Leaves())
            {
                for (DependencyObject? cur = element; cur is not null; cur = VisualTreeHelper.GetParent(cur))
                {
                    if (ReferenceEquals(cur, leaf.Container))
                    {
                        _focused = leaf;
                        UpdateFocusBorders();
                        return leaf.Id;
                    }
                }
            }
            return null;
        }

        /// <summary>フォーカス中ビューポートのコントロールへキーボードフォーカスを移す。</summary>
        public void FocusFocused()
        {
            if (_focused is { } leaf && _resolve(leaf.TabId) is { } control)
                _focusControl(control);
        }

        /// <summary>フォーカス中ビューポートから指定方向の最寄りビューポートへ移る。移れたときだけ true。</summary>
        public bool FocusInDirection(DropZone direction, Visual relativeTo)
        {
            if (_focused is null || LeafCount <= 1)
                return false;

            var rects = ViewportRects(relativeTo).ToList();
            var fromEntry = rects.FirstOrDefault(r => r.Id == _focused.Id);
            if (fromEntry.Id == default)
                return false;

            var from = fromEntry.Rect;
            var fromCenter = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
            Guid bestId = default;
            var bestScore = double.MaxValue;

            foreach (var (id, rect) in rects)
            {
                if (id == _focused.Id)
                    continue;

                const double tolerance = 1.0;
                var inDirection = direction switch
                {
                    DropZone.Left => rect.X + rect.Width <= from.X + tolerance,
                    DropZone.Right => rect.X >= from.X + from.Width - tolerance,
                    DropZone.Above => rect.Y + rect.Height <= from.Y + tolerance,
                    _ => rect.Y >= from.Y + from.Height - tolerance,
                };
                if (!inDirection)
                    continue;

                var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                var (axis, perpendicular) = direction is DropZone.Left or DropZone.Right
                    ? (Math.Abs(center.X - fromCenter.X), Math.Abs(center.Y - fromCenter.Y))
                    : (Math.Abs(center.Y - fromCenter.Y), Math.Abs(center.X - fromCenter.X));
                var score = axis + perpendicular * 2;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestId = id;
                }
            }

            if (bestId == default)
                return false;

            FocusViewport(bestId);
            return true;
        }

        /// <summary>各ビューポートの矩形（relativeTo 座標系）を列挙する。</summary>
        public IEnumerable<(Guid Id, Rect Rect)> ViewportRects(Visual relativeTo)
        {
            foreach (var leaf in Leaves())
            {
                var c = leaf.Container;
                if (!c.IsVisible || c.ActualWidth <= 0 || c.ActualHeight <= 0)
                    continue;
                var topLeft = c.TransformToVisual(relativeTo).Transform(new Point(0, 0));
                yield return (leaf.Id, new Rect(topLeft, new Size(c.ActualWidth, c.ActualHeight)));
            }
        }

    }
}
