namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 無題のエディタタブの言語を本文から決める（§24.28、Zed の untitled バッファの言語自動判定）。
/// 拡張子が無いので、貼り付けた JSON も書き始めた C# も色が付かなかった。
/// <para>打鍵のたびには判定しない——手が止まってから（<see cref="UntitledDetectDelay"/>）一度だけ本文を読む
/// （§31.15：打鍵の経路で本文まるごとの写しを作らない）。推定は <see cref="UntitledLanguageDetector"/>（頭の行だけを見る）。
/// 分からないときは今の色付けを保つ（一度付いた色を、書きかけで消したり付けたりしない）。
/// 名前を付けて保存すれば、以後はエディタが拡張子から決める。</para>
/// </summary>
public partial class ShellWindow
{
    private static readonly TimeSpan UntitledDetectDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>無題のタブに判定を仕掛ける。ファイルを読み込んだら（パスが付いたら）自分で外れる。</summary>
    private void AttachUntitledLanguageDetection(VimEditorControl control)
    {
        if (!string.IsNullOrEmpty(control.FilePath) || control.IsVirtualDocument) return;
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = UntitledDetectDelay };
        EventHandler<EditorTextEditedEventArgs>? onEdited = null;
        void Detach()
        {
            timer.Stop();
            control.TextEdited -= onEdited;
        }
        onEdited = (_, _) =>
        {
            if (!string.IsNullOrEmpty(control.FilePath) || control.IsVirtualDocument)
            {
                Detach();
                return;
            }
            timer.Stop();
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!string.IsNullOrEmpty(control.FilePath) || control.IsVirtualDocument)
            {
                Detach();
                return;
            }
            DetectUntitledLanguage(control);
        };
        control.TextEdited += onEdited;
        control.Unloaded += (_, _) => timer.Stop();
        // 復元した無題タブは本文を持って始まるので、編集を待たずに一度決める。
        if (control.DocumentInfo.LineCount > 1 || control.Text.Length > 0)
            DetectUntitledLanguage(control);
    }

    private static void DetectUntitledLanguage(VimEditorControl control)
    {
        if (UntitledLanguageDetector.Detect(control.Text) is { } language &&
            !string.Equals(control.DocumentInfo.Language, language, StringComparison.Ordinal))
            control.SetSyntaxLanguage(language);
    }
}
