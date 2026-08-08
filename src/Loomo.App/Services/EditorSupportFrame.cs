namespace sk0ya.Loomo.App.Services;

/// <summary>
/// EditorSupport ペインへ<b>一度に</b>適用する、完成した表示状態。
///
/// <para>
/// 以前は <c>UpdateEditorSupportAsync</c> が <c>await</c> をまたいで
/// 「ヘッダーボタン → ビジュアル表示切替 → WebView の pending 設定 → タイトル →
/// （await）→ ナビゲート」と<b>バラバラに UI を書いて</b>いた。途中で追い越されて return すると
/// 「タイトルは新しいファイル、中身は前のファイル」という中途半端な状態がそのまま残り、
/// これが「固まったように見える」の主因だった。
/// </para>
/// <para>
/// フレームは<b>すべての await を終えてから</b>組み立て、
/// <c>ShellWindow.ApplyEditorSupportFrame</c> が同期で丸ごと適用する。
/// 部分適用が型として起こり得ない形にするのが目的。
/// </para>
/// </summary>
internal sealed record EditorSupportFrame(
    string Title,
    bool ShowSlide,
    bool ShowOutline,
    bool ShowOpenInBrowser,
    bool ShowExport,
    EditorSupportFrameContent Content);

/// <summary>フレームの中身。表示方式ごとに1ケース。</summary>
internal abstract record EditorSupportFrameContent
{
    private EditorSupportFrameContent() { }

    /// <summary>WebView2 表示（HTML 全体／本文差し替え／ファイル URI 直開き）。</summary>
    internal sealed record WebContent(
        string? Html,
        string? Body,
        string? Uri,
        string? MapFolder,
        string? PageKey) : EditorSupportFrameContent;

    /// <summary>
    /// WPF コントロールをそのまま載せる表示（CSV グリッド・画像・Hex 等）。
    /// <paramref name="Apply"/> は読み込み済みの内容をビューへ反映する関数で、載せた直後に同期で呼ぶ。
    /// </summary>
    internal sealed record VisualContent(
        IEditorSupportVisual Visual,
        Action Apply) : EditorSupportFrameContent;

    /// <summary>コード構造アウトライン（＋②呼び出しパネル）。</summary>
    internal sealed record OutlineContent(
        IReadOnlyList<OutlineNode> Roots,
        int CurrentLine1,
        CallPanels Panels) : EditorSupportFrameContent;

    /// <summary>
    /// 既に出ている構造ツリーはそのままに、②呼び出しパネル（と current）だけを差し替える。
    /// ツリーを作り直すと折りたたみ状態が飛ぶので、遅い LSP 解析の完了後はこちらを使う。
    /// <paramref name="CurrentLine1"/> が null なら current には触れない
    /// （キャレット移動時は <c>CaretMoved</c> が即時に付け替え済み）。
    /// </summary>
    internal sealed record PanelsContent(
        int? CurrentLine1,
        CallPanels Panels) : EditorSupportFrameContent;

    /// <summary>言語サーバー未接続／未導入などの案内。</summary>
    internal sealed record NoticeContent(
        LspNoticeModel.Notice Notice) : EditorSupportFrameContent;
}
