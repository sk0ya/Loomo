using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

public enum SearchScope { Text, FileName, Terminal }

public readonly record struct TerminalSearchHit(int LineIndex, int Column, int Length, string LineText);

public sealed class SearchMatchItem
{
    public SearchMatchItem(ContentSearchHit hit)
    {
        FullPath = hit.FullPath;
        RelativePath = hit.RelativePath;
        Line = hit.Line;
        Column = hit.Column;
        LineText = hit.LineText.TrimEnd();
    }

    private SearchMatchItem(TerminalSearchHit hit)
    {
        IsTerminal = true;
        TerminalHit = hit;
        FullPath = "";
        RelativePath = "";
        Line = hit.LineIndex + 1;
        Column = hit.Column;
        LineText = hit.LineText.TrimEnd();
    }

    public static SearchMatchItem ForTerminal(TerminalSearchHit hit) => new(hit);
    public bool IsTerminal { get; }
    public TerminalSearchHit TerminalHit { get; }
    public string FullPath { get; }
    public string RelativePath { get; }
    public int Line { get; }
    public int Column { get; }
    public string LineText { get; }
    public string Preview => LineText.TrimStart();
    public bool IsExpanded { get; set; }
}

public sealed partial class SearchFileGroup : ObservableObject
{
    public SearchFileGroup(string fullPath, string relativePath, IEnumerable<SearchMatchItem> matches)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Matches = new ObservableCollection<SearchMatchItem>(matches);
    }

    [ObservableProperty] private bool _isExpanded = true;
    public string FullPath { get; }
    public string RelativePath { get; }
    public ObservableCollection<SearchMatchItem> Matches { get; }
    public int Count => Matches.Count;
    public string FileName => Segment(afterSlash: true);
    public string FolderPath => Segment(afterSlash: false);
    public bool ShowCount => Count > 0;

    private string Segment(bool afterSlash)
    {
        var index = RelativePath.LastIndexOf('/');
        if (index < 0) return afterSlash ? RelativePath : "";
        return afterSlash ? RelativePath[(index + 1)..] : RelativePath[..index];
    }
}

public sealed partial class SearchFolderNode : ObservableObject
{
    public SearchFolderNode(string name, string relativePath)
    {
        Name = name;
        RelativePath = relativePath;
    }

    [ObservableProperty] private bool _isExpanded = true;
    public string Name { get; private set; }
    public string RelativePath { get; }
    public ObservableCollection<object> Children { get; } = new();
    public int Count => Children.Sum(child => child switch
    {
        SearchFolderNode folder => folder.Count,
        SearchFileGroup group => group.Count,
        _ => 0,
    });
    public bool ShowCount => Count > 0;

    internal void PrependName(string parentSegment)
    {
        if (!string.IsNullOrEmpty(parentSegment)) Name = parentSegment + "/" + Name;
    }
}

public readonly record struct SearchHit(string FullPath, int Line, int Column, string Highlight = "");
