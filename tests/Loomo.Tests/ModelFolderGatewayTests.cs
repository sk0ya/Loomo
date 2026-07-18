using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class ModelFolderGatewayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("loomo-model-folder-").FullName;
    private readonly ModelFolderGateway _sut = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Resolve_returns_folder_for_onnx_model()
    {
        File.WriteAllText(Path.Combine(_root, "genai_config.json"), "{}");

        var result = Assert.IsType<ModelFolderSelection>(_sut.Resolve(_root));

        Assert.Equal(_root, result.ModelPath);
        Assert.Equal(Path.GetFileName(_root), result.Name);
    }

    [Fact]
    public void Resolve_returns_first_gguf_file_in_name_order()
    {
        File.WriteAllText(Path.Combine(_root, "z.gguf"), "");
        File.WriteAllText(Path.Combine(_root, "a.gguf"), "");

        var result = Assert.IsType<ModelFolderSelection>(_sut.Resolve(_root));

        Assert.Equal(Path.Combine(_root, "a.gguf"), result.ModelPath);
    }

    [Fact]
    public void Resolve_rejects_non_model_folder()
    {
        Assert.Null(_sut.Resolve(_root));
    }
}
