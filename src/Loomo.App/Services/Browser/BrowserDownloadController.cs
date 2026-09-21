using System;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>WebView2 のダウンロード進捗を一覧項目へ反映する。</summary>
internal sealed class BrowserDownloadController(BrowserViewModel viewModel, Dispatcher dispatcher)
{
    internal void OnStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var operation = e.DownloadOperation;
        e.Handled = true;
        var item = BrowserDisplayMapper.StartingDownload(operation, e.ResultFilePath);
        viewModel.Downloads.Insert(0, item);
        viewModel.IsDownloadsOpen = true;
        viewModel.NotifyDownloadsChanged();
        void Refresh() => dispatcher.BeginInvoke(new Action(() => Update(item)));
        operation.BytesReceivedChanged += (_, _) => Refresh();
        operation.StateChanged += (_, _) => Refresh();
        Update(item);
    }

    private void Update(BrowserDownloadViewModel item)
    {
        if (item.Operation is not { } operation)
            return;
        var received = (long?)operation.BytesReceived ?? 0;
        var total = (long?)operation.TotalBytesToReceive ?? 0;
        BrowserDisplayMapper.ApplyDownload(item, received, total, operation.State, operation.ResultFilePath);
        viewModel.NotifyDownloadsChanged();
    }
}
