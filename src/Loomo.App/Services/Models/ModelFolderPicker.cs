using Microsoft.Win32;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>モデルフォルダー選択ダイアログと選択結果の検証をまとめる UI Gateway。</summary>
public sealed class ModelFolderPicker
{
    private readonly ModelFolderGateway _folders;

    public ModelFolderPicker(ModelFolderGateway folders) => _folders = folders;

    public ModelFolderSelection? Pick(string currentModelPath)
    {
        var initial = _folders.GetExistingDirectory(currentModelPath) ?? ModelDownloadService.DefaultModelsRoot;
        var dialog = new OpenFolderDialog
        {
            Title = "モデルフォルダを選択（ONNX: genai_config.json／GGUF: *.gguf を含むフォルダ）",
            InitialDirectory = _folders.GetExistingDirectory(initial),
        };
        return dialog.ShowDialog() == true ? _folders.Resolve(dialog.FolderName) : null;
    }
}
