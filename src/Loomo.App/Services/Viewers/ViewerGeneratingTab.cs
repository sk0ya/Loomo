using System.Windows.Automation;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Services.Viewers;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// そのままでは出せないファイルの Viewer を Claude Code が作っている（または、これから作る）タブ（§44.2「パスから開く」）。
/// 作り終えたら <see cref="Completed"/> を鳴らし、ペインが同じ場所を「その Viewer でそのファイルを開いたタブ」に差し替える。
/// <para>始め方は 3 つ：すぐ作る（人間がパスを渡した）／一言を聞いてから作る（「作り直す」）／作らずに待つ（復元したタブ——
/// 起動のたびに裏で Claude を回さない）。</para>
/// </summary>
internal sealed class ViewerGeneratingTab : ViewerTabBase
{
    internal enum StartMode { Now, AskNote, Wait }

    private enum Phase { Waiting, Asking, Running, Failed }

    private readonly ViewerStore _store;
    private readonly string _viewerId;
    private readonly bool _remake;
    private readonly DispatcherTimer _clock;
    private Phase _phase;
    private CancellationTokenSource? _cancel;
    private DateTime _startedAt;
    private TextBlock? _elapsed;
    private TextBox? _noteBox;
    private string _note = "";
    private string _message = "";

    public ViewerGeneratingTab(string targetPath, string viewerId, StartMode mode, ViewerStore store, Panel host, Dispatcher dispatcher)
        : base(targetPath, host, dispatcher)
    {
        _store = store;
        _viewerId = viewerId;
        _remake = mode == StartMode.AskNote;
        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateElapsed();
        _phase = mode switch { StartMode.AskNote => Phase.Asking, StartMode.Wait => Phase.Waiting, _ => Phase.Waiting };
        SetStatus(mode == StartMode.Now ? ViewerTabStatus.Generating : ViewerTabStatus.Stopped);
        if (mode == StartMode.Now)
            _ = GenerateAsync();
    }

    public string ViewerId => _viewerId;

    /// <summary>作り終えた（<c>true</c>＝できた・やめた作り直し＝元の Viewer に戻す）。</summary>
    public event EventHandler? Completed;

    public override string ToolTip => TargetPath + "\nClaude Code が Viewer を作っています";

    public override UIElement? BuildCard()
    {
        var target = TargetPath!;
        var kind = _viewerId == ViewerPath.GeneratedPrefix + "folder" ? "フォルダー" : ViewerTargets.ExtensionLabel(target);
        var panel = ViewerCards.Panel();
        switch (_phase)
        {
            case Phase.Running:
                panel.Children.Add(ViewerCards.Line($"Claude Code が {kind}を見せる Viewer を作っています…", "Fs14", "Fg", bold: true));
                panel.Children.Add(ViewerCards.Line($"「{ViewerTargets.NameOf(target)}」をもとに作り、できたらこのタブに出します。同じ種類のファイルは次から待たずに開けます。",
                    "Fs12", "FgDim"));
                panel.Children.Add(_elapsed = ViewerCards.Line(ElapsedText(), "Fs11", "FgDim", top: 8));
                panel.Children.Add(ViewerCards.Buttons(ViewerCards.Button("中止", "SecondaryButton", () => _cancel?.Cancel(), "ViewerGenerateCancelButton")));
                break;
            case Phase.Asking:
                panel.Children.Add(ViewerCards.Line($"{kind}の Viewer を作り直します", "Fs14", "Fg", bold: true));
                panel.Children.Add(ViewerCards.Line("どう直してほしいかを書いてください（空のままでも作り直せます）。", "Fs12", "FgDim"));
                _noteBox = new TextBox
                {
                    Text = _note,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 60,
                    MaxWidth = 600,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                AutomationProperties.SetName(_noteBox, "作り直しの頼みごと");
                _noteBox.KeyDown += (_, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Escape)
                    {
                        e.Handled = true;
                        Completed?.Invoke(this, EventArgs.Empty);
                    }
                };
                panel.Children.Add(_noteBox);
                panel.Children.Add(ViewerCards.Buttons(
                    ViewerCards.Button("作り直す", "FlatButton", () => _ = GenerateAsync(), "ViewerRemakeButton"),
                    ViewerCards.Button("やめる", "SecondaryButton", () => Completed?.Invoke(this, EventArgs.Empty))));
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _noteBox?.Focus());
                break;
            case Phase.Failed:
                panel.Children.Add(ViewerCards.Line("Viewer を作れませんでした", "Fs14", "Fg", bold: true));
                panel.Children.Add(ViewerCards.Mono(_message, "Fs11", "Fg", top: 6));
                panel.Children.Add(ViewerCards.Buttons(
                    ViewerCards.Button("もう一度", "FlatButton", () => _ = GenerateAsync()),
                    ViewerCards.Button("閉じる", "SecondaryButton", RequestClose)));
                break;
            default:
                panel.Children.Add(ViewerCards.Line($"{kind}を見せる Viewer がまだありません", "Fs14", "Fg", bold: true));
                panel.Children.Add(ViewerCards.Line("Claude Code に作ってもらうと、このタブに出ます（裏で動き、見えるターミナルには打ちません）。", "Fs12", "FgDim"));
                panel.Children.Add(ViewerCards.Mono(target, "Fs11", "FgDim", top: 8));
                panel.Children.Add(ViewerCards.Buttons(ViewerCards.Button("Claude Code に作ってもらう", "FlatButton", () => _ = GenerateAsync(), "ViewerGenerateButton")));
                break;
        }
        return panel;
    }

    private async Task GenerateAsync()
    {
        if (_phase == Phase.Running || Disposed)
            return;
        if (_noteBox is not null)
            _note = _noteBox.Text;
        _phase = Phase.Running;
        _startedAt = DateTime.Now;
        _clock.Start();
        SetStatus(ViewerTabStatus.Generating);
        using var cancel = _cancel = new CancellationTokenSource();
        ViewerGenerationResult result;
        try
        {
            var note = _remake || _note.Length > 0 ? _note : null;
            result = await Task.Run(() => ViewerGenerator.GenerateAsync(_store, TargetPath!, _viewerId, note, cancel.Token));
        }
        catch (OperationCanceledException)
        {
            result = new ViewerGenerationResult(false, "中止しました。");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 置き場を作れない・頼みごとを組めない など。「作っています…」のまま固めない。
            result = new ViewerGenerationResult(false, "Viewer を作る準備に失敗しました：" + ex.Message);
        }
        _cancel = null;
        _clock.Stop();
        if (Disposed)
            return;
        if (result.Ok)
        {
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }
        _message = result.Message;
        _phase = Phase.Failed;
        SetStatus(ViewerTabStatus.Broken);
    }

    private string ElapsedText()
    {
        var seconds = (int)(DateTime.Now - _startedAt).TotalSeconds;
        return seconds < 60 ? $"{seconds} 秒" : $"{seconds / 60} 分 {seconds % 60} 秒";
    }

    private void UpdateElapsed()
    {
        if (_elapsed is not null)
            _elapsed.Text = ElapsedText();
    }

    protected override void OnShownChanged() { }
    protected override void Unmount() { }

    public override void Dispose()
    {
        _clock.Stop();
        _cancel?.Cancel();
        Completed = null;
        base.Dispose();
    }
}
