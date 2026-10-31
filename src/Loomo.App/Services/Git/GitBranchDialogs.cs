using System.Windows;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ブランチまわりの確認・入力ダイアログ。同じ操作を持つGitペインと切替ポップアップで、
/// 文言と既定値を共通にする。
/// </summary>
internal static class GitBranchDialogs
{
    /// <summary>強制プッシュの確認。履歴の置き換えとleaseの効き目を明示する。</summary>
    public static bool ConfirmForcePush(Window? owner, string target) =>
        MessageBox.Show(owner,
            $"{target} をリモートへ強制的に上書きします（--force-with-lease）。\n\n" +
            "リモートの履歴はこちらの内容で置き換わります。\n" +
            "最後に取得した位置からリモートが進んでいた場合は、上書きせずに中止します。\n\n実行しますか？",
            "強制プッシュ", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>切替がローカル変更とぶつかって止まったとき（§24.99）。はい＝退避して切り替える、いいえ＝変更を持って切り替える、
    /// キャンセル＝やめる。ぶつかったファイルは先頭の数件だけ並べる。</summary>
    public static CheckoutRefusalChoice AskCheckoutWithLocalChanges(
        Window? owner, string branch, IReadOnlyList<string> files, bool untracked)
    {
        const int shown = 8;
        var list = string.Join("\n", files.Take(shown).Select(f => "  " + f))
                   + (files.Count > shown ? $"\n  ほか {files.Count - shown} 件" : "");
        var what = untracked ? "追跡していないファイル" : "未コミットの変更";
        var answer = MessageBox.Show(owner,
            $"{branch} へ切り替えると、次の{what}が上書きされるため、git が切替を止めました。\n\n{list}\n\n" +
            "［はい］変更をスタッシュに退避して切り替える（Git パネルのスタッシュから戻せます）\n" +
            "［いいえ］変更を持ったまま切り替える（git checkout -m。ぶつかる箇所は衝突として残ります）\n" +
            "［キャンセル］切り替えない",
            "ローカル変更があるため切り替えられません", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return answer switch
        {
            MessageBoxResult.Yes => CheckoutRefusalChoice.Stash,
            MessageBoxResult.No => CheckoutRefusalChoice.Carry,
            _ => CheckoutRefusalChoice.Cancel,
        };
    }

    /// <summary>ローカル削除と取り違えないよう、リモートブランチ削除を明示して確認する。</summary>
    public static bool ConfirmDeleteRemoteBranch(Window? owner, string remoteBranch) =>
        MessageBox.Show(owner,
            $"リモート上のブランチ {remoteBranch} を削除します。\n" +
            "リモートから消えるため、他の人の作業にも影響します。\n\n実行しますか？",
            "リモートブランチの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>現在の上流または既定リモートの同名ブランチを候補にした上流入力を表示する。</summary>
    public static string? PromptUpstream(Window? owner, GitSessionViewModel vm, GitBranchInfo branch)
    {
        var arguments = GitBranchActionPolicy.UpstreamPrompt(
            branch.Name, branch.Upstream, vm.RemoteLabel, vm.RemoteBranchNames);
        return InputDialog.Prompt(owner, "上流を設定",
            $"{branch.Name} の上流を入力してください（例: origin/main）{arguments.CandidateHint}",
            arguments.InitialValue);
    }
}
