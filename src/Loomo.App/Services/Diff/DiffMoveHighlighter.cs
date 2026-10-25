using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 統合表示の行へ「移動したブロック」の印を配る（§24.70・UI 非依存）。判定は <see cref="DiffMoveDetector"/>。
/// git の差分の行はパッチの 1 文字を頭に持つ（<see cref="DiffRowVm.Prefixed"/>）ので、剥がしてから比べる。
/// </summary>
internal static class DiffMoveHighlighter
{
    /// <summary>移動の無い差分。</summary>
    internal static IReadOnlyList<DiffMove?> None { get; } = Array.Empty<DiffMove?>();

    internal static IReadOnlyList<DiffMove?> ForUnified(IReadOnlyList<DiffRowVm> rows)
    {
        if (rows.Count == 0 || rows.Count > DiffMoveDetector.MaxRows)
            return None;
        var input = new (DiffMoveSide, string)[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var side = rows[i].Kind switch
            {
                "Removed" => DiffMoveSide.Removed,
                "Added" => DiffMoveSide.Added,
                _ => DiffMoveSide.None,
            };
            var text = rows[i].Text;
            input[i] = (side, rows[i].Prefixed && text.Length > 0 ? text[1..] : text);
        }
        var moves = DiffMoveDetector.Detect(input);
        return moves.Any(m => m is not null) ? moves : None;
    }

    /// <summary>移動の行の行内差分を外す。移動の行は相手（別の場所）と同じ中身なので、同じかたまりの無関係な行と対にした範囲は嘘になる。</summary>
    internal static IReadOnlyList<IReadOnlyList<TextSpan>?> WithoutMovedInline(
        IReadOnlyList<IReadOnlyList<TextSpan>?> inline, IReadOnlyList<DiffMove?> moves)
    {
        if (moves.Count == 0 || inline.Count == 0)
            return inline;
        var result = new IReadOnlyList<TextSpan>?[inline.Count];
        for (var i = 0; i < inline.Count; i++)
            result[i] = i < moves.Count && moves[i] is not null ? null : inline[i];
        return result;
    }

    /// <summary>移動の行のツールチップ。</summary>
    internal static string ToolTip(DiffMove move)
        => move.IsSource
            ? $"移動したブロック（{move.Length} 行）の移動元——ここで消え、別の場所に同じ中身が足されています。ダブルクリックで移動先へ。"
            : $"移動したブロック（{move.Length} 行）の移動先——別の場所から同じ中身が移ってきました。ダブルクリックで移動元へ。";
}
