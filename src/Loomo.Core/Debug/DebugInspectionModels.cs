namespace sk0ya.Loomo.Core.Debug;

/// <summary>コールスタックの1フレーム。Line は1始まり。</summary>
public sealed record DebugStackFrame(int Id, string Name, string? SourcePath, int Line);
/// <summary>変数スコープ。</summary>
public sealed record DebugScope(string Name, int VariablesReference, bool Expensive);
/// <summary>デバッグ中の変数。</summary>
public sealed record DebugVariable(string Name, string Value, string? Type, int VariablesReference);
/// <summary>実行中スレッド。</summary>
public sealed record DebugThread(int Id, string Name);
/// <summary>ロード済みモジュール。</summary>
public sealed record DebugModule(string Name, string? Path, string? Version, string? SymbolStatus);
/// <summary>ステップイン候補。</summary>
public sealed record DebugStepInTarget(int Id, string Label);
/// <summary>例外ブレークのフィルタ。</summary>
public sealed record DebugExceptionFilter(string Id, string Label, bool Default);
/// <summary>行と任意の条件を持つブレークポイント。Line は1始まり。</summary>
public sealed record DebugBreakpoint(
    int Line,
    string? Condition = null,
    string? HitCondition = null,
    string? LogMessage = null,
    bool Enabled = true);
