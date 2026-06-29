using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Rendering;
using Terminal.Settings;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: エディタタブを開く・プレビュータブの使い回し（新規タブ・仮想ドキュメント・
/// ファイル/プレビューで開く・外部変更の読み直し・プレビュー↔通常の昇格）。選択/クローズ/活性化は ShellWindow.Tabs.cs。</summary>
public partial class ShellWindow
{
    private void OnEditorNewTab(object sender, RoutedEventArgs e)
    {
        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, null, false, false);
        ActivateEditorTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// 仮想ドキュメント（システムプロンプト・危険コマンド一覧など）を編集するための専用タブを用意する。
    /// 同名タブが既にあればそれをアクティブ化して再利用し、無ければ新規タブを作成する。
    /// EditorService が <see cref="VimEditorControl.OpenVirtualDocument"/> を呼ぶ直前にこれを呼ぶため、
    /// ここでアクティブ化（＝Attach）した control に対して仮想ドキュメントが開かれる。
    /// </summary>
    private void OpenVirtualDocumentTab(string title)
    {
        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.VirtualTitle, title, StringComparison.Ordinal));
        if (existing is not null)
        {
            ActivateEditorTab(existing.Id);
            return;
        }

        var tab = CreateEditorTab();
        tab.VirtualTitle = title;
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, title, false, false);
        ActivateEditorTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }

    private async Task OpenFileInNewEditorTabAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        // 同一ファイルの重複タブを防ぐためパスを正規化する。Git ペイン等は Path.Combine(root, "a/b")
        // で区切り混在の path（C:\root\a/b）を渡すので、正規化しないとエクスプローラ起点のタブと
        // 文字列一致せず二重に開いてしまう。VimEditorControl は渡した文字列をそのまま FilePath に保持する。
        path = Path.GetFullPath(path);
        RecordRecentFile(path);

        // Editor も EditorSupport も出ていなければ、左上を開く対象（バイナリ＝サポート／他＝Editor）へ切替える。
        EnsureEditorPaneForOpenedFile(path);

        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // 明示的に開いた（ダブルクリック・Enter 等）ので、プレビュー中なら通常タブへ確定する。
            if (ReferenceEquals(_previewEditorTab, existing))
                SetPreviewTab(null);
            ActivateEditorTab(existing.Id);
            // 既に開いているファイルが外部（AI の write_file/edit_file・git・ターミナル等）で書き換わって
            // いれば、ここで読み直して本文と EditorSupport を最新化する（下記ヘルパ参照）。
            await ReloadExistingTabIfChangedAsync(existing);
            return;
        }

        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, path, false, false);
        ActivateEditorTab(tab.Id);
        // 活性化済みタブの control へ直接読み込む。ここで _editor.OpenFileAsync を呼ぶと
        // FileOpenRequested 経由で本メソッドへ再入してしまうため、低レベルの LoadFile を使う。
        tab.Control.LoadFile(path);
        UpdateEditorTab(tab);
        // タブ活性化の時点では FilePath が未確定だったので、読込後に EditorSupport を同期し直す。
        await UpdateEditorSupportAsync();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// FolderTree の単クリックでファイルをプレビュータブ（タイトル斜体）で開く。
    /// 未編集のプレビュータブ（無ければ空の Untitled タブ）を使い回して中身だけ差し替えるので、
    /// クリックのたびにタブが増えない。プレビュータブは編集された時点で通常タブへ昇格する
    /// （<see cref="UpdateEditorTab"/>）。既にタブで開いているファイルはそれをアクティブ化するだけ。
    /// </summary>
    private async Task OpenFileInPreviewTabAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        // 区切り混在のパス（Git 起点等）でも既存タブと一致させるため正規化する（上記参照）。
        path = Path.GetFullPath(path);
        RecordRecentFile(path);

        // Editor も EditorSupport も出ていなければ、左上を開く対象（バイナリ＝サポート／他＝Editor）へ切替える。
        EnsureEditorPaneForOpenedFile(path);

        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateEditorTab(existing.Id);
            // 外部変更があれば読み直して本文と EditorSupport を最新化する（下記ヘルパ参照）。
            await ReloadExistingTabIfChangedAsync(existing);
            return;
        }

        // 差し替え先：未編集のプレビュータブ、無ければアクティブな空の Untitled タブを転用する。
        var target = _previewEditorTab is { } preview && _editorTabs.Contains(preview)
                     && !preview.PeekIsModified && !preview.PeekIsVirtual
            ? preview
            : _activeEditorTab is { } active && _editorTabs.Contains(active)
              && string.IsNullOrEmpty(active.PeekFilePath) && !active.PeekIsModified
              && !active.PeekIsVirtual && active.VirtualTitle is null
                ? active
                : null;

        if (target is null)
        {
            target = CreateEditorTab();
            _editorTabs.Add(target);
            _vm.Tabs.AddEditorTab(target.Id, path, false, false);
        }

        ActivateEditorTab(target.Id);
        // 活性化済みタブの control へ直接読み込む（_editor.OpenFileAsync は再入を招くため使わない）。
        target.Control.LoadFile(path);
        // LoadFile 中の BufferChanged が UpdateEditorTab の昇格判定を誤爆させないよう、読込後に印を付ける。
        SetPreviewTab(target);
        UpdateEditorTab(target);
        await UpdateEditorSupportAsync();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// 既に開いているタブを再オープンしたとき、ファイルが外部（AI の write_file/edit_file・git・
    /// ターミナル等）で書き換わっていれば、未編集に限りディスクから読み直して本文を最新化する。
    /// エディタ内蔵のファイルウォッチャによる自動リロード（VimEditorControl.ReloadCurrentFile）は
    /// <c>BufferChanged</c> を発火しないため、それに依存せず明示的に <c>LoadFile</c> して BufferChanged を
    /// 起こす。さらに EditorSupport の追従元タブなら、同一タブ再活性で
    /// <see cref="SwitchEditorSupportSourceAsync"/> が早期 return する分を補ってプレビューを即更新する。
    /// </summary>
    private async Task ReloadExistingTabIfChangedAsync(EditorTab tab)
    {
        var path = tab.Control.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        // 未保存編集のあるタブは読み直さない（編集を破棄しない）。リロード判断はエディタ側に委ねる。
        if (tab.Control.IsModified)
            return;

        string diskText;
        try { diskText = await File.ReadAllTextAsync(path); }
        catch { return; }   // 読めなければ現状維持（best-effort）

        // 改行コードだけの差では読み直さない（無編集ファイルのスクロール位置を無駄に失わない）。
        if (NormalizeEol(diskText) != NormalizeEol(tab.Control.Text))
        {
            // ディスク内容が違う＝外部変更。LoadFile で本文を最新化する（BufferChanged が発火する）。
            tab.Control.LoadFile(path);
            UpdateEditorTab(tab);
        }

        // 本文が既に最新（ウォッチャが先に読み直した等）でも、外部リロードは BufferChanged を上げず
        // EditorSupport が取り残されるため、追従元タブなら明示的に更新する。
        if (ReferenceEquals(_editorSupportSourceTab, tab))
            await UpdateEditorSupportAsync();
    }

    private static string NormalizeEol(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>プレビュータブの参照とタブUIの斜体表示を同期して切り替える（null で解除＝昇格）。</summary>
    private void SetPreviewTab(EditorTab? tab)
    {
        if (_previewEditorTab is { } old && !ReferenceEquals(old, tab))
            _vm.Tabs.SetEditorTabPreview(old.Id, false);
        _previewEditorTab = tab;
        if (tab is not null)
        {
            MovePreviewEditorTabToEnd();
            _vm.Tabs.SetEditorTabPreview(tab.Id, true);
        }
    }

    private void MovePreviewEditorTabToEnd()
    {
        if (_previewEditorTab is not { } preview)
            return;

        var index = _editorTabs.FindIndex(t => ReferenceEquals(t, preview));
        var last = _editorTabs.Count - 1;
        if (index < 0 || index == last)
            return;

        _editorTabs.RemoveAt(index);
        _editorTabs.Add(preview);
    }
}

