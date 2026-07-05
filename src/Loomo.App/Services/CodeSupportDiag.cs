using System;
using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// EditorSupport の「コード（LSP アウトライン＋②呼び出し解析）」の描画レイテンシを数値で追うための
/// 軽量診断ロガー。環境変数 <c>LOOMO_CODE_SUPPORT_DIAG=1</c> のときだけ有効で、無効時は完全に no-op
/// （文字列組み立ても行わない ─ 呼び出し側は <see cref="IsEnabled"/> でガードする）。
/// 出力先は <c>%TEMP%\loomo-code-support.log</c>（1 行 1 イベント追記）。
/// 「ファイル選択 → 結果表示までが遅いときがある」の切り分け用：
/// LSP ready 待ち / documentSymbols / callHierarchy+references のどこで時間を食っているかを可視化する。
/// </summary>
internal static class CodeSupportDiag
{
    /// <summary>診断が有効か（環境変数 <c>LOOMO_CODE_SUPPORT_DIAG=1</c>）。無効なら計測コードごと飛ばす。</summary>
    public static readonly bool IsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LOOMO_CODE_SUPPORT_DIAG"), "1", StringComparison.Ordinal);

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "loomo-code-support.log");
    private static readonly object Gate = new();

    /// <summary>1 行追記する（有効時のみ）。I/O 失敗は握り潰す（診断で本体を落とさない）。</summary>
    public static void Log(string message)
    {
        if (!IsEnabled)
            return;
        try
        {
            lock (Gate)
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 診断ログの失敗は無視（本処理には影響させない）。
        }
    }
}
