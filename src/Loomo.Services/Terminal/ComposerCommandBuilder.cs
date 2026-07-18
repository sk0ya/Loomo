using System;
using System.IO;
using System.Text;

namespace sk0ya.Loomo.Services.Terminal;

/// <summary>コマンドコンポーザの本文を可視ターミナルへ送る1行コマンドに変換する。</summary>
public static class ComposerCommandBuilder
{
    public const string ScriptFileName = "composer-run.ps1";

    /// <summary>
    /// 単一行はそのまま返す。複数行はスクリプトへ書き出して呼び出しコマンドを返す。
    /// 空文字または空白だけなら null。
    /// </summary>
    public static string? Build(string text, string scriptDirectory)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return null;

        if (!trimmed.Contains('\n'))
            return trimmed;

        Directory.CreateDirectory(scriptDirectory);
        var path = Path.Combine(scriptDirectory, ScriptFileName);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return $"& '{path}'";
    }

    public static string DefaultScriptDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo");
}
