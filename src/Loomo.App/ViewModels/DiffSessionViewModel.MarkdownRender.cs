using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Core.Markdown;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// DiffSessionViewModel の「Markdown 差分のレンダリング表示」パート（設計書 §24.10）。
/// 対象が Markdown のときだけ、生のパッチ行ではなく<b>レンダリングした文書の上で</b>差分を読ませる。
///
/// <para>表示モードの選択は<b>セッション内だけ</b>持つ（設定にも復元にも出さない）。差分の見方は
/// 「今このファイルをどう読みたいか」であってワークスペースの状態ではないため。ファイルを選び直しても
/// トグルは倒れたままにしてある——Markdown を続けて見るときに毎回押し直させないため。Markdown 以外へ
/// 移ると <see cref="CanRenderMarkdown"/> が false になり、モードは自動的に効かなくなる。</para>
/// </summary>
public sealed partial class DiffSessionViewModel
{
    /// <summary>レンダリング表示にするか（ヘッダーのトグル）。Markdown 以外では効かない。</summary>
    [ObservableProperty] private bool _isMarkdownRender;

    /// <summary>レンダリング表示を出せないときの理由（大きすぎる・差分が無い）。空なら出さない。</summary>
    [ObservableProperty] private string _markdownRenderNotice = "";

    /// <summary>レンダリング差分のページ HTML。ビューはこの変化で WebView2 を実体化して表示する
    /// （null＝出せなかった／モードが効いていない）。</summary>
    [ObservableProperty] private string? _markdownRenderHtml;

    /// <summary>相対パス画像を解決するためのマップ先フォルダー（<c>preview.loomo</c> 仮想ホストの実体）。
    /// マルチルート：そのファイルを担当するワークスペースフォルダー基準で決める。
    /// <b>HTML より先に入れる</b>（ビューは HTML の変化を合図に、そのあとでこれを読む）。</summary>
    public string MarkdownRenderMapFolder { get; private set; } = "";

    /// <summary>選択中のファイルはレンダリング表示できるか（＝Markdown で、コンフリクト解消表示中でない）。
    /// ヘッダーのトグルはこれが false のときは<b>出さない</b>（押せるのに何も起きない項目を作らない・§24.7）。
    /// <b>コンフリクト中を外すのが要点</b>——本文グリッドごと Ours/Result/Theirs へ置き換わっているので、
    /// トグルだけ出しても押した先に描く場所が無い。</summary>
    public bool CanRenderMarkdown
        => MarkdownBlockDiff.IsMarkdownPath(SelectedFile?.FullPath) && !Conflict.IsConflictMode;

    /// <summary>いまレンダリング表示が効いているか（トグルが倒れていて、対象が Markdown）。</summary>
    public bool IsMarkdownRenderActive => IsMarkdownRender && CanRenderMarkdown;

    /// <summary>「左右／統合」の切り替えを出すか。レンダリング表示中は<b>出さない</b>——
    /// 押すと差分の読み直し（レンダリング全体の作り直し）だけが走って、画面は何も変わらない。</summary>
    public bool CanChooseTextLayout => !IsMarkdownRenderActive;

    /// <summary>統合テキスト差分を出すか（レンダリング表示中はテキスト側を丸ごと退ける）。</summary>
    public bool ShowUnifiedText => !IsSideBySide && !IsMarkdownRenderActive;

    /// <summary>左右並びテキスト差分を出すか。</summary>
    public bool ShowSideText => IsSideBySide && !IsMarkdownRenderActive;

    partial void OnIsMarkdownRenderChanged(bool value)
    {
        NotifyMarkdownRenderState();
        _ = LoadAndAutoJumpAsync(SelectedFile);
    }

    /// <summary>表示モードに関わる派生プロパティをまとめて通知する（選択・表示形式・モードのどれが
    /// 変わっても効き方が変わるので、1か所に集めて呼び分けない）。</summary>
    private void NotifyMarkdownRenderState()
    {
        OnPropertyChanged(nameof(CanRenderMarkdown));
        OnPropertyChanged(nameof(IsMarkdownRenderActive));
        OnPropertyChanged(nameof(CanChooseTextLayout));
        OnPropertyChanged(nameof(ShowUnifiedText));
        OnPropertyChanged(nameof(ShowSideText));
    }

    /// <summary>この読込でレンダリング表示を使うか。<b>選択中の項目ではなく読込対象の項目</b>で判断する
    /// ——読込の途中で選択が変わっても、この1回の組み立ての前提はぶれない。</summary>
    private bool UseMarkdownRender(DiffFileItem? item)
        => IsMarkdownRender && MarkdownBlockDiff.IsMarkdownPath(item?.FullPath);

    /// <summary>レンダリング差分を組み立てる（差分の計算も HTML 化も UI スレッドの外）。</summary>
    private async Task<MarkdownDiffRender> BuildMarkdownRenderAsync(DiffFileItem item)
    {
        IReadOnlyList<DiffLine> lines;
        if (item.Comparison is { } comparison)
        {
            var (oldText, newText) = (comparison.LeftText, comparison.RightText);
            lines = await Task.Run(() => DiffUtil.ComputeFull(oldText, newText));
        }
        else
        {
            // 全文コンテキストのパッチを使う（ハンクだけだと畳まれた文脈行が抜け、Markdown として別物になる）。
            var patch = await GetPatchTextAsync(item, FullFileContext);
            if (patch.Length == 0)
                return new MarkdownDiffRender(null, NoDiffMessage);
            lines = await Task.Run(() => DiffUtil.FromUnifiedPatch(patch));
            // パッチはあるのに1行も取れない＝ハンクが無い（git のエラーメッセージ・読み取り失敗の注記など）。
            // 「差分はありません」と言い切ると理由が消えるので分ける——テキスト差分なら原文がそのまま読める。
            if (lines.Count == 0)
                return new MarkdownDiffRender(null, UnparsablePatchMessage);
        }

        var path = item.FullPath;
        // マルチルート：相対パス画像の基準は、そのファイルを担当するワークスペースフォルダー。
        var (mapFolder, baseHref) = MarkdownPreviewPaths.Resolve(_workspace.FolderForOrPrimary(path), path);
        var title = $"差分: {Path.GetFileName(path)}";
        // 配色はプレビューと同じ設定から引く。読むのは<b>組み立てのときだけ</b>なので、外観設定の変更は
        // 次のファイル切替／モード切替（＝次の組み立て）から効く。
        var theme = _settings.Appearance.MarkdownPreviewTheme;
        var render = await Task.Run(() => MarkdownDiffPage.Build(lines, title, theme, baseHref, NoDiffMessage));
        return render with { MapFolder = mapFolder };
    }

    /// <summary>パッチを差分として解釈できなかったときの文言（差分が無いのとは別物）。</summary>
    private const string UnparsablePatchMessage =
        "差分を解釈できませんでした。テキスト差分に切り替えると git の出力がそのまま読めます。";

    /// <summary>組み立て結果を反映する。<b>HTML を最後に入れる</b>——ビューは HTML の変化で描き直すので、
    /// マップ先や理由を先に置いておかないと1回ぶん古い基準で描いてしまう。</summary>
    private void ApplyMarkdownRender(MarkdownDiffRender render)
    {
        MarkdownRenderMapFolder = render.MapFolder;
        MarkdownRenderNotice = render.Notice;
        MarkdownRenderHtml = render.Html;
    }
}
