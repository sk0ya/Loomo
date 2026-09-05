namespace sk0ya.Loomo.Core.Models;

/// <summary>機能をペインに前面表示するときの配置方法。</summary>
public enum PaneOpenBehavior
{
    /// <summary>左上のメインペインと入れ替える。</summary>
    Main,
    /// <summary>サブペインと入れ替える。サブがまだ無ければメインの隣（<see cref="PaneSubDirection"/> が
    /// 横なら右・縦なら下）へ追加する。</summary>
    Sub,
    /// <summary>サブを起点にした場合は既存内容をメインへ送り、空いたサブへ表示する。</summary>
    Loop
}
