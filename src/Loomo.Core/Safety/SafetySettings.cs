using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Safety;

/// <summary>
/// コマンド実行・ファイル書込の安全設計（設計書 §10）。
/// アプリ設定の一部として永続化し、設定画面から編集する。
/// </summary>
public sealed class SafetySettings
{
    /// <summary>
    /// 自動承認モード。信頼できるワークスペース向けのオプトイン。
    /// 有効でも、ブロックリストに一致する危険コマンドは常にブロックされる。
    /// </summary>
    public bool AutoApprove { get; set; }

    /// <summary>ツールのファイルアクセスをワークスペースルート配下に限定する（パストラバーサル防止）。</summary>
    public bool RestrictToWorkspaceRoot { get; set; } = true;

    /// <summary>常にブロックする危険コマンドの正規表現パターン（大文字小文字無視）。</summary>
    public List<string> BlockedCommandPatterns { get; set; } = new(DefaultBlockedPatterns);

    /// <summary>既定の危険コマンドパターン（破壊的操作・システム停止・フォークボム等）。</summary>
    public static readonly string[] DefaultBlockedPatterns =
    {
        @"\brm\s+(?=(?:\S+\s+)*-\S*f)(?=(?:\S+\s+)*-\S*r)", // rm に force(-f) と recursive(-r) が付く（-rf/-fr/分割いずれも）
        @"\brmdir\s+/s",                          // rmdir /s
        @"\bdel\s+/[sq]",                         // del /s, del /q
        @"\bRemove-Item\b(?=.*-Recurse)(?=.*-Force)", // Remove-Item -Recurse -Force（順不同）
        @"\bformat\s+(/|[a-z]:)",                 // ディスクフォーマット（format C: / format /q）。dotnet format 等は除外
        @"\bmkfs\b",
        @"\bdd\s+if=",
        @">\s*/dev/sd",
        @"\bshutdown\b",
        @"\breboot\b",
        @":\(\)\s*\{\s*:\|:",                      // フォークボム :(){ :|:& };:
    };
}
