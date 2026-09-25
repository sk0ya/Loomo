using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// フォルダーツリーの「選択が止まったら、その行をプレビュータブで開く」の判定。
/// <para>
/// 以前は選択変更の通知が来たら中身を見ずに据え置きを仕掛け、明けたときに<b>その時点で選ばれている行</b>を開いていた。
/// ツリーを入れ替えると（ワークスペース切替）まず選択が外れた通知（null）が来て据え置きが仕掛かり、
/// そのあいだに復元で選ばれた行が「明けたときに選ばれている行」として開かれる——復元選択は開かない決まりを
/// すり抜けるうえ、切替の途中で開くので、タブが前のワークスペースへ紛れ込んでいた。
/// 開いてよいのは<b>据え置きを仕掛けた行が、明けたときもまだ選ばれている</b>ときだけにする。
/// </para>
/// </summary>
internal static class FolderTreeSelectionPreviewPolicy
{
    /// <summary>選択が変わったとき、据え置きのあとで開く候補。null＝開かない（選択が外れた・フォルダーの行）。</summary>
    public static FileNodeViewModel? CandidateFor(object? newSelection)
        => newSelection is FileNodeViewModel { IsDirectory: false } node ? node : null;

    /// <summary>据え置きが明けたとき、候補を開いてよいか。</summary>
    public static bool ShouldPreview(object? selectedNow, FileNodeViewModel? candidate)
        => candidate is not null && ReferenceEquals(selectedNow, candidate);
}
