using Editor.Controls;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Diff の右側で編集する作業ツリーのファイルの<b>持ち主</b>＝Editor ペイン。右側は自分でファイルを
/// 持たず、Editor ペインのタブの本文を映して一緒に編集する（§24.16）。だから未保存の編集は Editor の
/// タブに残り、Diff を閉じても・別のファイルへ移っても何も聞かない——同じ文書を2か所から触っているだけ。
/// </summary>
internal interface IDiffWorkingDocuments
{
    /// <summary>タブの文書の読み込み・保存・閉じる（右が映し直す・保存済みに揃える・ディスクへ戻るため）。</summary>
    EditorDocumentEvents Events { get; }

    /// <summary>そのファイルを開いている Editor のタブのエディタ。開いていなければ null。</summary>
    VimEditorControl? Find(string path);

    /// <summary>Editor ペインにタブとして開き、そのエディタを返す。前面には出さない（Diff で打っている
    /// 最中に部屋の配置を変えない）。開けなければ null。</summary>
    VimEditorControl? Open(string path);

    /// <summary>Editor ペインと同じ経路で保存する（Ctrl+S・:w と同じ）。</summary>
    Task SaveAsync(VimEditorControl document);

    /// <summary>そのエディタがまだ Editor ペインのタブとして開いているか（閉じられたら追従をやめる）。</summary>
    bool IsOpen(VimEditorControl document);
}
