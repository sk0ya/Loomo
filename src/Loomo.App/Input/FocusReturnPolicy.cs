using System;

using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Input;

/// <summary>フォーカスを戻す先の種別（外側ほど粗い退避先）。</summary>
public enum FocusReturnKind
{
    /// <summary>戻す先が無い（呼び出し側は何もしない＝現在のフォーカスを触らない）。</summary>
    None,

    /// <summary>覚えていた要素そのもの（エディタ Canvas・ターミナル surface・一覧の行など）。</summary>
    Element,

    /// <summary>ペイン内の特定ビューポート（分割中に元の分割面へ戻す）。</summary>
    Viewport,

    /// <summary>ペイン単位（そのペインの既定の入力先へ）。</summary>
    Pane,

    /// <summary>サイドバー。</summary>
    Sidebar,
}

/// <summary>覚えておいた「最後の内部フォーカス」の位置。<see cref="Pane"/> が null ならサイドバー。</summary>
/// <param name="Pane">フォーカスがあったペイン。null はサイドバー。</param>
/// <param name="ViewportId">ペイン内の分割ビューポート Id（分割していないときは <c>default</c>）。</param>
public readonly record struct FocusReturnOrigin(PaneKind? Pane, Guid ViewportId = default)
{
    /// <summary>サイドバーを指す起点。</summary>
    public static FocusReturnOrigin Sidebar => new((PaneKind?)null);

    /// <summary>ペイン全体を指す起点。</summary>
    public static FocusReturnOrigin Of(PaneKind kind) => new(kind);

    /// <summary>ペイン内のビューポートを指す起点。</summary>
    public static FocusReturnOrigin Viewport(PaneKind kind, Guid viewportId) => new(kind, viewportId);
}

/// <summary>フォーカスを戻す先の決定結果。</summary>
/// <param name="Kind">戻し方。</param>
/// <param name="Pane">対象ペイン（<see cref="FocusReturnKind.Viewport"/>／<see cref="FocusReturnKind.Pane"/> のとき）。</param>
/// <param name="ViewportId">対象ビューポート（<see cref="FocusReturnKind.Viewport"/> のとき）。</param>
public readonly record struct FocusReturnDecision(FocusReturnKind Kind, PaneKind? Pane = null, Guid ViewportId = default)
{
    /// <summary>何もしない。</summary>
    public static readonly FocusReturnDecision None = new(FocusReturnKind.None);
}

/// <summary>
/// 設定ウィンドウのように「本体の外」で入力を受ける面を閉じたあと、キーボードフォーカスを
/// <b>どこへ戻すか</b>を決める純ロジック（WPF 非依存・テスト可能）。
///
/// <para>設計書 §31.8（Phase 5）の完了条件「舞台切替、デタッチ復帰、設定オーバーレイを挟んでも
/// キャレット／選択／入力先が予測可能である」に対応する。開く直前に覚えた「最後の内部フォーカス」
/// （<see cref="FocusReturnOrigin"/> と、実際にフォーカスを持っていた要素）を第一候補とし、
/// 閉じるまでにそれが失われていた場合の退避先を順に落としていく。</para>
///
/// <para>優先順位は内側から外側へ：
/// <list type="number">
///   <item>覚えていた要素そのもの（生きていて可視・操作可能なら、そこへ戻す）</item>
///   <item>同じペインの同じビューポート（要素は消えたが分割面は残っている＝タブを閉じた等）</item>
///   <item>同じペイン（分割構成が変わった＝ビューポートが消えた）</item>
///   <item>サイドバー（起点がサイドバーで、要素だけ消えた）</item>
///   <item>何もしない（ペインが非表示・サイドバーが閉じた＝戻す先が無い）</item>
/// </list>
/// 「何もしない」を最後の退避先にするのは、勝手に別ペインへ入力先を移すほうが予測不能になるため。
/// 要素の復元は各コントロールが自分でフォーカスを置いた要素へ戻すだけで、内部状態は再実装しない
/// （§31.2-3「Editor に属するものを Loomo へ複製しない」）。</para>
/// </summary>
public static class FocusReturnPolicy
{
    /// <summary>戻す先を決める。</summary>
    /// <param name="origin">開く直前に覚えた最後の内部フォーカス位置。覚えていなければ null。</param>
    /// <param name="elementAlive">覚えていた要素がまだ生きていて、可視・操作可能で、本体ウィンドウの配下にあるか。</param>
    /// <param name="paneAvailable">起点のペインが今もフォーカス可能か（可視、または舞台で出し直せる）。サイドバー起点では無視。</param>
    /// <param name="viewportAlive">起点のビューポートが今も存在するか。ビューポート起点でないときは無視。</param>
    /// <param name="sidebarVisible">サイドバーが今も開いているか。サイドバー起点でないときは無視。</param>
    public static FocusReturnDecision Decide(
        FocusReturnOrigin? origin,
        bool elementAlive,
        bool paneAvailable,
        bool viewportAlive,
        bool sidebarVisible)
    {
        if (origin is not { } from)
            return FocusReturnDecision.None;

        if (from.Pane is not { } pane)
            return elementAlive ? new FocusReturnDecision(FocusReturnKind.Element)
                : sidebarVisible ? new FocusReturnDecision(FocusReturnKind.Sidebar)
                : FocusReturnDecision.None;

        // ペインが消えている（非表示にした・レイアウトから外した）なら、要素が生きていても戻さない。
        // 見えていない場所へ入力先を移すと、次のキー入力の行き先が説明できなくなる。
        if (!paneAvailable)
            return FocusReturnDecision.None;

        if (elementAlive)
            return new FocusReturnDecision(FocusReturnKind.Element, pane, from.ViewportId);

        if (from.ViewportId != default && viewportAlive)
            return new FocusReturnDecision(FocusReturnKind.Viewport, pane, from.ViewportId);

        return new FocusReturnDecision(FocusReturnKind.Pane, pane);
    }
}
