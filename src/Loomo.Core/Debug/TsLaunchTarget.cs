using System;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>
/// TypeScript / Node.js デバッグの実行対象のエンコード。<see cref="DebugLaunchProfile.TargetProgram"/>（文字列
/// 1 本）に「ファイルパス」「npm スクリプト」「ブラウザ URL」を格納できるよう、後 2 者は接頭辞付きで表す
/// （<c>npm:dev</c> / <c>chrome:http://localhost:5173</c>）。プロファイルのレコード形は dotnet と共用したまま変えない。
/// </summary>
public static class TsLaunchTarget
{
    public const string NpmPrefix = "npm:";
    public const string ChromePrefix = "chrome:";

    /// <summary>npm スクリプト名 → 格納形式（"npm:dev"）。</summary>
    public static string FormatNpmScript(string script) => NpmPrefix + script;

    /// <summary>格納形式が npm スクリプトなら true を返し、スクリプト名を取り出す。</summary>
    public static bool TryParseNpmScript(string target, out string script)
    {
        if (target.StartsWith(NpmPrefix, StringComparison.Ordinal))
        {
            script = target[NpmPrefix.Length..].Trim();
            return true;
        }
        script = "";
        return false;
    }

    /// <summary>ブラウザ URL → 格納形式（"chrome:http://localhost:5173"）。</summary>
    public static string FormatChromeUrl(string url) => ChromePrefix + url;

    /// <summary>格納形式がブラウザ（Chrome）デバッグなら true を返し、URL を取り出す。</summary>
    public static bool TryParseChromeUrl(string target, out string url)
    {
        if (target.StartsWith(ChromePrefix, StringComparison.Ordinal))
        {
            url = target[ChromePrefix.Length..].Trim();
            return true;
        }
        url = "";
        return false;
    }
}
