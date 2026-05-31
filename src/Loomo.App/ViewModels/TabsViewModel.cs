using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

public enum TabEntryKind
{
    Terminal,
    Editor,
    Browser
}

public sealed partial class TabEntryViewModel : ObservableObject
{
    public TabEntryViewModel(TabEntryKind kind, string title, bool isActive = true)
        : this(Guid.NewGuid(), kind, title, isActive)
    {
    }

    public TabEntryViewModel(Guid id, TabEntryKind kind, string title, bool isActive = true)
    {
        Id = id;
        Kind = kind;
        _title = title;
        _isActive = isActive;
    }

    public Guid Id { get; }
    public TabEntryKind Kind { get; }
    public string IconGlyph => Kind switch
    {
        TabEntryKind.Terminal => "\uE756",
        TabEntryKind.Editor => "\uE70F",
        TabEntryKind.Browser => "\uE774",
        _ => "\uE8A5"
    };

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isActive;
}

/// <summary>Terminal / Editor / Browser のタブ相当情報をサイドバーへ表示する。</summary>
public sealed partial class TabsViewModel : ObservableObject
{
    public ObservableCollection<TabEntryViewModel> TerminalTabs { get; } = new();
    public ObservableCollection<TabEntryViewModel> EditorTabs { get; } = new();
    public ObservableCollection<TabEntryViewModel> BrowserTabs { get; } = new();

    public event EventHandler<TabEntryViewModel>? TabActivated;
    public event EventHandler<TabEntryViewModel>? TabCloseRequested;

    public TabsViewModel()
    {
    }

    public void AddTerminalTab(Guid id, string? title, bool isActive)
    {
        TerminalTabs.Add(new TabEntryViewModel(
            id,
            TabEntryKind.Terminal,
            TerminalTitle(title),
            isActive));
    }

    public void UpdateTerminalTab(Guid id, string? title)
    {
        var tab = TerminalTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;

        tab.Title = TerminalTitle(title);
    }

    public void ActivateTerminalTab(Guid id)
    {
        foreach (var tab in TerminalTabs)
            tab.IsActive = tab.Id == id;
    }

    public void RemoveTerminalTab(Guid id)
    {
        var tab = TerminalTabs.FirstOrDefault(t => t.Id == id);
        if (tab is not null)
            TerminalTabs.Remove(tab);
    }

    public void AddEditorTab(Guid id, string? path, bool isModified, bool isActive)
    {
        var title = string.IsNullOrWhiteSpace(path)
            ? "Untitled"
            : Path.GetFileName(path);
        if (isModified)
            title += " *";

        EditorTabs.Add(new TabEntryViewModel(
            id,
            TabEntryKind.Editor,
            title,
            isActive));
    }

    public void UpdateEditorTab(Guid id, string? path, bool isModified)
    {
        var tab = EditorTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;

        var title = string.IsNullOrWhiteSpace(path)
            ? "Untitled"
            : Path.GetFileName(path);
        if (isModified)
            title += " *";

        tab.Title = title;
    }

    public void ActivateEditorTab(Guid id)
    {
        foreach (var tab in EditorTabs)
            tab.IsActive = tab.Id == id;
    }

    public void RemoveEditorTab(Guid id)
    {
        var tab = EditorTabs.FirstOrDefault(t => t.Id == id);
        if (tab is not null)
            EditorTabs.Remove(tab);
    }

    public void AddBrowserTab(Guid id, string? title, bool isActive)
    {
        BrowserTabs.Add(new TabEntryViewModel(
            id,
            TabEntryKind.Browser,
            BrowserTitle(title),
            isActive));
    }

    public void UpdateBrowserTab(Guid id, string? title)
    {
        var tab = BrowserTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;

        tab.Title = BrowserTitle(title);
    }

    public void ActivateBrowserTab(Guid id)
    {
        foreach (var tab in BrowserTabs)
            tab.IsActive = tab.Id == id;
    }

    public void RemoveBrowserTab(Guid id)
    {
        var tab = BrowserTabs.FirstOrDefault(t => t.Id == id);
        if (tab is not null)
            BrowserTabs.Remove(tab);
    }

    private static string BrowserTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? "Browser" : title.Trim();

    private static string TerminalTitle(string? title)
        => string.IsNullOrWhiteSpace(title) ? "Terminal" : title.Trim();

    [RelayCommand]
    private void ActivateTab(TabEntryViewModel? tab)
    {
        if (tab is not null)
            TabActivated?.Invoke(this, tab);
    }

    [RelayCommand]
    private void CloseTab(TabEntryViewModel? tab)
    {
        if (tab is not null)
            TabCloseRequested?.Invoke(this, tab);
    }
}
