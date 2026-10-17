namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタのガター（変更マーク）の比べる相手を共有の比較基準に合わせる（§24.11.1）。</summary>
public partial class ShellWindow
{
    private readonly EditorGutterBaseTracker _gutterBase;

    /// <summary>比べる相手が変わったので、実体化済みのタブのガターを引き直す。未実体化のタブは
    /// 実体化のときに今の相手で引くので触らない（ここで実体化させると起動の遅延読み込みが崩れる）。</summary>
    private void RefreshRealizedEditorGutters()
    {
        foreach (var tab in _editorTabs.ToArray())
        {
            if (tab.IsRealized && !string.IsNullOrEmpty(tab.Control.FilePath))
                tab.Control.RefreshGitDiff();
        }
    }
}
