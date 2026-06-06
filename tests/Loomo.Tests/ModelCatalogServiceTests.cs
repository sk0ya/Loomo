using System.IO;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.Tests;

public class ModelCatalogServiceTests
{
    [Fact]
    public void ResolvePath_returns_empty_for_blank_model_name()
    {
        var service = new ModelCatalogService(new AiSettings());

        Assert.Equal("", service.ResolvePath("   "));
    }

    [Fact]
    public void ResolvePath_returns_empty_for_missing_model_name()
    {
        var root = CreateTempDir();
        try
        {
            var knownModel = Path.Combine(root, $"known-{Guid.NewGuid():N}");
            Directory.CreateDirectory(knownModel);
            File.WriteAllText(Path.Combine(knownModel, "genai_config.json"), "{}");

            var settings = new AiSettings();
            settings.Local.ModelPath = knownModel;
            var service = new ModelCatalogService(settings);

            var missingName = $"missing-{Guid.NewGuid():N}";

            Assert.Equal("", service.ResolvePath(missingName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolvePath_returns_existing_onnx_model_dir_from_current_model_parent()
    {
        var root = CreateTempDir();
        try
        {
            var modelName = $"model-{Guid.NewGuid():N}";
            var modelDir = Path.Combine(root, modelName);
            Directory.CreateDirectory(modelDir);
            File.WriteAllText(Path.Combine(modelDir, "genai_config.json"), "{}");

            var settings = new AiSettings();
            settings.Local.ModelPath = modelDir;
            var service = new ModelCatalogService(settings);

            Assert.Equal(modelDir, service.ResolvePath(modelName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-model-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
