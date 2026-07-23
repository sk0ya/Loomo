using System.ComponentModel;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>起動構成の「現在値」（対象・引数・環境変数・例外オプション）を持つ起動系サブ VM の窓口。
/// <see cref="DebugProfilesViewModel"/> がプロファイル切替時の流し込み先／編集のデバウンス保存元として使う。
/// dotnet（<see cref="DebugLaunchViewModel"/>）と TypeScript の起動 VM が実装する。
/// プロパティ名は <see cref="DebugProfilesViewModel"/> の監視対象名と一致している必要がある。</summary>
internal interface ILaunchConfigurationOwner : INotifyPropertyChanged
{
    /// <summary>実行対象。意味は toolchain 依存（dotnet: dll/exe パス、TS: ファイルパスまたは "npm:スクリプト名"）。</summary>
    string TargetProgram { get; set; }

    /// <summary>開始前にビルド系の前処理を行うか（dotnet: dotnet build、TS: tsc 型チェック）。</summary>
    bool BuildFirst { get; set; }

    /// <summary>コマンドライン引数（1 行のテキスト、<c>DebugLaunchArgs.ParseArgs</c> で分解）。</summary>
    string LaunchArgs { get; set; }

    /// <summary>環境変数（KEY=VALUE の複数行テキスト、<c>DebugLaunchArgs.ParseEnv</c> で分解）。</summary>
    string LaunchEnv { get; set; }

    /// <summary>自分のコードだけデバッグ（dotnet: justMyCode、TS: node_internals のスキップ）。</summary>
    bool JustMyCode { get; set; }

    /// <summary>すべての例外で停止。</summary>
    bool BreakOnAllExceptions { get; set; }

    /// <summary>未処理の例外で停止。</summary>
    bool BreakOnUncaughtExceptions { get; set; }
}
