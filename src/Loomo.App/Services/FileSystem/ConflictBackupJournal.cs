using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>上書き貼り付けで退避した実体（<c>.loomo-conflict-&lt;GUID&gt;-&lt;元の名前&gt;</c>）の在り処を控える台帳。
///
/// <para>退避先は Undo で元へ戻すために残す必要があるが、その寿命は
/// <see cref="FileOperationHistory"/>（＝プロセス）と同じ。履歴が捨てられれば消せるので、通常は
/// <see cref="FileOperationHistory.Clear"/>／履歴あふれの経路で消える。<b>問題はそこを通らない終わり方</b>
/// ——クラッシュや強制終了だと、隠し属性の退避先がユーザーのフォルダーに残り続ける。</para>
///
/// <para>そこで、退避を作った時点でフルパスを1行ずつ書き足し、消せたら消す。次回起動時に
/// <see cref="Sweep"/> が残骸（＝前回の生存者）を片付ける。行の中身を信用しすぎないよう、
/// 削除するのはファイル名が <see cref="Prefix"/> で始まるものだけに限る。</para></summary>
internal static class ConflictBackupJournal
{
    /// <summary>退避先の名前の頭。これで始まる項目だけを台帳から削除する。</summary>
    internal const string Prefix = ".loomo-conflict-";

    private static readonly object Gate = new();
    private static string _path = DefaultPath();

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "pending-conflicts.txt");

    /// <summary>台帳の場所を差し替える（テスト用。null で既定＝%APPDATA%/Loomo へ戻す）。</summary>
    internal static void UseFile(string? path)
    {
        lock (Gate) _path = path ?? DefaultPath();
    }

    /// <summary>退避先を1件控える。</summary>
    public static void Record(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath)) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, backupPath + Environment.NewLine);
            }
            catch { /* 台帳が書けなくてもファイル操作自体は成立させる */ }
        }
    }

    /// <summary>退避先を消せたので台帳から落とす。</summary>
    public static void Forget(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath)) return;
        lock (Gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var kept = File.ReadAllLines(_path)
                    .Where(line => line.Length > 0
                        && !string.Equals(line.Trim(), backupPath, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (kept.Length == 0) File.Delete(_path);
                else File.WriteAllLines(_path, kept);
            }
            catch { /* 次回起動の Sweep が拾う */ }
        }
    }

    /// <summary>前回のプロセスが残した退避先を片付ける（起動時に一度）。</summary>
    public static void Sweep()
    {
        lock (Gate)
        {
            string[] lines;
            try
            {
                if (!File.Exists(_path)) return;
                lines = File.ReadAllLines(_path);
            }
            catch { return; }

            var remaining = new List<string>();
            foreach (var line in lines)
            {
                var path = line.Trim();
                if (path.Length == 0) continue;
                // 台帳は自分で書いたものだが、行の中身をそのまま削除対象にはしない。
                if (!Path.GetFileName(path).StartsWith(Prefix, StringComparison.Ordinal)) continue;
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch { remaining.Add(path); /* ロック中なら次回に回す */ }
            }

            try
            {
                if (remaining.Count == 0) File.Delete(_path);
                else File.WriteAllLines(_path, remaining);
            }
            catch { }
        }
    }
}
