using System;
using System.IO;
using System.Text;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// コマンドコンポーザ（設計書 §23.2）の本文を、可視ターミナルへ送る1行コマンドに変換する。
/// <para>
/// 単一行はそのまま送る。複数行は <b>常に一時 .ps1 に書き出して <c>&amp; '…'</c> で実行</b>する —
/// 改行を <c>;</c> で繋ぐ案は、行コメント（<c>#</c> 以降が全部コメント化）・行末パイプ/継続
/// （<c>|</c> や <c>`</c> で終わる行の直後に <c>;</c> を挟むと構文エラー）・here-string を壊すため
/// 採用しない（設計 §23.2 の「数行なら ; 結合」から正確さ優先で変更）。
/// </para>
/// </summary>
internal static class ComposerCommandBuilder
{
    internal const string ScriptFileName = "composer-run.ps1";

    /// <summary>
    /// 送信用の1行コマンドを返す。複数行のときは <paramref name="scriptDirectory"/> に
    /// スクリプトを書き出す（毎回上書き。送信はターミナル側で直列化される）。
    /// 空文字・空白のみは null（送るものがない）。
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
        // BOM 付き UTF-8：pwsh が日本語コメント等を確実に正しく読むため。
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return $"& '{path}'";
    }

    public static string DefaultScriptDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo");
}
