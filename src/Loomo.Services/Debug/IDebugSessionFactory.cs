using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>新しい（未起動の）<see cref="IDebugService"/> インスタンスを作る。<see cref="NetcoredbgDebugService"/> は
/// 「1 インスタンス＝1 デバッグセッション」の作りなので、複数セッションを同時に持つには、この工場でセッションの数だけ
/// 別々のインスタンスを作る（DI からは単一の <see cref="IDebugService"/> を注入しない）。</summary>
public interface IDebugSessionFactory
{
    /// <summary>まだ何も起動していない、新しい <see cref="IDebugService"/> を作る。</summary>
    IDebugService Create();
}

/// <summary>netcoredbg（DAP）向けの既定実装。<see cref="NetcoredbgDebugService"/> はコンストラクタ引数が無いので、
/// 生成するだけの薄いラッパー。</summary>
public sealed class NetcoredbgDebugSessionFactory : IDebugSessionFactory
{
    public IDebugService Create() => new NetcoredbgDebugService();
}
