namespace sk0ya.Loomo.App.Services;

/// <summary>クエリモードを検索処理と一覧・状態表示へつなぐ。</summary>
internal sealed class CommandPaletteSearchPresenter(
    PaletteSearchCoordinator search,
    CommandPaletteViewController view,
    TextBlock status,
    Func<bool> isOpen,
    Func<PaletteTarget, Action> jump)
{
    public async Task RefilterAsync(
        string input,
        IReadOnlyList<PaletteCommand> commands,
        Action<PaletteMode> setHint,
        string? activeFilePath,
        Func<string, string> toDisplayPath)
    {
        var query = PaletteQuery.Parse(input);
        setHint(query.Mode);
        search.CancelSearch();
        view.SetNavigation(query.IsNavigation);
        switch (query.Mode)
        {
            case PaletteMode.All:
                await ShowSearchOutcomeAsync(
                    search.SearchAllAsync(query, commands, jump, message => status.Text = message), query);
                break;
            case PaletteMode.Command:
                status.Text = "";
                ShowItems(PaletteFilter.Filter(commands, query.Text), query.Text);
                break;
            case PaletteMode.Line:
                var result = PaletteLineNavigationPolicy.Resolve(
                    activeFilePath, query.LineNumber, toDisplayPath, jump);
                ShowItems(result.Items, "");
                status.Text = result.Status;
                break;
            default:
                await ShowSearchOutcomeAsync(
                    search.SearchAsync(query, jump, message => status.Text = message), query);
                break;
        }
    }

    private async Task ShowSearchOutcomeAsync(Task<PaletteSearchOutcome?> pending, PaletteQuery query)
    {
        var outcome = await pending;
        if (outcome is null || !isOpen())
            return;
        ShowItems(outcome.Items, query.Text);
        status.Text = outcome.Status;
    }

    private void ShowItems(IReadOnlyList<PaletteCommand> items, string query)
    {
        var hasNoResults = items.Count == 0;
        if (hasNoResults)
            search.CancelPreview();

        view.ShowItems(items, query);
        if (hasNoResults)
            view.ShowPreviewMessage("", "候補がありません");
    }
}
