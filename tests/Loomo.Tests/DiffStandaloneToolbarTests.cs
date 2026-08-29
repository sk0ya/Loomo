using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 切り離しウィンドウ（Git のコミット詳細のダブルクリック／Diff ペインが隠れているときの
/// 差分の行き先）のツールバー。
///
/// <para>Diff の操作はペインヘッダー（ShellWindow）へ集約されているため、ヘッダーを持たない
/// 切り離しウィンドウでは「次/前の差分」「エディタで開く」等がまるごと消えていた。ビュー自前の
/// バーで補うのがこのテストの対象で、固定したいのは2点——(1) ペインでは出ないこと（同じボタンが
/// 二段になる）、(2) 切り離し時には主要操作が VM のコマンドに結線されて出ること。</para>
/// </summary>
[Collection(WpfViewTests.Name)]
public sealed class DiffStandaloneToolbarTests
{
    private readonly WpfViewHost _host;

    public DiffStandaloneToolbarTests(WpfViewHost host) => _host = host;

    [Fact]
    public void ツールバーは既定で隠れ切り離しホストでだけ出る()
    {
        _host.Run(() =>
        {
            var view = new DiffSessionView();
            var bar = (FrameworkElement)view.FindName("StandaloneToolbar")!;

            Assert.Equal(Visibility.Collapsed, bar.Visibility);   // ペイン：ヘッダー側が持っている

            view.ShowStandaloneToolbar();

            Assert.Equal(Visibility.Visible, bar.Visibility);
        });
    }

    [Fact]
    public void 切り離しツールバーは差分ジャンプとエディタで開くを持つ()
    {
        _host.Run(() =>
        {
            var view = new DiffSessionView();
            view.ShowStandaloneToolbar();
            var bar = (FrameworkElement)view.FindName("StandaloneToolbar")!;

            var commands = Descendants(bar).OfType<ButtonBase>()
                .Select(b => BindingOperations.GetBinding(b, ButtonBase.CommandProperty)?.Path.Path)
                .Where(p => p is not null)
                .ToList();

            Assert.Contains("JumpToNextChangeCommand", commands);
            Assert.Contains("JumpToPrevChangeCommand", commands);
            Assert.Contains("OpenInEditorCommand", commands);
            Assert.Contains("OpenCommitInGitCommand", commands);

            // 表示形式（左右/統合・テキスト/描画）の切替もヘッダー側にしか無かった
            var toggles = Descendants(bar).OfType<RadioButton>()
                .Select(r => BindingOperations.GetBinding(r, ToggleButton.IsCheckedProperty)?.Path.Path)
                .ToList();
            Assert.Contains("IsSideBySide", toggles);
            Assert.Contains("IsMarkdownRender", toggles);
        });
    }

    /// <summary>この窓は「Diff ペインが隠れているときの差分の行き先」でもある（§24.5.2）＝
    /// git の差分もアドホック比較もここへ来る。ペインヘッダーにしか無かった操作
    /// （ソース切替・比較の入替/再比較/閉じる・変更を破棄・作業ツリーへ戻す）が窓でも要るのはそのため。</summary>
    [Fact]
    public void 切り離しツールバーは比較と作業ツリーの操作も持つ()
    {
        _host.Run(() =>
        {
            var view = new DiffSessionView();
            view.ShowStandaloneToolbar();
            var bar = (FrameworkElement)view.FindName("StandaloneToolbar")!;

            var commands = Descendants(bar).OfType<ButtonBase>()
                .Select(b => BindingOperations.GetBinding(b, ButtonBase.CommandProperty)?.Path.Path)
                .Where(p => p is not null)
                .ToList();

            Assert.Contains("SwapComparisonCommand", commands);
            Assert.Contains("RecompareWithClipboardCommand", commands);
            Assert.Contains("CloseComparisonCommand", commands);
            Assert.Contains("DiscardCommand", commands);
            Assert.Contains("ClearGitTargetCommand", commands);

            // ソース（Git／比較）の切替そのものもヘッダー側にしか無かった
            var toggles = Descendants(bar).OfType<RadioButton>()
                .Select(r => BindingOperations.GetBinding(r, ToggleButton.IsCheckedProperty)?.Path.Path)
                .ToList();
            Assert.Contains("IsGitMode", toggles);
            Assert.Contains("IsCompareMode", toggles);
        });
    }

    /// <summary>ボタンが視界の外でも手が届くよう、次/前の変更はキーでも叩ける（ペイン・窓の共通）。</summary>
    [Fact]
    public void 次と前の変更はキーからも叩ける()
    {
        _host.Run(() =>
        {
            var view = new DiffSessionView();

            var keys = view.InputBindings.OfType<KeyBinding>()
                .ToDictionary(
                    k => (k.Key, k.Modifiers),
                    k => ((Binding)BindingOperations.GetBinding(k, InputBinding.CommandProperty)!).Path.Path);

            Assert.Equal("JumpToNextChangeCommand", keys[(Key.F8, ModifierKeys.None)]);
            Assert.Equal("JumpToPrevChangeCommand", keys[(Key.F8, ModifierKeys.Shift)]);
            Assert.Equal("JumpToNextChangeCommand", keys[(Key.Down, ModifierKeys.Alt)]);
            Assert.Equal("JumpToPrevChangeCommand", keys[(Key.Up, ModifierKeys.Alt)]);
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}
