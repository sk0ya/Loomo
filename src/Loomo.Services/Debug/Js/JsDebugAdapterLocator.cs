using System;
using System.IO;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Services.Debug.Js;

/// <summary>
/// vscode-js-debug（Node.js 用 DAP アダプタ）の導入場所と導入判定。netcoredbg と違い「PATH 上の実行ファイル」
/// ではなく、<b>PATH 上の node</b> ＋ <b>GitHub Releases からダウンロードした dapDebugServer.js</b> の 2 段構え
/// （js-debug は npm 公開されておらず、リリースの tar.gz を展開して使う）。導入先は
/// <c>%APPDATA%/Loomo/debug-adapters/js-debug/</c>（モデルや LSP 設定と同じ Loomo 領域）。
/// </summary>
public static class JsDebugAdapterLocator
{
    /// <summary>展開先ルート。tar.gz 内のトップが <c>js-debug/</c> フォルダなので、実体はこの下の
    /// <c>js-debug/src/dapDebugServer.js</c> になる。</summary>
    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "debug-adapters", "js-debug");

    /// <summary>DAP サーバ本体（<c>node dapDebugServer.js &lt;port&gt;</c> で TCP 待ち受けする）。</summary>
    public static string DapServerPath => Path.Combine(RootDir, "js-debug", "src", "dapDebugServer.js");

    /// <summary>node が PATH 上にあるか（js-debug は node で動かすため必須）。</summary>
    public static bool IsNodeInstalled => ExecutableResolver.IsOnPath("node");

    /// <summary>dapDebugServer.js がダウンロード済みか。</summary>
    public static bool IsServerInstalled => File.Exists(DapServerPath);

    /// <summary>デバッグを開始できる状態か（node ＋ js-debug の両方が揃っている）。</summary>
    public static bool IsInstalled => IsNodeInstalled && IsServerInstalled;

    /// <summary>js-debug を導入する PowerShell コマンド（見えるターミナルで実行する想定）。
    /// GitHub API で最新リリースの <c>js-debug-dap-*.tar.gz</c> を取得し、Windows 標準の tar で展開する。</summary>
    public static string InstallCommand =>
        "$d = Join-Path $env:APPDATA 'Loomo\\debug-adapters\\js-debug'; " +
        "New-Item -ItemType Directory -Force $d | Out-Null; " +
        "$u = ((Invoke-RestMethod 'https://api.github.com/repos/microsoft/vscode-js-debug/releases/latest').assets | " +
        "Where-Object name -like 'js-debug-dap-*.tar.gz' | Select-Object -First 1).browser_download_url; " +
        "Invoke-WebRequest $u -OutFile (Join-Path $d 'js-debug.tar.gz'); " +
        "tar -xzf (Join-Path $d 'js-debug.tar.gz') -C $d; " +
        "Remove-Item (Join-Path $d 'js-debug.tar.gz'); " +
        "Write-Host 'js-debug のインストールが完了しました。'";
}
