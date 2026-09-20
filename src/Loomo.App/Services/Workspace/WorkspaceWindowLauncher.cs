using System.Diagnostics;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークスペースを「別ウィンドウ」で開く。
///
/// 別ウィンドウ＝<b>別プロセス</b>。同じプロセスに 2 つ目の <c>ShellWindow</c> を出す手は取らない——
/// ワークスペース（<c>IWorkspaceService</c>）・LSP のクライアントプール・レイアウト保存はいずれも
/// プロセスに 1 つの singleton で、2 つの部屋が同じものを奪い合うことになる。新しいプロセスを
/// <c>--workspace</c> 付きで起動すれば、タスクバーの「最近使った項目」（<see cref="TaskbarWorkspaceRecentService"/>）
/// から開くのと<em>まったく同じ経路</em>で、独立した部屋がもう 1 つ立ち上がる。</summary>
internal static class WorkspaceWindowLauncher
{
    /// <summary>起動する自分自身のパス（テストから差し替えられるよう引数に取る）。</summary>
    public static ProcessStartInfo BuildStartInfo(string applicationPath, string folder)
        => new(applicationPath)
        {
            Arguments = StartupArguments.FormatWorkspaceArgument(folder),
            WorkingDirectory = folder,
            // シェル経由にすると引数の再解釈が挟まる。自分自身を起動するだけなので直接起こす。
            UseShellExecute = false
        };

    /// <summary>失敗（実行ファイルが辿れない・起動できない）は<b>呼び出し側へ投げる</b>。
    /// ポップアップの中に理由を出したいので、ここでは握り潰さない。</summary>
    public static void Launch(string folder)
    {
        var applicationPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(applicationPath))
            throw new InvalidOperationException("Loomo の実行ファイルが特定できません。");

        Process.Start(BuildStartInfo(applicationPath, folder));
    }
}
