using System;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Detach;

/// <summary>切り離したタブを<b>メイン窓のタブ帯へ戻す</b>ときの受け入れ先と戻し方。
/// <see cref="Kind"/> はどのペインの帯が受けるか（Editor のタブは Editor の帯だけが受ける）。
/// <see cref="Apply"/> は切り離しウィンドウから外れた<b>後</b>に呼ばれ、メイン側のタブとして迎える処理と、
/// 迎えられない実体（ブラウザは再ペアレントすると空表示になるので作り直す）の後始末まで持つ。</summary>
internal sealed record DetachReturn(TabEntryKind Kind, Action Apply);

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

    /// <summary>メイン窓のタブ帯へ戻せるならその戻し方（null＝戻せない。Diff・EditorSupport は
    /// 対応するタブ帯がメインに無い）。</summary>
    public DetachReturn? Return { get; init; }

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
