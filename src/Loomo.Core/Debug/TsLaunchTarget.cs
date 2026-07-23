using System;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>
/// TypeScript / Node.js デバッグの実行対象のエンコード。<see cref="DebugLaunchProfile.TargetProgram"/>（文字列
/// 1 本）に「ファイルパス」と「npm スクリプト」の両方を格納できるよう、npm スクリプトは <c>npm:スクリプト名</c>
/// の接頭辞付きで表す（例: <c>npm:dev</c>）。プロファイルのレコード形は dotnet と共用したまま変えない。
/// </summary>
public static class TsLaunchTarget
{
    public const string NpmPrefix = "npm:";

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
}
