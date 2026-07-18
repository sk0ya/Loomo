using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

public sealed record ModelChoice(string Name, string Label, bool IsInstalled, DownloadableModel? Download);
public sealed record ModelChoiceResult(
    IReadOnlyList<ModelChoice> Choices, bool SelectedIsInstalled, DownloadableModel? SelectedDownload);

/// <summary>配置済みモデルとダウンロード候補を設定画面の選択肢へ変換する Mapper。</summary>
public sealed class SettingsModelChoiceMapper
{
    public ModelChoiceResult Map(IEnumerable<string> availableModels, string selectedModel)
    {
        var installed = new HashSet<string>(availableModels, StringComparer.OrdinalIgnoreCase);
        var choices = installed.Select(name => new ModelChoice(name, name, true, null)).ToList();
        choices.AddRange(ModelDownloadService.Catalog
            .Where(model => !installed.Contains(model.FolderName))
            .Select(model => new ModelChoice(model.FolderName, $"⬇ {model.DisplayName}", false, model)));
        var selected = choices.FirstOrDefault(choice =>
            string.Equals(choice.Name, selectedModel, StringComparison.OrdinalIgnoreCase));
        return new ModelChoiceResult(choices, selected?.IsInstalled ?? true, selected?.Download);
    }
}
