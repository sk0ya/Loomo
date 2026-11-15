using System;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// Claude Code に登録する橋（<c>sk0ya.Loomo.McpBridge.exe</c>、§43.6 D）を、本体の出力から
/// <c>%LOCALAPPDATA%/Loomo/mcp-bridge/</c> へ写す。橋は claude のセッションが続くあいだ動き続けるので、本体の bin から
/// 起こすと exe を掴まれて以後のビルドが落ちる（常駐ホスト＝§34 と同じ事情）。登録する場所を変えないために、
/// 写す先は版ごとに分けない——掴まれているファイルは<b>脇へ名前を変えて</b>から新しいものを置く（動いている exe も
/// 名前は変えられる。古い橋はそのセッションが終わるまで古いまま動き、脇へどけたものは次の起動で消す）。
/// </summary>
public static class LoomoMcpBridgeInstaller
{
    public const string ExeName = "sk0ya.Loomo.McpBridge.exe";
    private const string AsideMark = ".old-";

    /// <summary>本体の出力のうち、橋が置かれているフォルダー（Loomo.App.csproj の CopyMcpBridge）。</summary>
    public static string SourceDirectory => Path.Combine(AppContext.BaseDirectory, "mcpbridge");

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loomo", "mcp-bridge");

    public static string InstalledExe => Path.Combine(InstallDirectory, ExeName);

    /// <summary>
    /// Claude Code への登録（ユーザーが自分で 1 回だけ打つ）。前の定義（環境変数を参照する http の定義）が残っていれば
    /// 置き換える。定義に入るのは橋の場所だけで、ポートもトークンも書かれない。PowerShell 用。
    /// </summary>
    public static string RegistrationCommand(string exePath)
        => $"claude mcp remove --scope user {LoomoMcpServer.ServerName} 2>$null; "
           + $"claude mcp add --scope user {LoomoMcpServer.ServerName} -- '{exePath.Replace("'", "''")}'";

    /// <summary>橋を写す（中身が同じファイルは触らない）。写した先に exe があれば true。</summary>
    public static bool Install(string? sourceDirectory = null, string? installDirectory = null)
    {
        var source = sourceDirectory ?? SourceDirectory;
        var target = installDirectory ?? InstallDirectory;
        try
        {
            if (Directory.Exists(source))
            {
                Directory.CreateDirectory(target);
                foreach (var aside in Directory.EnumerateFiles(target, $"*{AsideMark}*"))
                {
                    try { File.Delete(aside); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* まだ動いている橋。次の起動で */ }
                }
                foreach (var file in Directory.EnumerateFiles(source))
                    Place(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写せなくても、前に写したものがあればそれで動く。
        }
        return File.Exists(Path.Combine(target, ExeName));
    }

    private static void Place(string from, string to)
    {
        if (File.Exists(to) && File.ReadAllBytes(from).AsSpan().SequenceEqual(File.ReadAllBytes(to)))
            return;
        try
        {
            File.Copy(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 動いている橋が掴んでいる。脇へどけて置き直す。
            File.Move(to, to + AsideMark + Guid.NewGuid().ToString("N")[..8]);
            File.Copy(from, to);
        }
    }
}
