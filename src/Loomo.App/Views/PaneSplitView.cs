using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.App.Views;
/// <summary>ペイン内分割のビューポート管理。1つのペイン
/// （Editor / Terminal）の ContentHost 内を複数ビューポートへ分割する。木のノード型は <see cref="ViewNode"/>、
/// 木の構造操作は <see cref="ViewportTree"/>。分割操作の入口（Ctrl+W v/s/q）は <c>ShellWindow.ViewportSplit.cs</c>。</summary>
    /// <summary>
    /// 1つのペイン（Editor / Terminal）の <c>ContentHost</c>（Grid）内を複数ビューポートへ分割管理する。
    /// 各ビューポートは既存タブの1つを表示する（コントロールは1タブ＝1インスタンスのため）。表示していない
    /// コントロールは <see cref="_parking"/>（非表示）へ退避し破棄しない。トップレベルのペイン木とは独立。
    /// </summary>
internal sealed class PaneSplitView
    {
        private const double SplitterThickness = 6;
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
                _root = ViewportTree.Insert(_root, target, leaf, orientation);
                _root = ViewportTree.Normalize(_root);
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
            _root = ViewportTree.Remove(_root, _focused);
            _root = ViewportTree.Normalize(_root);
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
                _root = ViewportTree.Remove(_root, leaf);
                _root = ViewportTree.Normalize(_root);
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

        // ----- レンダリング -----

        public void Rebuild()
        {
            if (_root is null)
            {
                _host.Children.Clear();
                return;
            }
            _root = ViewportTree.Normalize(_root);

            // 既存の親から全コントロール・全コンテナを外してから組み直す。
            foreach (var el in _allControls())
                ViewportTree.Detach(el);
            foreach (var leaf in Leaves())
            {
                leaf.Container.Child = null;
                ViewportTree.Detach(leaf.Container);
            }
            ViewportTree.Detach(_parking);
            _host.Children.Clear();
            _host.RowDefinitions.Clear();
            _host.ColumnDefinitions.Clear();

            var visual = Build(_root!);
            _host.Children.Add(visual);

            // 各ビューポートに表示コントロールを差し込み、残りは parking へ退避。
            var shown = new HashSet<FrameworkElement>();
            foreach (var leaf in Leaves())
            {
                if (_resolve(leaf.TabId) is { } control)
                {
                    ViewportTree.Detach(control);
                    control.Visibility = Visibility.Visible;
                    leaf.Container.Child = control;
                    shown.Add(control);
                }
            }
            _parking.Children.Clear();
            foreach (var el in _allControls())
            {
                if (shown.Contains(el))
                    continue;
                ViewportTree.Detach(el);
                el.Visibility = Visibility.Collapsed;
                _parking.Children.Add(el);
            }
            _host.Children.Add(_parking);

            UpdateFocusBorders();
        }

        private FrameworkElement Build(ViewNode node)
        {
            if (node is ViewLeaf leaf)
                return leaf.Container;

            var split = (ViewSplit)node;
            split.Host = null;
            foreach (var c in split.Children)
                c.TrackIndex = -1;
            if (split.Children.Count == 1)
                return Build(split.Children[0]);

            var grid = new Grid();
            split.Host = grid;
            var cols = split.Orientation == SplitKind.Columns;
            var min = cols ? 160.0 : 80.0;

            for (var i = 0; i < split.Children.Count; i++)
            {
                if (i > 0)
                {
                    AddTrack(grid, cols, new GridLength(SplitterThickness));
                    var splitter = NewSplitter(cols);
                    SetTrack(splitter, cols, i * 2 - 1);
                    grid.Children.Add(splitter);
                }
                var child = split.Children[i];
                AddTrack(grid, cols, new GridLength(child.Weight <= 0 ? 1 : child.Weight, GridUnitType.Star), min);
                child.TrackIndex = i * 2;
                var visual = Build(child);
                SetTrack(visual, cols, i * 2);
                grid.Children.Add(visual);
            }
            return grid;
        }

        private GridSplitter NewSplitter(bool cols)
        {
            var splitter = new GridSplitter
            {
                Width = cols ? SplitterThickness : double.NaN,
                Height = cols ? double.NaN : SplitterThickness,
                ResizeDirection = cols ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = _border(),
                Cursor = cols ? Cursors.SizeWE : Cursors.SizeNS,
                ToolTip = "ドラッグでリサイズ"
            };
            splitter.MouseEnter += (_, _) => splitter.Background = _accent();
            splitter.MouseLeave += (_, _) => splitter.Background = _border();
            splitter.DragCompleted += (_, _) => { CaptureSizes(); _onChanged(); };
            return splitter;
        }

        private void UpdateFocusBorders()
        {
            var multi = LeafCount > 1;
            foreach (var leaf in Leaves())
            {
                var on = multi && ReferenceEquals(leaf, _focused);
                leaf.Container.BorderThickness = new Thickness(on ? 1 : 0);
                leaf.Container.BorderBrush = on ? _accent() : null;
            }
        }

        // ----- 木の参照（構造操作は ViewportTree） -----

        private IEnumerable<ViewLeaf> Leaves() => ViewportTree.Leaves(_root);

        private ViewLeaf? FindLeafByTab(Guid tabId) => Leaves().FirstOrDefault(l => l.TabId == tabId);
        private ViewLeaf? FindLeafById(Guid id) => Leaves().FirstOrDefault(l => l.Id == id);

        private static void AddTrack(Grid grid, bool columns, GridLength length, double min = 0)
        {
            if (columns)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length, MinWidth = min });
            else
                grid.RowDefinitions.Add(new RowDefinition { Height = length, MinHeight = min });
        }

        private static void SetTrack(UIElement element, bool columns, int index)
        {
            if (columns)
                Grid.SetColumn(element, index);
            else
                Grid.SetRow(element, index);
        }

        private void CaptureSizes(ViewNode? node = null)
        {
            node ??= _root;
            if (node is not ViewSplit split)
                return;
            if (split.Host is { } grid)
            {
                var cols = split.Orientation == SplitKind.Columns;
                foreach (var child in split.Children)
                {
                    var index = child.TrackIndex;
                    if (index < 0)
                        continue;
                    if (cols && index < grid.ColumnDefinitions.Count)
                    {
                        var w = grid.ColumnDefinitions[index].ActualWidth;
                        if (w > 0) child.Weight = w;
                    }
                    else if (!cols && index < grid.RowDefinitions.Count)
                    {
                        var h = grid.RowDefinitions[index].ActualHeight;
                        if (h > 0) child.Weight = h;
                    }
                }
            }
            foreach (var child in split.Children)
                CaptureSizes(child);
        }
}
