using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// <c>loomo://</c> を Windows に教える（§24.59）。利用者ごと（HKCU）に、<b>初めてリンクをコピーしたときだけ</b>書く——
/// 起動のたびにレジストリを触らないし、リンクを使わない人の環境には何も残さない。exe の場所が変わっていれば書き直す
/// （ビルドの置き場は固定だが、別の場所から起動した Loomo が最後にコピーした方を指す）。
/// 踏まれたら <c>"exe" "loomo://…"</c> で起動され、<see cref="StartupArguments"/> が読み、起動中の部屋へ中継する。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LoomoLinkProtocol
{
    private const string KeyPath = @"Software\Classes\" + LoomoLink.Scheme;

    /// <summary>登録が今の exe を指すようにする。書けなかった（ポリシー等）ときは false。リンクのコピー自体は止めない。</summary>
    public static bool EnsureRegistered(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return false;
        var command = $"\"{exePath}\" \"%1\"";
        try
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command"))
            {
                if (existing?.GetValue(null) as string == command)
                    return true;
            }
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(null, "URL:Loomo の場所");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exePath}\",0");
            using (var open = key.CreateSubKey(@"shell\open\command"))
                open.SetValue(null, command);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
