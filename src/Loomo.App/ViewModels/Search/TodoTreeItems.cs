using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>検索位置は原文のまま保持し、一覧の本文だけを読みやすくする。</summary>
public sealed partial class TodoEntry : ObservableObject
{
    public TodoEntry(string tag, ContentSearchHit hit) { Tag = tag; Hit = hit; }
    public string Tag { get; }
    public ContentSearchHit Hit { get; }
    public string Key => $"{Hit.FullPath}\0{Hit.Line}\0{Hit.Column}\0{Tag}";
    public string Label => Body;
    public string Body
    {
        get
        {
            var start = Math.Clamp(Hit.Column - 1 + Tag.Length, 0, Hit.LineText.Length);
            var body = Hit.LineText[start..].TrimStart(' ', '\t', ':', '：', '-');
            // タグの後ろだけを表示する。原文やコピー内容には手を加えない。
            var next = Regex.Match(body, TodoTreeViewModel.TagPattern);
            if (next.Success) body = body[..next.Index].TrimEnd();
            foreach (var suffix in new[] { "-->", "*/" })
                if (body.EndsWith(suffix, StringComparison.Ordinal)) body = body[..^suffix.Length].TrimEnd();
            return string.IsNullOrWhiteSpace(body) ? "（内容なし）" : body;
        }
    }
    public string Location => $"{Hit.RelativePath}:{Hit.Line}";
    public string ToolTip => $"{Hit.FullPath}:{Hit.Line}:{Hit.Column}\n{Hit.LineText.Trim()}";
    public bool IsExpanded { get; set; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class TodoGroup : ObservableObject
{
    public TodoGroup(string name, IReadOnlyList<TodoEntry> entries, bool byTag = false, string? fullPath = null)
    { Name = name; ByTag = byTag; FullPath = fullPath ?? name; Entries = new(entries); }
    public string Name { get; }
    public bool ByTag { get; }
    public string FullPath { get; }
    public string Key => ByTag ? $"tag:{Name}" : $"file:{FullPath}";
    public string Title => ByTag ? Name : Path.GetFileName(Name);
    public string Directory => ByTag ? "" : Path.GetDirectoryName(Name) ?? "";
    public ObservableCollection<TodoEntry> Entries { get; }
    public int Count => Entries.Count;
    public string Label => $"{Name} ({Count})";
    [ObservableProperty] private bool _isSelected;
    public void NotifyCount() { OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(Label)); }
    [ObservableProperty] private bool _isExpanded = true;
}

/// <summary>TODO が見つかったフォルダー。件数とコピー対象は配下すべての一致行を含む。</summary>
public sealed partial class TodoFolder : ObservableObject
{
    public TodoFolder(string name, string fullPath) { Name = name; FullPath = fullPath; }
    public string Name { get; }
    public string FullPath { get; }
    public string Key => $"folder:{FullPath}";
    public ObservableCollection<object> Children { get; } = [];
    public IEnumerable<TodoEntry> Entries => TodoTreeLayout.Entries(Children);
    public int Count => Entries.Count();
    public void NotifyCount() => OnPropertyChanged(nameof(Count));
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class TodoTagFilter : ObservableObject
{
    public TodoTagFilter(string tag) => Tag = tag;
    public string Tag { get; }
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private int _count;
}
