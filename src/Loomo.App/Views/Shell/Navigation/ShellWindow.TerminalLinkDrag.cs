using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナルのリンクを Ctrl+ドラッグで他のペインへ運ぶ（§24.52）。運ぶ中身は Terminal パッケージが
/// 文字列（と Web の URL）で用意し、ここではファイルのリンクを解決して添える——エディタへ落とせばその行で開き、
/// ほかのアプリやエクスプローラーへはファイルとして渡る。解決はクリック（<see cref="OnTerminalLinkActivated"/>）と同じ。</summary>
public partial class ShellWindow {
    private void OnTerminalLinkDragStarting(object? sender, TerminalHyperlinkDragEventArgs e) {
        var target = TerminalLinkTargetResolver.Resolve(
            _workspace, e.Target, (sender as TerminalTabView)?.WorkingDirectory);
        if (target.Kind != LinkOpenTargetKind.File || !File.Exists(target.Value))
            return;
        FileDragDrop.SetPaths(e.Data, [target.Value]);
        if (target.Line > 0)
            FileDragDrop.SetSourceLocation(e.Data, target.Line, target.Column);
    }
}
