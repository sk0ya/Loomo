namespace sk0ya.Loomo.App.Views;

/// <summary>EditorSupport の追従元、ピン留め、ファイル履歴を所有する機能コントローラー。</summary>
internal sealed class EditorSupportController
{
    public EditorTab? Source { get; private set; }
    public bool IsPinned { get; set; }
    public bool IsNavigating { get; set; }
    public EditorSupportHistory History { get; } = new();

    public bool TryChangeSource(EditorTab source, bool force, out EditorTab? previous)
    {
        previous = Source;
        if (ReferenceEquals(Source, source))
            return false;
        if (IsPinned && !force && Source is not null)
            return false;
        Source = source;
        if (!IsNavigating)
            History.Navigate(source.PeekFilePath);
        return true;
    }

    public EditorTab? DetachSource()
    {
        var previous = Source;
        Source = null;
        return previous;
    }

    public void Reset()
    {
        Source = null;
        IsPinned = false;
        IsNavigating = false;
    }
}
