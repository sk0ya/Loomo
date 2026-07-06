using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Settings;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: ペインの表示/非表示トグルと、開いたファイル・結果表示のためのペイン確保
/// （SetPaneVisible・トグル状態同期・左上入れ替え・最下段追加）。レイアウト構築は ShellWindow.PaneLayout.cs。</summary>
public partial class ShellWindow
{
    private void OnHidePane(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;
        BeginTrailLayoutChange();
        SetPaneVisible(kind, false);
    }

    // ── ペイン表示トグルのホバードロップダウン（メインペインヘッダー直下に縦並び） ──
    // ヘッダーに入ったら開き、開いている間は DispatcherTimer でマウスの画面座標を監視して、
    // ヘッダーと枠の画面矩形を PaneTogglePopupHoverSlackPx だけふくらませた範囲から出たら閉じる。
    // 透明な当たり判定レイヤーは AllowsTransparency ポップアップ上でヒットが不安定なので使わない。
    // この方式ならスラックを画面座標で確実に好きなだけ広げられ、下の UI へのクリックも奪わない。

    /// <summary>当たり判定を見える枠よりどれだけ外側まで広げるか（デバイスピクセル）。</summary>
    private const double PaneTogglePopupHoverSlackPx = 120;

    private DispatcherTimer? _paneToggleHoverTimer;

    private void OnMainPaneMouseEnter(object sender, MouseEventArgs e)
    {
        PaneTogglePopup.IsOpen = true;
        (_paneToggleHoverTimer ??= CreatePaneToggleHoverTimer()).Start();
    }

    private DispatcherTimer CreatePaneToggleHoverTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            if (!PaneTogglePopup.IsOpen || !IsMouseNearPaneTogglePopup())
            {
                PaneTogglePopup.IsOpen = false;
                _paneToggleHoverTimer?.Stop();
            }
        };
        return timer;
    }

    /// <summary>マウスがヘッダー／枠の「ふくらませた」画面矩形のどちらかに入っているか。</summary>
    private bool IsMouseNearPaneTogglePopup()
    {
        if (!GetCursorPos(out var p))
            return true; // 座標取得に失敗したら閉じない（誤クローズより開きっぱなしの方が安全）
        var mouse = new Point(p.X, p.Y);
        return InflatedScreenRect(MainPaneButton).Contains(mouse)
            || (PaneTogglePopupRoot.IsVisible && InflatedScreenRect(PaneTogglePopupRoot).Contains(mouse));
    }

    /// <summary>要素の画面矩形（デバイスピクセル）をスラック分ふくらませて返す。</summary>
    private static Rect InflatedScreenRect(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        var rect = new Rect(topLeft, bottomRight);
        rect.Inflate(PaneTogglePopupHoverSlackPx, PaneTogglePopupHoverSlackPx);
        return rect;
    }
    // GetCursorPos / POINT は ShellWindow.SpanMaximize.cs に定義済みのものを使う。

    /// <summary>現在のメインペイン（ソロ中は舞台のペイン、タイル時は左上の可視ペイン）。</summary>
    private PaneKind? CurrentMainPane() => _stageActive ? _stagePane : TopLeftPane();

    /// <summary>ペインの線画アイコンの StreamGeometry リソースキー（XAML 側と単一ソース）。</summary>
    private static string PaneIconKey(PaneKind kind) => $"PaneIcon.{kind}";

    /// <summary>メインペインヘッダーのアイコンとツールチップを現在のメインペインへ同期する。</summary>
    private void UpdateMainPaneHeader()
    {
        var main = CurrentMainPane();
        MainPaneIcon.Data = main is { } kind && TryFindResource(PaneIconKey(kind)) is Geometry geo ? geo : null;
        MainPaneButton.ToolTip = main is { } k
            ? $"メインペイン：{PaneLabel(k)}（ホバーで表示ペインを切り替え）"
            : "表示ペインを切り替え（ホバーで一覧）";
    }

    private void OnTogglePaneVisibility(object sender, RoutedEventArgs e)
    {
        BeginTrailLayoutChange();
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneKind>(tag, out var kind))
            ToggleSessionEnabled(kind);

        // ToggleSessionEnabled が何もしなかった場合（最後の1枚は無効化できない等）も、クリックで
        // 勝手に反転した IsChecked を実状態へ戻す必要があるためここでも同期する。
        UpdatePaneToggleStates();
    }

    /// <summary>
    /// タイトルバーのペイントグルを実際の有効状態へ同期する（IsChecked＝有効→アクセント色）。
    /// ツールチップも「有効化／無効化」を状態に合わせて切り替える。
    /// </summary>
    private void UpdatePaneToggleStates()
    {
        foreach (var child in PaneToggleBar.Children)
        {
            if (child is not ToggleButton { Tag: string tag } button || !Enum.TryParse<PaneKind>(tag, out var kind))
                continue;
            var enabled = IsSessionEnabled(kind);
            button.IsChecked = enabled;
            button.ToolTip = $"{PaneLabel(kind)} を{(enabled ? "無効化" : "有効化")}";
        }

        UpdateMainPaneHeader();
    }

    /// <summary>ペインの日本語表示名（ペイントグルのツールチップ用）。</summary>
    private static string PaneLabel(PaneKind kind) => kind switch
    {
        PaneKind.Terminal => "ターミナル",
        PaneKind.Editor => "エディタ",
        PaneKind.EditorSupport => "エディタサポート",
        PaneKind.Browser => "ブラウザ",
        PaneKind.Ai => "AI",
        PaneKind.Git => "Git",
        PaneKind.Diff => "Diff",
        PaneKind.Trace => "トレース",
        PaneKind.Debug => "IDE",
        _ => kind.ToString(),
    };

    /// <summary>ペインがツリーに在りかつ表示中か。</summary>
    private bool IsPaneVisible(PaneKind kind) => FindLeaf(kind) is { Hidden: false };

    /// <summary>表示中（非 Hidden）のリーフ数。</summary>
    private int VisibleLeafCount() => AllLeaves().Count(l => !l.Hidden);

    /// <summary>
    /// ペインの表示／非表示を切り替える。非表示にしてもリーフはツリーに残し
    /// <see cref="PaneLeaf.Hidden"/> を立てるだけなので、再表示で元の位置・比率に戻る。
    /// </summary>
    /// <remarks>
    /// 表示（<paramref name="visible"/>=true）は「リーフが無ければ最下段の新しい行へ追加／在れば元位置で再表示」
    /// であり、ステージモードは見ない（タイルツリーだけを操作する）。<b>結果やコンテンツをペインに出して
    /// 前面化する用途では <see cref="EnsurePaneVisibleOrSwapTopLeft"/>（左上ペインと入れ替え＋ステージ対応）の
    /// 利用を検討すること。</b>「AIに聞く」「ブラウザで調べる」「差分を開く」等はそちらに統一済み。
    /// この直接呼び出しが妥当なのは、非表示化（<paramref name="visible"/>=false）・明示トグル・
    /// セッション有効化のタイル復帰・専用位置への挿入後の表示など、左上入れ替えが不要な経路に限る。
    /// </remarks>
    private void SetPaneVisible(PaneKind kind, bool visible)
    {
        var leaf = FindLeaf(kind);
        var currentlyVisible = leaf is { Hidden: false };

        // Main に出るペインは必ず有効扱いにする（トグル以外の自動表示＝EditorSupport・ターミナル
        // セット等から呼ばれても「Main に出ている＝無効」という不整合を生まない）。隠す側では
        // 有効状態は変えない（隠れた有効セッションは袖へ回る）。
        if (visible)
            _enabledSessions.Add(kind);

        if (currentlyVisible == visible)
            return;

        CaptureLayoutSizes();

        if (visible)
        {
            if (leaf is null)
            {
                // 一度もツリーに置かれていないペイン。跨ぎ最大化中はモニタの継ぎ目を跨ぐ
                // 全幅の行ではなく、右端の列の最下段へ入れる。
                var newLeaf = NewLeaf(kind);
                if (_isSpanMaximized && _root is PaneSplit { Orientation: SplitKind.Columns } columns
                    && columns.Children.Count > 0)
                    columns.Children[^1] = AddLeafAtBottom(columns.Children[^1], newLeaf);
                else
                    AddLeafAtBottom(newLeaf);
            }
            else
                leaf.Hidden = false;
        }
        else
        {
            // 最後の1枚は隠さない
            if (VisibleLeafCount() <= 1)
                return;
            leaf!.Hidden = true;
            if (_focusedRegion?.Pane == kind)
                _focusedRegion = null; // 起点が消えたので次回ナビゲーションは可視ペインから選び直す
        }

        // 跨ぎ最大化中の表示切替は、解除時に戻す保存レイアウトへも反映する
        // （跨ぎ解除やスナップショット保存で表示状態が巻き戻らないように）。
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
        {
            if (AllLeaves(savedRoot).FirstOrDefault(l => l.Kind == kind) is { } savedLeaf)
                savedLeaf.Hidden = !visible;
            else if (visible)
                _spanSavedRoot = AddLeafAtBottom(savedRoot, NewLeaf(kind));
        }

        // EditorSupport を表示にしたら、現在のエディタ内容でプレビューを流し込む（自動開閉はしない）。
        if (kind == PaneKind.EditorSupport && visible)
            _ = UpdateEditorSupportAsync();

        _zoomedPane = null; // 表示構成が変わるのでズームは解除する
        _root = Normalize(_root);
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// ファイルを開くとき、Editor も EditorSupport もレイアウトに出ていなければ、左上のペインを
    /// 開く対象（バイナリ＝EditorSupport／テキスト＝Editor）へ差し替えて必ず見えるようにする。
    /// FolderTree・Diff・Git・検索・ターミナル/エディタのリンクなど、すべての「ファイルを開く」経路の
    /// 共通前処理として、呼び出し側がタブを活性化する前に呼ぶ。どちらかが既に見えていれば何もしない。
    /// </summary>
    private void EnsureEditorPaneForOpenedFile(string path)
    {
        var target = BinaryFileDetector.IsBinary(path) ? PaneKind.EditorSupport : PaneKind.Editor;

        if (_stageActive)
        {
            // ソロモード：Editor も EditorSupport も舞台に立っていなければ、対象を舞台へ立てる。
            if (!OnStage(PaneKind.Editor) && !OnStage(PaneKind.EditorSupport))
                SetStagePane(target);
            return;
        }

        // タイルモード：配置は設定 PaneOpenBehavior に従う（結果表示の EnsurePaneVisibleOrSwapTopLeft と同じ）。
        // 全モード共通：エディタ系（Editor/EditorSupport）がどちらか可視ならレイアウトの入れ替えはしない
        // （ファイル自体は呼び出し側が既存のエディタへ開く。ここは配置専用）。
        if (IsPaneVisible(PaneKind.Editor) || IsPaneVisible(PaneKind.EditorSupport))
            return;
        PlacePaneByBehavior(target);
    }

    /// <summary>
    /// 指定ペインがレイアウトに出ていなければ、必ず見えるように前面へ出す。ステージモード中は対象を舞台へ立てる。
    /// 既に見えていれば何もしない。「AIに聞く」「ブラウザで調べる」「差分を開く」のように、結果を表示するペインを
    /// 前面に出す経路で使う。タイルモードでの具体的な配置は設定 <see cref="AiSettings.PaneOpenBehavior"/> で切り替わる
    /// （<see cref="PaneOpenBehavior.Main"/>＝左上と入れ替え〔従来〕／<see cref="PaneOpenBehavior.Sub"/>＝右上と入れ替え／
    /// <see cref="PaneOpenBehavior.Loop"/>＝サブ表示・サブ起点ならメインへ繰り上げ）。
    /// </summary>
    private void EnsurePaneVisibleOrSwapTopLeft(PaneKind target)
    {
        if (_stageActive)
        {
            if (!OnStage(target))
                SetStagePane(target);
            return;
        }

        // 全モード共通：対象が既に画面に出ているならレイアウトは変えない（右上への組み替えもしない）。
        // 配置（左上／右上／繰り上げ）は対象が非表示のときだけ行う。
        if (IsPaneVisible(target))
            return;
        PlacePaneByBehavior(target);
    }

    /// <summary>タイルモードで対象ペインを設定 <see cref="AiSettings.PaneOpenBehavior"/> に従って前面へ配置する。
    /// 「AIに聞く」等の結果表示・ファイルを開く・袖ミニチュアのクリックの共通配置ロジック（可視時にその位置を
    /// 保つかどうかの main 判定だけは呼び出し側が事前に行う）。</summary>
    private void PlacePaneByBehavior(PaneKind target)
    {
        switch (_settings.PaneOpenBehavior)
        {
            case PaneOpenBehavior.Sub:
                PlaceIntoSubPane(target);
                break;
            case PaneOpenBehavior.Loop:
                PlaceIntoLoopPane(target);
                break;
            default:
                SwapIntoTopLeft(target);
                break;
        }
    }

    /// <summary>対象を左上（メイン）ペインと入れ替える（<see cref="PaneOpenBehavior.Main"/>＝従来の既定動作）。
    /// 左上が取れない／対象自身が左上のときは素直に表示する。</summary>
    private void SwapIntoTopLeft(PaneKind target)
    {
        if (TopLeftPane() is { } topLeft && topLeft != target)
            PlaceWingPane(target, topLeft, center: true, zone: null);
        else
            SetPaneVisible(target, true);
    }

    /// <summary>対象を右上（サブ）ペインと入れ替える（<see cref="PaneOpenBehavior.Sub"/>）。上段が横1枚しか
    /// なければ、左上ペインの右へ新しく追加してサブを作る。上段の左右判定はどちらも上段ノードから構造的に
    /// 求める（<see cref="TopRowLeftPane"/>／<see cref="TopRightPane"/>）ので、矩形未確定でも右へ入る。</summary>
    private void PlaceIntoSubPane(PaneKind target)
    {
        // 既に画面に出ているならレイアウトの入れ替えはしない（右上への組み替えもしない）。
        if (IsPaneVisible(target))
            return;

        var main = TopRowLeftPane();
        var sub = TopRightPane();
        if (sub is { } s && s != main)
            PlaceWingPane(target, s, center: true, zone: null);                // 右上と入れ替え
        else if (main is { } m && m != target)
            PlaceWingPane(target, m, center: false, zone: DropZone.Right);     // 横1枚 → 右に追加
        else
            SetPaneVisible(target, true);
    }

    /// <summary><see cref="PaneOpenBehavior.Loop"/>：基本はサブ（右上）へ出す。ただし操作の起点が現在の
    /// サブペインだった場合は、今サブにある内容をメイン（左上）へ繰り上げてから、対象を空いたサブへ出す
    /// （サブでの作業がメインへ繰り上がり、新しい結果はサブに来るベルトコンベア）。</summary>
    private void PlaceIntoLoopPane(PaneKind target)
    {
        // 既に画面に出ているならレイアウトの入れ替えはしない（繰り上げも含めて組み替えない）。
        if (IsPaneVisible(target))
            return;

        var main = TopRowLeftPane();
        var sub = TopRightPane();
        var originFromSub = _focusedRegion?.Pane is { } origin
            && sub is { } s && s != main && origin == s;

        if (originFromSub && main is { } m && sub is { } current && current != target)
        {
            // 現在のサブをメインの位置へ昇格（元のメインは袖へ退場）。上段は昇格した1枚だけになる。
            PlaceWingPane(current, m, center: true, zone: null);
            // その右へ対象を追加＝新しいサブにする。
            PlaceWingPane(target, current, center: false, zone: DropZone.Right);
        }
        else
        {
            PlaceIntoSubPane(target);
        }
    }

    /// <summary>再表示するペインを最下段の新しい行として追加する。</summary>
    private void AddLeafAtBottom(PaneLeaf leaf) => _root = AddLeafAtBottom(_root, leaf);

    /// <summary>指定ツリーの最下段の新しい行としてリーフを追加し、新しいルートを返す。
    /// 既存ノードを行スプリットで包む場合は外側の重み（親スプリット内の比率）を引き継ぐ。</summary>
    private static PaneNode AddLeafAtBottom(PaneNode? root, PaneLeaf leaf) => PaneLayoutTree.AddLeafAtBottom(root, leaf);
}

