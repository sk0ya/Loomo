namespace sk0ya.Loomo.Core.Tools;

/// <summary>
/// 唯一のエージェントツール <c>pwsh</c> の契約（ツール名・canonical な引数キー・キー別名）。
/// 文字列の直書きを避け、ツール定義・安全評価・壊れた tool call の復元が同じ語彙を共有する。
/// </summary>
public static class PwshContract
{
    /// <summary>ツール名。</summary>
    public const string ToolName = "pwsh";

    /// <summary>canonical な引数キー（正規化後は必ずこのキーに揃う）。</summary>
    public const string CommandArg = "command";

    /// <summary>
    /// command を表す引数キーの揺れ。小モデルは command を cmd/script/code 等で送ることがあるため
    /// 別名として受理する。先頭が canonical（<see cref="CommandArg"/>）で、優先順位もこの順。
    /// </summary>
    public static readonly string[] CommandKeys =
        { CommandArg, "cmd", "commandline", "command_line", "script", "code", "powershell", "pwsh", "input", "line" };
}
