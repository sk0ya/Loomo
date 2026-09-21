using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

// Git競合解決画面（DiffSessionView）の Ours/Theirs ペイン用。RichTextBox.Document は Binding を直接
// 設定できない（DependencyProperty だが XAML の Binding 経由の設定を拒む）ため、SearchHighlightBehavior
// と同じ考え方で「行データが変わるたびに Document を組み直す」添付プロパティにする。
// Mode は "Gutter"（行番号のみ）/ "Ours"（本文・差分は緑）/ "Theirs"（本文・差分は赤）のいずれか。
public static class ConflictSideDocumentBehavior
{
    public static readonly DependencyProperty LinesProperty =
        DependencyProperty.RegisterAttached(
            "Lines", typeof(IReadOnlyList<ConflictSideLineVm>), typeof(ConflictSideDocumentBehavior),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode", typeof(string), typeof(ConflictSideDocumentBehavior),
            new PropertyMetadata(null, OnChanged));

    public static IReadOnlyList<ConflictSideLineVm>? GetLines(DependencyObject obj) =>
        (IReadOnlyList<ConflictSideLineVm>?)obj.GetValue(LinesProperty);
    public static void SetLines(DependencyObject obj, IReadOnlyList<ConflictSideLineVm>? value) =>
        obj.SetValue(LinesProperty, value);

    public static string? GetMode(DependencyObject obj) => (string?)obj.GetValue(ModeProperty);
    public static void SetMode(DependencyObject obj, string? value) => obj.SetValue(ModeProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box) return;
        box.Document = ConflictSideFlowDocumentRenderer.Build(GetLines(box), GetMode(box));
    }
}
