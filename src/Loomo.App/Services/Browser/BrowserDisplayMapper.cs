using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>WebView2 の値をブラウザペインの表示用状態へ写す。</summary>
internal static class BrowserDisplayMapper
{
    /// <summary>現在のタブから戻る/進む・読み込み・ズーム状態をツールバーへ反映する。</summary>
    internal static void ApplyToolbar(
        BrowserViewModel viewModel, CoreWebView2? core, bool isLoading, double zoomFactor)
    {
        viewModel.CanGoBack = core?.CanGoBack == true;
        viewModel.CanGoForward = core?.CanGoForward == true;
        viewModel.IsLoading = isLoading;
        viewModel.ZoomPercent = BrowserZoomPolicy.Percent(zoomFactor);
    }

    internal static BrowserDownloadViewModel StartingDownload(
        CoreWebView2DownloadOperation operation, string resultFilePath)
        => new()
        {
            Url = operation.Uri,
            FileName = Path.GetFileName(resultFilePath),
            FilePath = resultFilePath,
            StatusText = "受信中…",
            Operation = operation,
        };

    internal static void ApplyDownload(
        BrowserDownloadViewModel item, long receivedBytes, long totalBytes,
        CoreWebView2DownloadState state, string? resultFilePath)
    {
        var display = Download(receivedBytes, totalBytes, state, resultFilePath);
        item.IsIndeterminate = display.IsIndeterminate;
        item.Progress = display.Progress;
        item.IsActive = display.IsActive;
        item.IsCompleted = display.IsCompleted;
        item.StatusText = display.StatusText;
        if (display.CompletedFilePath is { } filePath)
            item.FilePath = filePath;
        if (display.CompletedFileName is { } fileName)
            item.FileName = fileName;
    }

    /// <summary>現在URLが未確定の間だけ、実体化待ちタブの遷移先を表示する。</summary>
    public static string? CurrentUrl(string? currentUrl, string? pendingUrl)
        => currentUrl ?? (string.IsNullOrEmpty(pendingUrl) ? null : pendingUrl);

    public static BrowserDownloadPresentation Download(
        long receivedBytes,
        long totalBytes,
        CoreWebView2DownloadState state,
        string? resultFilePath)
    {
        var isCompleted = state == CoreWebView2DownloadState.Completed;
        var isInterrupted = state == CoreWebView2DownloadState.Interrupted;
        var progress = totalBytes > 0
            ? Math.Clamp(receivedBytes * 100.0 / totalBytes, 0, 100)
            : 0;
        var status = isCompleted
            ? $"完了 · {FormatBytes(receivedBytes)}"
            : isInterrupted
                ? "中断"
                : totalBytes > 0
                    ? $"{FormatBytes(receivedBytes)} / {FormatBytes(totalBytes)}"
                    : FormatBytes(receivedBytes);

        return new(
            IsIndeterminate: totalBytes <= 0,
            Progress: isCompleted ? 100 : progress,
            IsActive: !isCompleted && !isInterrupted,
            IsCompleted: isCompleted,
            StatusText: status,
            CompletedFilePath: isCompleted ? resultFilePath : null,
            CompletedFileName: isCompleted && resultFilePath is not null
                ? Path.GetFileName(resultFilePath)
                : null);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}

internal sealed record BrowserDownloadPresentation(
    bool IsIndeterminate,
    double Progress,
    bool IsActive,
    bool IsCompleted,
    string StatusText,
    string? CompletedFilePath,
    string? CompletedFileName);
