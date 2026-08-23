using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>デバッグ起動構成。</summary>
public sealed record DebugLaunchConfig(
    string Program,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool StopAtEntry = false,
    bool JustMyCode = false,
    // ブラウザ（chrome:URL）ターゲット専用：可視ブラウザペイン（WebView2）の CDP リモートデバッグポート。
    // 設定時は外部 Chrome を起動する代わりに、このポートへ pwa-chrome の attach を張ってペインをデバッグする。
    int? BrowserDebugPort = null);

/// <summary>実行中のプロセスに接続するための構成。dotnet（netcoredbg）は <paramref name="ProcessId"/> で、
/// Node.js（js-debug）は <paramref name="Port"/>（<c>--inspect</c> のデバッグポート）で接続する。</summary>
public sealed record DebugAttachConfig(int ProcessId, string? Name = null, int? Port = null);

/// <summary>ソース走査で見つかったテスト。<paramref name="SourcePath"/>／<paramref name="Line1"/> は
/// 宣言位置（1 始まり。エディタのガターに ▶ を出す位置）。走査元のファイルを渡されなかった場合は
/// <paramref name="SourcePath"/> だけが null になる（行は常に判る）。<b>失敗位置（スタックトレース由来）とは別物</b>——あちらはヘルパの行を指すことがあり、
/// ▶ の置き場所には使えない。</summary>
public sealed record DiscoveredTest(string FullyQualifiedName, bool IsParameterized,
    string? SourcePath = null, int Line1 = 0);
