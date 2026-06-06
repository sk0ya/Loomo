namespace sk0ya.Loomo.Core.Tools;

/// <summary>
/// 構造化ファイル書き込みツール <c>write_file</c> の契約（ツール名・canonical な引数キー・キー別名）。
/// <see cref="PwshContract"/> と同じ流儀で、定義・正規化・実行が同じ語彙を共有する。
/// 内容を <c>content</c> という独立 JSON 引数で受けることで、PowerShell コマンド文字列に
/// 内容を埋め込む場合の「PowerShell 構文＋JSON の二重エスケープ」を避けられる（小モデルの失敗源を断つ）。
/// </summary>
public static class WriteFileContract
{
    /// <summary>ツール名。</summary>
    public const string ToolName = "write_file";

    /// <summary>canonical な引数キー（正規化後は必ずこのキーに揃う）。</summary>
    public const string PathArg = "path";
    public const string ContentArg = "content";

    /// <summary>path を表す引数キーの揺れ。小モデルは file/filename 等で送ることがある。先頭が canonical。</summary>
    public static readonly string[] PathKeys =
        { PathArg, "file", "filename", "file_path", "filepath", "fileName", "filePath" };

    /// <summary>content を表す引数キーの揺れ。先頭が canonical。</summary>
    public static readonly string[] ContentKeys =
        { ContentArg, "text", "body", "data", "contents", "file_content", "value" };
}

/// <summary>
/// 構造化ファイル編集ツール <c>edit_file</c> の契約。既存ファイル中の <c>old_string</c> を
/// <c>new_string</c> へ一意置換する（Claude Code の Edit と同じ「完全一致＋一意」セマンティクス）。
/// 一致が 0／複数なら綺麗なエラーを返すため、小モデルが外しても安全に再試行できる
/// （壊れた <c>-replace</c> でファイルを黙って破損させない）。
/// </summary>
public static class EditFileContract
{
    public const string ToolName = "edit_file";

    public const string PathArg = "path";
    public const string OldArg = "old_string";
    public const string NewArg = "new_string";

    public static readonly string[] PathKeys = WriteFileContract.PathKeys;

    public static readonly string[] OldKeys =
        { OldArg, "old", "old_str", "old_text", "oldText", "search", "find", "from" };

    public static readonly string[] NewKeys =
        { NewArg, "new", "new_str", "new_text", "newText", "replace", "replacement", "to", "with" };
}
