using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// キーボードショートカットを「実際に押して」割り当てる小コントロール。クリック（またはフォーカスして
/// Enter／Space）でキャプチャを開始し、押されたキーから <see cref="KeySequence"/> を組み立てて、
/// DataContext の <see cref="KeybindingRowViewModel"/> へ適用する。修飾子のみの押下は確定せず待ち、
/// 1 打目の後 短時間（<see cref="ChordTimeoutMs"/>）内に 2 打目が来れば連鎖（最大 2）として扱う。
/// 待っている間は押し終えた分を表示するので、連鎖の 1 打目が入ったかどうかが見える。Esc で取消。
/// （Esc 自体は取消に割り当てているため、キャプチャでは割り当てられない。）
/// キャプチャ中は行の <see cref="KeybindingRowViewModel.IsCapturing"/> を立てる——枠の強調と、
/// 行のボタン（既定に戻す／未割当にする）の抑止を XAML 側のトリガに任せるため。
/// </summary>
public partial class KeyCaptureBox : UserControl
{
    private const int ChordTimeoutMs = 700;

    private readonly List<KeyChord> _buffer = new();
    private DispatcherTimer? _timer;
    private bool _capturing;

    public KeyCaptureBox()
    {
        InitializeComponent();
    }

    private KeybindingRowViewModel? Row => DataContext as KeybindingRowViewModel;

    private void OnStart(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        StartCapture();
    }

    private void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        _buffer.Clear();
        if (Row is { } row) row.IsCapturing = true;
        Display.Visibility = Visibility.Collapsed;
        Prompt.Visibility = Visibility.Visible;
        UpdatePrompt();
        Keyboard.Focus(this);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            // 未開始：Enter／Space で開始（マウスなしでも割り当てられるように）。他のキーは
            // Tab 移動などのため素通しする。
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                StartCapture();
            }
            return;
        }

        e.Handled = true;

        // Esc は常に取消（押すと取消なので Esc 自体は割り当てられない）。
        if (e.Key == Key.Escape)
        {
            CancelCapture();
            return;
        }

        if (KeyChord.FromEvent(e) is not { } chord) return; // 修飾子のみ：確定を待つ

        _buffer.Add(chord);
        if (_buffer.Count >= KeySequence.MaxChords)
        {
            Commit();
            return;
        }

        // 2 打目を短時間待つ（来なければ単一として確定）。
        UpdatePrompt();
        RestartTimer();
    }

    /// <summary>キャプチャ中の表示を「押した分＋続きの促し」に更新する。</summary>
    private void UpdatePrompt()
        => Prompt.Text = _buffer.Count == 0
            ? "キーを押す…"
            : string.Join(" ", _buffer.Select(c => c.Format())) + " …";

    private void RestartTimer()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ChordTimeoutMs) };
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e) => Commit();

    private void Commit()
    {
        StopTimer();
        if (!_capturing) return;
        var sequence = _buffer.Count > 0 ? new KeySequence(_buffer.ToArray()) : null;
        ResetUi();
        Row?.ApplyCapture(sequence);
    }

    private void CancelCapture()
    {
        StopTimer();
        ResetUi();
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturing) CancelCapture();
    }

    private void ResetUi()
    {
        _capturing = false;
        _buffer.Clear();
        if (Row is { } row) row.IsCapturing = false;
        Prompt.Visibility = Visibility.Collapsed;
        Display.Visibility = Visibility.Visible;
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
