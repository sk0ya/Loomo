using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Core.Pty;

/// <summary>
/// Loomo が全部終わったとき（常駐ホストが全セッションを殺すとき）に残す、タブの画面（§34.13）。
/// <para>§24.72 の書庫は「読むための写し」なので素の文字だけだが、こちらは<b>同じタブに戻して見せる</b>ためのもので、
/// 付け直しと同じ VT の列（<c>HeadlessTerminal.CreateVtSnapshot</c>）のまま残す——色・リンク・コマンドの区切りの印まで戻る。
/// 鍵はタブ ID（＝セッションの鍵）。次の起動でそのタブを立て直し、ホストが同じ ID のシェルを起こすときに 1 回だけ読んで消す。</para>
/// <para>戻るのは画面だけで、シェルもその中で動いていたもの（Claude Code・vim・dev サーバー）も新しくは起きない。
/// だから再生の後ろに「ここまで前回の画面・シェルは新しく起動」の区切りを必ず入れる（<see cref="Divider"/>）——
/// §34.6 が空のシェルを同じ名前で並べ直さないことにした理由（戻ったと見誤る）に、区切りで答える。</para>
/// </summary>
public static class TerminalScreenRestore
{
    /// <summary>1 つの上限（文字）。VT は素の文字より膨らむので書庫（<see cref="TerminalArchive.MaxChars"/>）より大きく取る。
    /// 超えたら残さない——VT の列は途中で切ると読めない。</summary>
    public const int MaxChars = 8_000_000;

    /// <summary>これより古いものは、次に書くときに消す（開き直されなかった部屋の分が溜まらないように）。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// 撮る前に古い画面へ流す片付け。代替画面から出て（vim などの全画面の絵は捨て、その前のシェルの画面に戻る）、
    /// DECSTR でモード（マウス・ブラケットペースト・kitty キーボード・カーソルキー・余白）を戻し、プログラムの申告（OSC 7501）と題を消す。
    /// これをしないと、前回 Claude Code が立てていた kitty キーボードやマウスの報告が、新しいシェルのタブに残る。
    /// 代替画面から出るのは <c>?1047l</c>（<c>?1049l</c> ではない）：<c>?1049l</c> は代替画面に居なくても保存したカーソル（たいてい左上）へ
    /// 戻すので、後ろに入れる区切りが前回の画面の頭に上書きされた。<c>?1047l</c> は居るときだけ出て、主画面のカーソルへ戻る。
    /// </summary>
    public const string CleanupBeforeSnapshot =
        "\u001b[?1047l\u001b[!p\u001b]7501;state=clear\u001b\\\u001b]2;\u0007";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>既定の置き場（<c>%APPDATA%\Loomo\terminal-restore</c>）。ホストと Loomo が同じ場所を見る。</summary>
    public static string DefaultDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "terminal-restore");

    /// <summary>残した画面の添え書き。</summary>
    public sealed record Saved(Guid SessionId, Guid? WorkspaceId, DateTime ClosedUtc, string Screen);

    private sealed record Meta(Guid SessionId, Guid? WorkspaceId, DateTime ClosedUtc);

    /// <summary>画面を残す。空・大きすぎるなら残さない。秘密らしい文字列は伏せる（§24.66）。書けたら true。</summary>
    public static bool Write(string directory, Guid sessionId, Guid? workspaceId, string vtSnapshot, DateTime closedUtc)
    {
        if (string.IsNullOrWhiteSpace(vtSnapshot) || vtSnapshot.Length > MaxChars)
            return false;
        Directory.CreateDirectory(directory);
        var screen = SecretRedactor.Redact(vtSnapshot, out _);
        File.WriteAllText(ScreenPath(directory, sessionId), screen, new UTF8Encoding(false));
        File.WriteAllText(MetaPath(directory, sessionId),
            JsonSerializer.Serialize(new Meta(sessionId, workspaceId, closedUtc), Json), new UTF8Encoding(false));
        PruneOlderThan(directory, closedUtc - MaxAge);
        return true;
    }

    /// <summary>画面が残っているタブ ID。置き場が無ければ空。</summary>
    public static IReadOnlyCollection<Guid> SavedIds(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<Guid>();
        var ids = new HashSet<Guid>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.vt"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)
                    && File.Exists(MetaPath(directory, id)))
                    ids.Add(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return ids;
    }

    /// <summary>そのタブの画面を取り出して消す（1 回だけ戻す。2 回目の起動で同じ画面が二重にならない）。無ければ null。</summary>
    public static Saved? Take(string directory, Guid sessionId)
    {
        var screenPath = ScreenPath(directory, sessionId);
        var metaPath = MetaPath(directory, sessionId);
        if (!File.Exists(screenPath) || !File.Exists(metaPath))
            return null;
        try
        {
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath), Json);
            var screen = File.ReadAllText(screenPath);
            return meta is null ? null : new Saved(meta.SessionId, meta.WorkspaceId, meta.ClosedUtc, screen);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            TryDelete(screenPath);
            TryDelete(metaPath);
        }
    }

    /// <summary>そのタブの画面を捨てる（タブを閉じた・常駐を切った）。</summary>
    public static void Discard(string directory, Guid sessionId)
    {
        TryDelete(ScreenPath(directory, sessionId));
        TryDelete(MetaPath(directory, sessionId));
    }

    /// <summary>
    /// 再生の後ろに入れる区切り（薄い字の 1 行）。行頭へ戻ってから改行するので、前回のプロンプトの行の続きには書かない。
    /// 時刻はこの PC の時刻で出す。
    /// </summary>
    public static string Divider(DateTime closedUtc)
    {
        var local = closedUtc.ToLocalTime();
        var when = local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        return $"\r\n\u001b[0m\u001b[2m── ここまで前回の画面（{when} に終了）・シェルは新しく起動 ──\u001b[0m\r\n";
    }

    private static void PruneOlderThan(string directory, DateTime limitUtc)
    {
        try
        {
            foreach (var metaPath in Directory.EnumerateFiles(directory, "*.json").ToList())
            {
                Meta? meta = null;
                try { meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath), Json); }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
                if (meta is null || meta.ClosedUtc < limitUtc)
                {
                    TryDelete(metaPath);
                    TryDelete(Path.ChangeExtension(metaPath, ".vt"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string ScreenPath(string directory, Guid id) => Path.Combine(directory, id.ToString("N") + ".vt");
    private static string MetaPath(string directory, Guid id) => Path.Combine(directory, id.ToString("N") + ".json");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
