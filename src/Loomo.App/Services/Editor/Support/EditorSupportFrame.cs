using sk0ya.Loomo.App.Views;   // EditorTab（アウトラインの持ち主）

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
    bool ShowEdit,
    bool ShowOpenInBrowser,
    bool ShowExport,
    EditorSupportFrameContent Content);

/// <summary>フレームを適用したときに確定するアウトライン状態の扱い。</summary>
internal enum EditorSupportOutlineCommitKind
{
    /// <summary>アウトラインは出ていない（コード表示以外・案内表示）。</summary>
    Clear,

    /// <summary>構造ツリーごと入れ替える。</summary>
    Replace,

    /// <summary>ツリーは据え置き、②の対象（シンボル範囲・キャレット）だけ更新する。</summary>
    Keep,
}

/// <summary>
/// フレームと<b>同時に</b>確定させるアウトライン状態。キャレット追従（<c>CaretMoved</c> の current 付替えと
/// ②パネルの取り直し判定）はこの状態だけを見て動くので、<b>画面に出ていないアウトラインが記録されると
/// 追従だけが黙って死ぬ</b>——ツリーは出ているのに②が二度と更新されない、という形の「固まる」になる。
///
/// <para>
/// だから状態は描画の途中で書かず、フレームの一部として運ぶ。書き込む場所は
/// <c>EditorSupportRenderFlow.Emit</c> ただ一つで、そこは<b>キャンセル確認の直後</b>なので、
/// 追い越されて捨てられた描画が状態を書くことが型として起こり得ない。
/// </para>
/// </summary>
internal sealed record EditorSupportOutlineCommit(
    EditorSupportOutlineCommitKind Kind,
    EditorTab? Source = null,
    string? FilePath = null,
    IReadOnlyList<OutlineNode>? Roots = null,
    LspRange? SymbolRange = null,
    (int Line, int Col)? Caret = null)
{
    public static EditorSupportOutlineCommit Cleared { get; } =
        new(EditorSupportOutlineCommitKind.Clear);
}

/// <summary>フレームの中身。表示方式ごとに1ケース。</summary>
internal abstract record EditorSupportFrameContent
{
    private EditorSupportFrameContent() { }

    /// <summary>
    /// このフレームが画面に出たときのアウトライン状態。<b>抽象メンバーにしてある</b>のは、
    /// 表示方式を1つ足したときに「アウトラインをどう扱うか」を決め忘れられないようにするため。
    /// </summary>
    internal abstract EditorSupportOutlineCommit Outline { get; }

    /// <summary>WebView2 表示（HTML 全体／本文差し替え／ファイル URI 直開き）。</summary>
    internal sealed record WebContent(
        string? Html,
        string? Body,
        string? Uri,
        string? MapFolder,
        string? PageKey,
        string? MarkdownSource,
        /// <summary>HTMLをUIスレッド外で一時ファイルへ書き込んだ後のナビゲーション先。</summary>
        string? PreparedPageUrl = null) : EditorSupportFrameContent
    {
        /// <summary>コード表示ではない＝アウトラインは無い（前のファイルの構造を持ち越さない）。</summary>
        internal override EditorSupportOutlineCommit Outline => EditorSupportOutlineCommit.Cleared;
    }

    /// <summary>
    /// WPF コントロールをそのまま載せる表示（CSV グリッド・画像・Hex 等）。
    /// <paramref name="Apply"/> は読み込み済みの内容をビューへ反映する関数で、載せた直後に同期で呼ぶ。
    /// </summary>
    internal sealed record VisualContent(
        IEditorSupportVisual Visual,
        Action Apply) : EditorSupportFrameContent
    {
        internal override EditorSupportOutlineCommit Outline => EditorSupportOutlineCommit.Cleared;
    }

    /// <summary>コード構造アウトライン（＋②呼び出しパネル）。</summary>
    /// <param name="Source">この構造の持ち主（追従元タブ）。</param>
    /// <param name="FilePath">この構造を取ったファイル。<b>タブだけでは足りない</b>——
    /// 同じタブが別のファイルを開き直すと、前のファイルの構造が「このタブのもの」として残る。</param>
    internal sealed record OutlineContent(
        IReadOnlyList<OutlineNode> Roots,
        int CurrentLine1,
        CallPanels Panels,
        EditorTab Source,
        string FilePath,
        LspRange? SymbolRange,
        (int Line, int Col) Caret) : EditorSupportFrameContent
    {
        internal override EditorSupportOutlineCommit Outline => new(
            EditorSupportOutlineCommitKind.Replace, Source, FilePath, Roots, SymbolRange, Caret);
    }

    /// <summary>
    /// 既に出ている構造ツリーはそのままに、②呼び出しパネル（と current）だけを差し替える。
    /// ツリーを作り直すと折りたたみ状態が飛ぶので、遅い LSP 解析の完了後はこちらを使う。
    /// <paramref name="CurrentLine1"/> が null なら current には触れない
    /// （キャレット移動時は <c>CaretMoved</c> が即時に付け替え済み）。
    /// </summary>
    internal sealed record PanelsContent(
        int? CurrentLine1,
        CallPanels Panels,
        LspRange? SymbolRange,
        (int Line, int Col) Caret) : EditorSupportFrameContent
    {
        /// <summary>ツリーは据え置き（折りたたみを保つ）ので、②の対象だけ更新する。</summary>
        internal override EditorSupportOutlineCommit Outline => new(
            EditorSupportOutlineCommitKind.Keep, SymbolRange: SymbolRange, Caret: Caret);
    }

    /// <summary>言語サーバー未接続／未導入などの案内。</summary>
    internal sealed record NoticeContent(
        LspNoticeModel.Notice Notice) : EditorSupportFrameContent
    {
        /// <summary>案内が出ている＝構造は画面に無い。ここを Keep にすると、消えたツリーを
        /// 相手にキャレット追従が空回りする。</summary>
        internal override EditorSupportOutlineCommit Outline => EditorSupportOutlineCommit.Cleared;
    }
}
