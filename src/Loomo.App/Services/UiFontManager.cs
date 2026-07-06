using System;
using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>アプリ UI 全体の基準フォントサイズの適用。
/// UI のフォントサイズは <c>Themes/Typography.xaml</c> のトークン（<c>Fs8</c>…<c>Fs20</c>）を
/// DynamicResource で参照している。ユーザーは「基準フォントサイズ（本文＝<c>Fs13</c> 相当の px）」を選び、
/// ここで各トークンを「等倍px × (基準サイズ / <see cref="ReferenceSize"/>)」に計算して
/// <see cref="Application.Resources"/> 直下へ同じキーで置く（マージ辞書より直下のキーが優先されるため、
/// スタイル・ビューを問わず即時反映される）。文字の大小関係（見出し／補足など）は比率を保つ。
/// 配色を差し替える <see cref="ThemeManager"/> と同じ流儀。
///
/// 対象は WPF で組んだアプリ UI のみ。エディタ／ターミナルは各コントロールが持つ独自のフォントサイズ
/// 設定（<see cref="sk0ya.Loomo.Ai.AppearanceSettings.EditorFontSize"/> など）で別管理し、この設定とは連動しない。
/// Markdown プレビュー／ブラウザは WebView2 側の CSS のため、そもそもこのトークンの影響を受けない。</summary>
public sealed class UiFontManager
{
    /// <summary>基準となる本文サイズ（等倍時の <c>Fs13</c>）。ユーザー指定サイズはこれを基準に比率化する。</summary>
    public const double ReferenceSize = 13;

    /// <summary>初期の基準フォントサイズ（未設定時）。既定を等倍(13)より少し大きくして標準で読みやすくする
    /// （従来の 1.2 倍相当 ≒ 16px）。</summary>
    public const double DefaultSize = 16;

    /// <summary>基準フォントサイズの許容範囲。UI のプリセットもこの範囲内に収める。</summary>
    public const double MinSize = 10;
    public const double MaxSize = 28;

    /// <summary>Typography.xaml のトークン（キー→等倍px）。トークンを増やすときは両方に足す。
    /// 小数の px はキー名の <c>.</c> を <c>_</c> に置き換える（例: 11.5px → <c>Fs11_5</c>）。</summary>
    private static readonly (string Key, double Base)[] Tokens =
    {
        ("Fs8", 8), ("Fs9", 9), ("Fs10", 10), ("Fs11", 11), ("Fs11_5", 11.5),
        ("Fs12", 12), ("Fs12_5", 12.5), ("Fs13", 13), ("Fs14", 14), ("Fs15", 15), ("Fs20", 20),
    };

    /// <summary>直近に適用した倍率（基準サイズ / <see cref="ReferenceSize"/>）。
    /// コードビハインドで生成する UI（<see cref="Scaled"/>）が参照する。</summary>
    public static double CurrentScale { get; private set; } = 1.0;

    /// <summary>基準サイズを <see cref="MinSize"/>〜<see cref="MaxSize"/> に丸める。0 以下は「未設定」とみなし既定を返す。</summary>
    public static double Effective(double stored) =>
        stored > 0 ? Math.Clamp(stored, MinSize, MaxSize) : DefaultSize;

    /// <summary>等倍pxに現在の倍率を掛けて返す。XAML を通さずコードで UI を組む箇所用
    /// （生成時点の倍率で確定するため、設定変更後に開き直すと反映される）。</summary>
    public static double Scaled(double baseSize) => Math.Round(baseSize * CurrentScale, 2);

    /// <summary>基準フォントサイズを適用する。各トークンを「等倍px × 倍率」でアプリ直下へ上書きする。</summary>
    public void Apply(double baseFontSize)
    {
        var size = Math.Clamp(baseFontSize <= 0 ? DefaultSize : baseFontSize, MinSize, MaxSize);
        CurrentScale = size / ReferenceSize;

        var app = Application.Current;
        if (app is null) return;
        foreach (var (key, b) in Tokens)
            app.Resources[key] = Math.Round(b * CurrentScale, 2);
    }
}
