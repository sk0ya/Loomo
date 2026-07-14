namespace sk0ya.Loomo.App.Detach;

/// <summary>別ウィンドウへ切り離した項目の種別。Editor/EditorSupport は「複製＋同期」、
/// Terminal/Browser は「新規スピンオフ」（同期なしの独立インスタンス）。</summary>
internal enum DetachKind
{
    /// <summary>メインの Editor タブと同一ファイルを開き、テキストを双方向同期する複製。</summary>
    EditorMirror,
    /// <summary>追従元エディタから再描画する EditorSupport の複製。</summary>
    EditorSupportMirror,
    /// <summary>同じ cwd で起動する独立ターミナル（同期なし）。</summary>
    TerminalSpinoff,
    /// <summary>開いていた URL を初回だけ開く独立ブラウザ（同期なし）。</summary>
    BrowserSpinoff,
    /// <summary>メインの Editor タブをそのまま別ウィンドウへ移動した実コントロール（複製なし）。</summary>
    EditorMove,
    /// <summary>メインの Terminal タブ（生セッション）をそのまま別ウィンドウへ移動した実コントロール（複製なし）。</summary>
    TerminalMove
}
