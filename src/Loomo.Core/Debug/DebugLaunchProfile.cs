using System;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>名前付きデバッグ起動構成（VS Code の launch.json のプロファイル相当）。ワークスペースごとに
/// 複数持てる。<see cref="ProjectPath"/> はワークスペースルートからの相対パス（<c>*.csproj</c>）で、
/// null/空なら「自動検出」（ワークスペース直下から最初に見つかった .csproj を使う既存の挙動）。
/// <see cref="TargetProgram"/> は明示的な実行対象（<c>*.dll</c>/<c>*.exe</c>）の直接指定で、空なら
/// <see cref="ProjectPath"/>（またはその自動検出結果）をビルドして出力 dll を探す。</summary>
public sealed record DebugLaunchProfile(
    string Id,
    string Name,
    string? ProjectPath,
    string TargetProgram,
    bool BuildFirst,
    string LaunchArgs,
    string LaunchEnv,
    bool JustMyCode,
    bool BreakOnAllExceptions,
    bool BreakOnUncaughtExceptions)
{
    /// <summary>新規プロファイルの既定値（今までの唯一の設定と同じ初期値）。</summary>
    public static DebugLaunchProfile CreateDefault(string name) => new(
        Id: Guid.NewGuid().ToString("N"),
        Name: name,
        ProjectPath: null,
        TargetProgram: "",
        BuildFirst: true,
        LaunchArgs: "",
        LaunchEnv: "",
        JustMyCode: false,
        BreakOnAllExceptions: false,
        BreakOnUncaughtExceptions: false);
}
