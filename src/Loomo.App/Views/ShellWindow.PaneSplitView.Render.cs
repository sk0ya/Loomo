using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using sk0ya.Loomo.App.Layout;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow.PaneSplitView のレンダリングとツリー操作（Grid 組み立て・分割線・フォーカス枠、
/// ノードの挿入/除去/正規化・サイズ取り込み）。状態操作（分割/活性化/フォーカス移動）は ShellWindow.PaneSplitView.cs。</summary>
public partial class ShellWindow
{
    private sealed partial class PaneSplitView
    {
        // ----- レンダリング -----

        public void Rebuild()
        {
            if (_root is null)
            {
                _host.Children.Clear();
                return;
            }
            _root = Normalize(_root);

            // 既存の親から全コントロール・全コンテナを外してから組み直す。
            foreach (var el in _allControls())
                Detach(el);
            foreach (var leaf in Leaves())
            {
                leaf.Container.Child = null;
                Detach(leaf.Container);
            }
            Detach(_parking);
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
                    Detach(control);
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
                Detach(el);
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
                    ShellWindow.AddTrack(grid, cols, new GridLength(ShellWindow.SplitterThickness));
                    var splitter = NewSplitter(cols);
                    ShellWindow.SetTrack(splitter, cols, i * 2 - 1);
                    grid.Children.Add(splitter);
                }
                var child = split.Children[i];
                ShellWindow.AddTrack(grid, cols, new GridLength(child.Weight <= 0 ? 1 : child.Weight, GridUnitType.Star), min);
                child.TrackIndex = i * 2;
                var visual = Build(child);
                ShellWindow.SetTrack(visual, cols, i * 2);
                grid.Children.Add(visual);
            }
            return grid;
        }

        private GridSplitter NewSplitter(bool cols)
        {
            var splitter = new GridSplitter
            {
                Width = cols ? ShellWindow.SplitterThickness : double.NaN,
                Height = cols ? double.NaN : ShellWindow.SplitterThickness,
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

        // ----- ツリー操作 -----

        internal static void Detach(FrameworkElement el)
        {
            switch (el.Parent)
            {
                case Panel p: p.Children.Remove(el); break;
                case Decorator d: d.Child = null; break;
                case ContentControl c: c.Content = null; break;
            }
        }

        private IEnumerable<ViewLeaf> Leaves(ViewNode? node = null)
        {
            node ??= _root;
            if (node is ViewLeaf leaf)
                yield return leaf;
            else if (node is ViewSplit split)
                foreach (var child in split.Children)
                    foreach (var l in Leaves(child))
                        yield return l;
        }

        private ViewLeaf? FindLeafByTab(Guid tabId) => Leaves().FirstOrDefault(l => l.TabId == tabId);
        private ViewLeaf? FindLeafById(Guid id) => Leaves().FirstOrDefault(l => l.Id == id);

        private ViewSplit? FindParent(ViewNode target, ViewNode? current = null)
        {
            current ??= _root;
            if (current is not ViewSplit split)
                return null;
            if (split.Children.Contains(target))
                return split;
            foreach (var child in split.Children)
                if (FindParent(target, child) is { } found)
                    return found;
            return null;
        }

        private void Insert(ViewNode target, ViewNode node, SplitKind orientation)
        {
            var parent = FindParent(target);
            if (parent is not null && parent.Orientation == orientation)
            {
                parent.Children.Insert(parent.Children.IndexOf(target) + 1, node);
                return;
            }

            var split = new ViewSplit { Orientation = orientation, Weight = target.Weight };
            target.Weight = 1;
            node.Weight = 1;
            split.Children.Add(target);
            split.Children.Add(node);
            if (parent is null)
                _root = split;
            else
                parent.Children[parent.Children.IndexOf(target)] = split;
        }

        private void Remove(ViewNode node)
        {
            var parent = FindParent(node);
            if (parent is null)
                _root = null;
            else
                parent.Children.Remove(node);
        }

        private ViewNode? Normalize(ViewNode? node)
        {
            if (node is not ViewSplit split)
                return node;

            var kids = new List<ViewNode>();
            foreach (var child in split.Children)
            {
                var n = Normalize(child);
                if (n is null)
                    continue;
                if (n is ViewSplit inner && inner.Orientation == split.Orientation)
                {
                    var total = inner.Children.Sum(c => c.Weight > 0 ? c.Weight : 1);
                    var scale = total > 0 ? (inner.Weight > 0 ? inner.Weight : 1) / total : 1;
                    foreach (var c in inner.Children)
                    {
                        c.Weight = (c.Weight > 0 ? c.Weight : 1) * scale;
                        kids.Add(c);
                    }
                }
                else
                {
                    kids.Add(n);
                }
            }

            if (kids.Count == 0)
                return null;
            if (kids.Count == 1)
            {
                kids[0].Weight = split.Weight;
                return kids[0];
            }
            split.Children.Clear();
            split.Children.AddRange(kids);
            return split;
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
}

