using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Services.Debug.Js;

/// <summary>js-debug（vscode-js-debug）向けの <see cref="IDebugSessionFactory"/>。「1 インスタンス＝1 セッション」の
/// 作りは netcoredbg と同じ。DI には<b>具象型で</b>登録する（<see cref="IDebugSessionFactory"/> の既定登録は
/// netcoredbg のまま——dotnet 用 IDE ペインと TS IDE ペインで工場を使い分けるため）。</summary>
public sealed class JsDebugSessionFactory : IDebugSessionFactory
{
    public IDebugService Create() => new JsDebugService();
}
