using System.Windows;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    private async Task RunFileAiAsync(FileAiRequest request)
    {
        if (_vm.AiBar.IsBusy || _vm.AiBar.IsWarmingUp || _fileAiPreparationCts is not null)
        {
            ToastService.Info("AIが処理中のため、完了後にもう一度実行してください。");
            return;
        }

        var title = FileAiSelectionContextBuilder.Title(request.Action);
        var confirm = MessageBox.Show(
            this,
            $"選択した {request.Paths.Count} 件をAIの「{title}」へ渡します。\n"
            + "テキスト内容を現在のAI会話へ追加します。続行しますか？",
            $"AI: {title}", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
            return;

        using var preparationCts = new CancellationTokenSource();
        _fileAiPreparationCts = preparationCts;
        try
        {
            var result = await _fileAiSelection.BuildAsync(request.Action, request.Paths, preparationCts.Token);
            if (preparationCts.IsCancellationRequested || !IsLoaded)
                return;
            foreach (var path in request.Paths)
            {
                if (Directory.Exists(path))
                    _vm.Recent.RecordFolder(path);
                else if (File.Exists(path))
                    _vm.Recent.RecordFile(path);
            }
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
            _vm.AiBar.AskAbout(result.Prompt);
            ToastService.Info(result.Summary);
        }
        catch (OperationCanceledException)
        {
            ToastService.Info("AIへ渡すファイルの準備を中断しました。");
        }
        catch (InvalidOperationException ex)
        {
            ToastService.Warning(ex.Message);
        }
        catch (Exception ex)
        {
            ToastService.Error($"AI用コンテキストの作成に失敗しました: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_fileAiPreparationCts, preparationCts))
                _fileAiPreparationCts = null;
        }
    }

    private void CancelFileAiPreparation() => _fileAiPreparationCts?.Cancel();
}
