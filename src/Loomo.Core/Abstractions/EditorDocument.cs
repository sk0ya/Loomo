namespace sk0ya.Loomo.Core.Abstractions;

using System;

/// <summary>
/// エディタペインで編集する「仮想ドキュメント」。永続先をファイルとせず、ユーザーが保存（:w）
/// したときに <see cref="OnSaved"/> で最新内容を呼び出し側へ返す。設定の長文項目
/// （システムプロンプト・危険コマンド一覧）を、狭いサイドバーの TextBox ではなく
/// エディタ領域で編集するために使う。永続化はコールバック側の責務。
/// </summary>
public sealed class EditorDocument
{
    /// <summary>エディタのタイトルに使う表示名。編集用の一時ファイル名にもなる（例: "loomo-system-prompt.md"）。</summary>
    public required string FileName { get; init; }

    /// <summary>エディタに表示する初期内容。</summary>
    public required string Content { get; init; }

    /// <summary>保存（:w）されたときに、エディタの最新内容で呼ばれるコールバック。</summary>
    public required Action<string> OnSaved { get; init; }
}
