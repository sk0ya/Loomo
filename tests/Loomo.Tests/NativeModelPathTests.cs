using System.IO;
using sk0ya.Loomo.Ai.Clients;

namespace sk0ya.Loomo.Tests;

public sealed class NativeModelPathTests
{
    [Fact]
    public void Create_UnicodeModelDirectory_ProvidesAccessibleAsciiPath()
    {
        var root = CreateTempDirectory();
        var modelDirectory = Path.Combine(root, "日本語モデル");
        Directory.CreateDirectory(modelDirectory);
        File.WriteAllText(Path.Combine(modelDirectory, "genai_config.json"), "{}");

        try
        {
            using var nativePath = NativeModelPath.Create(modelDirectory);

            Assert.All(nativePath.Path, c => Assert.InRange((int)c, 0, 0x7f));
            Assert.True(Directory.Exists(nativePath.Path));
            Assert.True(File.Exists(Path.Combine(nativePath.Path, "genai_config.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Create_UnicodeGgufFileName_UsesTemporaryAsciiHardLink()
    {
        var root = CreateTempDirectory();
        var modelDirectory = Path.Combine(root, "モデル置き場");
        Directory.CreateDirectory(modelDirectory);
        var modelPath = Path.Combine(modelDirectory, "日本語モデル.gguf");
        File.WriteAllBytes(modelPath, [1, 2, 3]);

        try
        {
            using (var nativePath = NativeModelPath.Create(modelPath))
            {
                Assert.All(nativePath.Path, c => Assert.InRange((int)c, 0, 0x7f));
                Assert.Equal([1, 2, 3], File.ReadAllBytes(nativePath.Path));
            }

            Assert.True(File.Exists(modelPath));
            Assert.Empty(Directory.EnumerateFiles(modelDirectory, ".loomo-model-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LoomoNativeModelPath-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
