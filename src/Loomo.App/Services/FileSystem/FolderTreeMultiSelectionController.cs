using System;
using System.Collections.Generic;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>フォルダーツリーの複数選択集合と修飾キーによる範囲選択を管理する。</summary>
internal sealed class FolderTreeMultiSelectionController
{
    private readonly List<FileNodeViewModel> _selected = new();
    private FileNodeViewModel? _rangeAnchor;

    public int Count => _selected.Count;

    public bool Contains(FileNodeViewModel node) => _selected.Contains(node);

    public void Clear()
    {
        foreach (var node in _selected)
            node.IsMultiSelected = false;
        _selected.Clear();
    }

    public void Add(FileNodeViewModel node)
    {
        if (_selected.Contains(node))
            return;
        _selected.Add(node);
        node.IsMultiSelected = true;
    }

    public void Toggle(FileNodeViewModel node)
    {
        if (_selected.Remove(node))
        {
            node.IsMultiSelected = false;
            return;
        }
        Add(node);
    }

    public IReadOnlyList<FileNodeViewModel> Resolve(
        FileNodeViewModel? fallback,
        FileNodeViewModel? currentSelection)
    {
        if (_selected.Count > 0)
            return _selected;
        var single = fallback ?? currentSelection;
        return single is null ? Array.Empty<FileNodeViewModel>() : new[] { single };
    }

    public void ApplyModifiers(
        FileNodeViewModel node,
        bool shift,
        bool control,
        FileNodeViewModel? currentSelection,
        IReadOnlyList<FileNodeViewModel> visibleNodes)
    {
        if (shift)
        {
            var anchor = _rangeAnchor ?? currentSelection ?? node;
            var anchorIndex = IndexOf(visibleNodes, anchor);
            var nodeIndex = IndexOf(visibleNodes, node);
            if (anchorIndex >= 0 && nodeIndex >= 0)
            {
                Clear();
                var from = Math.Min(anchorIndex, nodeIndex);
                var to = Math.Max(anchorIndex, nodeIndex);
                for (var i = from; i <= to; i++)
                    Add(visibleNodes[i]);
            }
            return;
        }

        if (control)
        {
            if (_selected.Count == 0 && currentSelection is not null && currentSelection != node)
                Add(currentSelection);
            Toggle(node);
            _rangeAnchor = node;
            return;
        }

        if (_selected.Count > 0)
            Clear();
        _rangeAnchor = node;
    }

    private static int IndexOf(IReadOnlyList<FileNodeViewModel> nodes, FileNodeViewModel target)
    {
        for (var i = 0; i < nodes.Count; i++)
            if (EqualityComparer<FileNodeViewModel>.Default.Equals(nodes[i], target))
                return i;
        return -1;
    }
}
