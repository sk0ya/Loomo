using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using sk0ya.Loomo.App.Input;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// キーボードショートカットを「実際に押して」割り当てる小コントロール。クリックでキャプチャを開始し、
/// 押されたキーから <see cref="KeySequence"/> を組み立てて、DataContext の
/// <see cref="KeybindingRowViewModel"/> へ適用する。修飾子のみの押下は確定せず待ち、1 打目の後
/// 短時間（<see cref="ChordTimeoutMs"/>）内に 2 打目が来れば連鎖（最大 2）として扱う。Esc で取消。
/// （Esc 自体は取消に割り当てているため、キャプチャでは割り当てられない。）
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
        Display.Visibility = Visibility.Collapsed;
        Prompt.Visibility = Visibility.Visible;
        Keyboard.Focus(this);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
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
        RestartTimer();
    }

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
