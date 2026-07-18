using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>デバッグ起動構成。</summary>
public sealed record DebugLaunchConfig(
    string Program,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool StopAtEntry = false,
    bool JustMyCode = false);

/// <summary>実行中の .NET プロセスに接続するための構成。</summary>
public sealed record DebugAttachConfig(int ProcessId, string? Name = null);

/// <summary>ソース走査で見つかったテスト。</summary>
public sealed record DiscoveredTest(string FullyQualifiedName, bool IsParameterized);
