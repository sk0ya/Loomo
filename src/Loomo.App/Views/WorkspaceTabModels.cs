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
using Terminal.Settings;
using Terminal.Tabs;


namespace sk0ya.Loomo.App.Views;

/// <summary>ワークスペース内のタブ実体モデル（端末／エディタ／ブラウザの各タブと、
/// ワークスペース単位のタブ集合）。エディタタブは起動を速くするため遅延実体化する。</summary>
internal sealed record TerminalTab(Guid Id, TerminalTabView View);
    /// <summary><see cref="VirtualTitle"/> は仮想ドキュメント（設定の長文項目など）を開いたタブの表示名。
    /// 仮想ドキュメントは FilePath を持たないため、タブ名はこの値から決める（通常ファイルは null）。</summary>
    /// <summary>エディタタブ。起動を速くするため <see cref="Control"/>（VimEditorControl の生成＋ファイル
    /// 読込＋Git差分）は<b>初回アクセス時に遅延実体化</b>する。復元直後は <see cref="Pending"/> に保存済み
    /// スナップショットだけを持ち、アクティブ化や本文取得で初めて実体化する。未実体化のままでもタブ strip・
    /// 永続化・パス重複判定が壊れないよう、メタ情報は <see cref="PeekFilePath"/> 等で実体化せずに読める。</summary>
internal sealed record EditorTab(Guid Id)
    {
        private VimEditorControl? _control;

        /// <summary>未実体化タブの実体化処理（コントロール生成→<see cref="SetControl"/>→Pending から本文復元）。
        /// <see cref="Control"/> の初回アクセスで呼ばれる。</summary>
        public Action<EditorTab>? Realizer { get; init; }

        /// <summary>未実体化の間だけ保持する保存済みスナップショット（実体化時に消費して null になる）。</summary>
        public EditorTabSnapshot? Pending { get; set; }

        public string? VirtualTitle { get; set; }

        /// <summary>コントロールが既に実体化済みか（実体化せずに判定）。</summary>
        public bool IsRealized => _control is not null;

        /// <summary>実体化処理の途中でコントロールを確定する。Pending 復元（LoadFile→BufferChanged で
        /// <see cref="Control"/> へ再入する）より<b>前</b>に呼ぶことで無限再帰を防ぐ。</summary>
        public void SetControl(VimEditorControl control) => _control = control;

        /// <summary>コントロール。未実体化なら初回アクセスでここで実体化する。</summary>
        public VimEditorControl Control
        {
            get
            {
                if (_control is null)
                    Realizer!(this);
                return _control!;
            }
        }

        /// <summary>実体化せずに読めるファイルパス（実体化済みなら現値、未実体化なら保存値）。</summary>
        public string? PeekFilePath => _control?.FilePath ?? Pending?.FilePath;
        /// <summary>実体化せずに読める変更フラグ。</summary>
        public bool PeekIsModified => _control?.IsModified ?? Pending?.IsModified ?? false;
        /// <summary>実体化せずに読める仮想ドキュメント判定（未実体化タブは常に実ファイル＝false）。</summary>
        public bool PeekIsVirtual => _control?.IsVirtualDocument ?? false;
    }
internal sealed record BrowserTab(Guid Id, WebView2CompositionControl View)
    {
        /// <summary>まだ CoreWebView2 を生成していない間の遷移先 URL（実体化時にここへナビゲートする）。
        /// 起動を速くするため Browser ペインが見えるまで WebView2 生成を遅らせる。</summary>
        public string? PendingUrl { get; set; }

        /// <summary>CoreWebView2 の生成を開始済みか（多重生成・多重ナビゲートの防止）。</summary>
        public bool RealizationStarted { get; set; }
    }

internal sealed class TerminalWorkspaceTabs
    {
        public List<TerminalTab> Tabs { get; } = new();
        public Guid? ActiveTabId { get; set; }
        public int NextTabNumber { get; set; } = 1;
        public bool IsInitialized { get; set; }
    }

internal sealed class EditorWorkspaceTabs
    {
        public List<EditorTab> Tabs { get; } = new();
        public Guid? ActiveTabId { get; set; }
        public bool IsInitialized { get; set; }
    }

internal sealed class BrowserWorkspaceTabs
    {
        public List<BrowserTab> Tabs { get; } = new();
        public Guid? ActiveTabId { get; set; }
        public int NextTabNumber { get; set; } = 1;
        public bool IsInitialized { get; set; }
}

