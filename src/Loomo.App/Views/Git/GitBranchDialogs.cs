using System;
using System.Linq;
using System.Windows;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ブランチまわりの確認・入力ダイアログ。<b>同じ操作が2箇所（Git ペインのブランチ一覧と、
/// タイトルバーのブランチ切替ポップアップ）にある</b>ので、文言と既定値をここ1箇所に置く
/// ——取り返しのつかない操作の説明文が場所によって違う、が起きないようにするため。
/// </summary>
internal static class GitBranchDialogs
{
    /// <summary>強制プッシュの確認。何が起きるか（履歴の置き換え）と、lease の効き目を明示する。</summary>
    public static bool ConfirmForcePush(Window? owner, string target) =>
        MessageBox.Show(owner,
            $"{target} をリモートへ強制的に上書きします（--force-with-lease）。\n\n" +
            "リモートの履歴はこちらの内容で置き換わります。\n" +
            "最後に取得した位置からリモートが進んでいた場合は、上書きせずに中止します。\n\n実行しますか？",
            "強制プッシュ", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>リモートブランチ削除の確認（ローカルの削除と取り違えられないよう明示する）。</summary>
    public static bool ConfirmDeleteRemoteBranch(Window? owner, string remoteBranch) =>
        MessageBox.Show(owner,
            $"リモート上のブランチ {remoteBranch} を削除します。\n" +
            "リモートから消えるため、他の人の作業にも影響します。\n\n実行しますか？",
            "リモートブランチの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>
    /// 上流（追跡先）の入力。既定値は「今の上流」→無ければ「既定リモート/同名ブランチ」で、
    /// 実在するリモート追跡ブランチを候補として本文に並べる（打ち間違いを減らすため）。
    /// </summary>
    public static string? PromptUpstream(Window? owner, GitSessionViewModel vm, GitBranchInfo branch)
    {
        var initial = branch.Upstream is { Length: > 0 } current
            ? current
            : vm.RemoteLabel.Length > 0 ? $"{vm.RemoteLabel}/{branch.Name}" : "";

        var candidates = vm.RemoteBranchNames
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .ToList();
        var hint = candidates.Count > 0
            ? "\n\n候補: " + string.Join(", ", candidates)
            : "";

        return InputDialog.Prompt(owner, "上流を設定",
            $"{branch.Name} の上流を入力してください（例: origin/main）{hint}", initial);
    }
}
