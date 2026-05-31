using System;
using System.IO;
using System.Windows;
using AgentStudio.App.ViewModels;
using AgentStudio.Core.Abstractions;
using AgentStudio.Services;
using Editor.Controls;
using Terminal.Tabs;

namespace AgentStudio.App.Views;

public partial class ShellWindow : Window
{
    private readonly TerminalService _terminal;
    private readonly EditorService _editor;
    private readonly IWorkspaceService _workspace;

    public ShellWindow(
        ShellViewModel vm,
        TerminalService terminal,
        EditorService editor,
        IWorkspaceService workspace)
    {
        InitializeComponent();
        DataContext = vm;
        _terminal = terminal;
        _editor = editor;
        _workspace = workspace;

        var startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // sk0ya コントロールを生成してホストへ配置し、サービスへ結びつける
        var termView = new TerminalTabView("powershell.exe", startDir);
        TerminalHost.Child = termView;
        _terminal.Attach(termView);
        _terminal.SetWorkingDirectory(startDir);

        var editorCtrl = new VimEditorControl();
        EditorHost.Child = editorCtrl;
        _editor.Attach(editorCtrl);

        // フォルダを開いたらエージェントの作業ディレクトリを同期
        _workspace.RootChanged += (_, root) =>
        {
            if (!string.IsNullOrEmpty(root)) _terminal.SetWorkingDirectory(root);
        };

        // ファイル選択でエディタに開く
        _workspace.SelectionChanged += async (_, path) =>
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                await _editor.OpenFileAsync(path);
        };
    }
}
