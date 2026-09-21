using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

public partial class AiBarView : UserControl
{
    private AiTranscriptScrollController _transcriptController = null!;
    private AiBarInputController _inputController = null!;

    public AiBarView()
    {
        InitializeComponent();
        _transcriptController = new AiTranscriptScrollController(TranscriptScrollViewer);
        _inputController = new AiBarInputController(InputBox, () => DataContext as AiBarViewModel);

        Loaded += (_, _) => _transcriptController.OnLoaded(DataContext as AiBarViewModel);
        Unloaded += (_, _) => _transcriptController.OnUnloaded();
        DataContextChanged += (_, e) =>
            _transcriptController.AttachViewModel(e.NewValue as AiBarViewModel);
    }

    /// <summary>AI入力欄へキーボードフォーカスを移す（ペイン間ナビゲーション用）。</summary>
    public void FocusInput() => _inputController.FocusInput();

    private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
        => _transcriptController.OnScrollChanged(e);

    private void OnTranscriptPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => _transcriptController.ForwardMouseWheel(sender, e);

    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
        => _inputController.OnPreviewKeyDown(e);

    private void OnCommandListClick(object sender, MouseButtonEventArgs e)
        => _inputController.AcceptSelectedCommand();
}
