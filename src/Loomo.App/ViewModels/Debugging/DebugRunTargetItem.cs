using System;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.CSharp.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>「何を実行するか」を 1 行で言い切った実行ターゲット（Rider の実行構成ウィジェットの行）。
///
/// 以前のタイトルバーのメニューは「構成」「起動プロジェクト」「launchSettings」を<b>3 つの並列な一覧</b>で
/// 出していた。どれを選べば何が動くのかが行だけからは分からず（3 つの掛け合わせが答えだった）、選んでから
/// 別の場所の「開始」を押す必要もあった。ここでは Rider と同じく、<b>1 行＝そのまま実行できる 1 つの対象</b>に
/// 畳む：保存した構成はそのまま 1 行、プロジェクトは launchSettings のプロファイルごとに 1 行
/// （「Loomo.App」「Loomo.App: http」）。行の右の ▶／🐞 がその行の実行・デバッグになる。
/// </summary>
public sealed partial class DebugRunTargetItem : ObservableObject
{
    /// <summary>保存した構成の行。</summary>
    public DebugRunTargetItem(DebugLaunchProfileItem profile, string detail, string? runProjectPath)
    {
        Profile = profile;
        Name = profile.Name;
        Detail = detail;
        RunProjectPath = runProjectPath;
        Glyph = "⚙";
    }

    /// <summary>プロジェクトの行（<paramref name="launchProfile"/> があれば launchSettings のプロファイル行）。</summary>
    public DebugRunTargetItem(DebugProjectDiscovery.ProjectEntry project, LaunchSettingsProfile? launchProfile,
        string detail)
    {
        Project = project;
        LaunchProfile = launchProfile;
        Name = launchProfile is null ? project.Name : $"{project.Name}: {launchProfile.Name}";
        Detail = detail;
        RunProjectPath = project.FullPath.Length == 0 ? null : project.FullPath;
        Glyph = launchProfile is null ? "▸" : "⚑";
        IsChild = launchProfile is not null;
    }

    public DebugLaunchProfileItem? Profile { get; }
    public DebugProjectDiscovery.ProjectEntry? Project { get; }
    public LaunchSettingsProfile? LaunchProfile { get; }

    /// <summary>行の見出し（構成名／プロジェクト名／「プロジェクト: プロファイル」）。</summary>
    public string Name { get; }

    /// <summary>右に淡色で添える補足（構成なら解決後の対象、プロジェクトなら相対パス）。</summary>
    public string Detail { get; }

    public string Glyph { get; }

    /// <summary>launchSettings のプロファイル行（親のプロジェクト行にぶら下がる＝字下げする）。</summary>
    public bool IsChild { get; }

    /// <summary>デバッグなしの実行（▶）に使う .csproj。決まらない行（自動検出・対象未解決の構成）では null で、
    /// その行の ▶ は出さない——押せるのに何も起きない行を作らないため。</summary>
    public string? RunProjectPath { get; }

    public bool CanRun => RunProjectPath is not null;

    /// <summary>グループの先頭行にだけ載せる見出し（「構成」「対象」）。一覧を 1 本に保ったまま
    /// 「保存した構成」と「この構成で実行する対象」の境目を見せるための 1 行。</summary>
    public string? GroupLabel { get; init; }

    public string ToolTipText
        => $"{Name}（{Detail}）{Environment.NewLine}クリックでこの対象に切り替え／▶ 実行・🐞 デバッグはその場で開始";

    /// <summary>いま選ばれている行（● を出す）。選択状態は VM 側が一括で同期する。</summary>
    [ObservableProperty] private bool _isCurrent;
}
