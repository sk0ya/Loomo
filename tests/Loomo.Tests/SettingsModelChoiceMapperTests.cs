using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class SettingsModelChoiceMapperTests
{
    [Fact]
    public void Map_does_not_duplicate_installed_catalog_model()
    {
        var mapper = new SettingsModelChoiceMapper();
        var installed = sk0ya.Loomo.Ai.ModelDownloadService.Default.FolderName;

        var result = mapper.Map(new[] { installed }, installed);

        Assert.Single(result.Choices, choice => choice.Name == installed);
        Assert.True(result.SelectedIsInstalled);
        Assert.Null(result.SelectedDownload);
    }
}
