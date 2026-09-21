namespace sk0ya.Loomo.App.Services;

/// <summary>C# 型生成・抽出ダイアログで使う既定名と出力先パスを組み立てる。</summary>
internal static class CSharpGenerationInputPolicy
{
    public static string DefaultInterfaceName(string className)
        => className.Length == 0 ? "IContract" : "I" + className;

    public static string DefaultTypeFilePath(string sourcePath, string typeName)
        => Path.Combine(SourceDirectory(sourcePath), typeName + ".cs");

    public static string DefaultMovedTypeFilePath(string sourcePath, string selectedName)
        => DefaultTypeFilePath(sourcePath, selectedName.Length == 0 ? "MovedType" : selectedName);

    public static string ResolveDestinationPath(string sourcePath, string destination)
    {
        var path = destination;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(SourceDirectory(sourcePath), path);
        return Path.GetFullPath(path.Trim());
    }

    private static string SourceDirectory(string sourcePath)
        => Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
}
