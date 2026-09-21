using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>新規ファイル名と拡張子の結合規則。</summary>
internal static class NewFileNamePolicy
{
    internal static bool TryComposeFileName(string? name, string? extension, out string fileName)
    {
        name = DialogTextPolicy.Trim(name);
        if (DialogTextPolicy.IsMissingRequiredText(name))
        {
            fileName = string.Empty;
            return false;
        }

        fileName = ComposeFileName(name, DialogTextPolicy.Trim(extension));
        return true;
    }

    internal static string ComposeFileName(string name, string extension)
    {
        name = DialogTextPolicy.Trim(name);
        extension = DialogTextPolicy.Trim(extension);

        if (string.IsNullOrEmpty(extension)
            || extension == "（なし）"
            || Path.HasExtension(name)
            || Path.GetFileName(name).StartsWith(".", StringComparison.Ordinal))
            return name;

        if (!extension.StartsWith(".", StringComparison.Ordinal))
            extension = "." + extension;
        return extension.Length == 1 ? name : name + extension;
    }
}
