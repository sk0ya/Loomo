using System;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace sk0ya.Loomo.App.Detach;

/// <summary>別ウィンドウ（<see cref="DetachedPaneWindow"/>）のタブ1つ。ホストする実コントロールと、
/// タブ表示用のタイトル／アイコン、破棄処理（同期購読解除・セッションクローズ等）を持つ。
/// タブはウィンドウ間を移動できる（<see cref="DetachedWindowManager"/> が再ペアレントする）。</summary>
internal sealed partial class DetachedItem : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public DetachKind Kind { get; }

    /// <summary>ウィンドウの <c>ContentHost</c> に載せる実コントロール（1インスタンス）。</summary>
    public FrameworkElement Content { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private ImageSource? _icon;
    /// <summary>所属ウィンドウ内でアクティブ表示中か（タブ強調・可視制御に使う）。</summary>
    [ObservableProperty] private bool _isActive;

    private readonly Action? _dispose;
    private bool _disposed;

    public DetachedItem(
        DetachKind kind,
        string title,
        FrameworkElement content,
        ImageSource? icon = null,
        Action? dispose = null)
    {
        Kind = kind;
        _title = title;
        Content = content;
        _icon = icon;
        _dispose = dispose;
    }

    /// <summary>同期購読の解除・生成したセッション/WebView2 の解放を1度だけ実行する。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _dispose?.Invoke();
    }
}
