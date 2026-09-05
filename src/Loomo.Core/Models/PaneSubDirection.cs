namespace sk0ya.Loomo.Core.Models;

/// <summary>サブペインをメインのどちら側に置くか（＝メインとサブを横に並べるか縦に並べるか）。
/// <see cref="PaneOpenBehavior.Sub"/>／<see cref="PaneOpenBehavior.Loop"/> の配置先に効く。</summary>
public enum PaneSubDirection
{
    /// <summary>横に並べる：サブはメインの右（従来動作）。</summary>
    Horizontal,
    /// <summary>縦に並べる：サブはメインの下。</summary>
    Vertical
}
