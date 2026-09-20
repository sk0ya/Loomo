namespace sk0ya.Loomo.App.Services;

public sealed record ModelFolderSelection(string Folder, string Name, string ModelPath);

/// <summary>ローカル AI モデルフォルダーの検出を担当する。</summary>
public sealed class ModelFolderGateway
{
    public string? GetExistingDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;

    public ModelFolderSelection? Resolve(string folder)
    {
        string? modelPath = null;
        if (File.Exists(Path.Combine(folder, "genai_config.json")))
            modelPath = folder;
        else
            modelPath = Directory.EnumerateFiles(folder, "*.gguf")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        if (modelPath is null) return null;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        return new ModelFolderSelection(folder, name, modelPath);
    }
}
