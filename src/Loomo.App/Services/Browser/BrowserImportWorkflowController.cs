using System.Runtime.Versioning;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>取り込み画面の busy/status と、読込から各保管先への反映までをまとめて進める。</summary>
[SupportedOSPlatform("windows")]
internal sealed class BrowserImportWorkflowController(
    BrowserViewModel browser,
    Func<IReadOnlyList<ImportedCookie>, Task<int>> applyCookies,
    Action<string> showSuccess)
{
    private BrowserImportViewModel Import => browser.Import;

    /// <summary>プロファイル一覧をバックグラウンドで検出し、前回選択と空状態文を更新する。</summary>
    public async Task RefreshSourcesAsync()
    {
        Import.IsBusy = true;
        try
        {
            var sources = await Task.Run(BrowserImportService.DetectSources);
            Import.SetSources(sources, BrowserImportService.SourcesStatus(sources.Count));
        }
        finally
        {
            Import.IsBusy = false;
        }
    }

    /// <summary>読み込み、browser.jsonへの併合、Cookie反映、次回起動用パスワード予約を順に行う。</summary>
    public async Task RunAsync(ChromiumProfileRef profile, BrowserImportSelection selection)
    {
        Import.IsBusy = true;
        Import.Status = "取り込んでいます…";
        try
        {
            var harvest = await Task.Run(() => BrowserImportService.Harvest(profile, selection));

            var (bookmarks, history) = browser.MergeImported(harvest.Bookmarks, harvest.History);
            var appliedCookies = harvest.Cookies.Count > 0
                ? await applyCookies(harvest.Cookies)
                : 0;
            var queuedPasswords = BrowserImportService.QueuePasswords(selection, harvest);
            var result = BrowserImportService.Summarize(
                selection, harvest, bookmarks, history, appliedCookies, queuedPasswords);
            Import.Status = result.Status;
            if (result.ImportedAnything)
                showSuccess($"{profile.Label} から取り込みました。");
        }
        catch (Exception ex)
        {
            Import.Status = $"取り込めませんでした: {ex.Message}";
        }
        finally
        {
            Import.IsBusy = false;
        }
    }

    /// <summary>CSVをバックグラウンドで読み、パスワードを次回起動用に予約して結果を表示する。</summary>
    public async Task ImportPasswordsFromCsvAsync(string path)
    {
        Import.IsBusy = true;
        try
        {
            var read = await Task.Run(() => ChromePasswordCsv.Read(path));
            if (read.Error is { } error)
            {
                Import.Status = error;
                return;
            }
            var queued = BrowserImportService.QueuePasswords(read.Items);
            var presentation = BrowserImportService.SummarizeCsvQueue(queued, read.Blocked);
            Import.Status = presentation.Status;
            if (presentation.SuccessMessage is { } success)
                showSuccess(success);
        }
        finally
        {
            Import.IsBusy = false;
        }
    }
}
